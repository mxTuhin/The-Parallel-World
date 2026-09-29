using System;
using Unity.Collections;
using UnityEngine;

namespace ParallelWorld.FireGraph
{
#pragma warning disable 0649   // fields are filled by JsonUtility
    [Serializable]
    class FfesManifest
    {
        public string format;
        public int version;
        public int n_nodes;
        public int n_edges;
        public double q_cr;
        public double ftp_n;
        public double ftp_mu;
        public double ftp_sigma;
        public double t_end_s;
        public double lookahead_s;
        public long ref_seed = -1;
        public int ref_replica = -1;
    }
#pragma warning restore 0649

    /// <summary>
    /// A compiled fire-spread scenario (.ffes, written by `python -m ffe sim compile`).
    /// Edges are CSR over incoming edges: the heat reaching building i comes from
    /// Src[InOffsets[i] .. InOffsets[i+1]).
    /// </summary>
    public sealed class FireScenario : IDisposable
    {
        public int NodeCount;
        public int EdgeCount;
        public float QCr;
        public float FtpN;
        public double FtpMu;
        public double FtpSigma;
        public float TEnd;
        public float Lookahead;
        public long RefSeed = -1;
        public int RefReplica = -1;

        public NativeArray<int> InOffsets;
        public NativeArray<int> Src;
        public NativeArray<float> A;      // kW/m2 at full intensity
        public NativeArray<float> B;      // firebrand ignitions per s at full intensity
        public NativeArray<float> Tau0, Tg, Td, Tx;
        public NativeArray<float> X, Y, Area, Height;
        public NativeArray<int> Ignitions;
        public NativeArray<float> RefFtp, RefEth;   // present when HasRefThresholds

        public bool HasRefThresholds => RefFtp.IsCreated;
        public string SourcePath;

        public static FireScenario Load(string path, Allocator allocator = Allocator.Persistent)
        {
            var c = FfeContainer.Load(path, "FFES");
            var m = JsonUtility.FromJson<FfesManifest>(c.ManifestJson);
            var s = new FireScenario
            {
                SourcePath = path,
                NodeCount = m.n_nodes,
                EdgeCount = m.n_edges,
                QCr = (float)m.q_cr,
                FtpN = (float)m.ftp_n,
                FtpMu = m.ftp_mu,
                FtpSigma = m.ftp_sigma,
                TEnd = (float)m.t_end_s,
                Lookahead = (float)m.lookahead_s,
                RefSeed = m.ref_seed,
                RefReplica = m.ref_replica,
                InOffsets = c.Read<int>("in_offsets", allocator),
                Src = c.Read<int>("src", allocator),
                A = c.Read<float>("a_kw_m2", allocator),
                B = c.Read<float>("b_per_s", allocator),
                Tau0 = c.Read<float>("tau0_s", allocator),
                Tg = c.Read<float>("tg_s", allocator),
                Td = c.Read<float>("td_s", allocator),
                Tx = c.Read<float>("tx_s", allocator),
                X = c.Read<float>("x_m", allocator),
                Y = c.Read<float>("y_m", allocator),
                Area = c.Read<float>("area_m2", allocator),
                Height = c.Read<float>("height_m", allocator),
                Ignitions = c.Read<int>("ignitions", allocator),
            };
            if (c.Has("ftp") && c.Has("eth"))
            {
                s.RefFtp = c.Read<float>("ftp", allocator);
                s.RefEth = c.Read<float>("eth", allocator);
            }
            s.Validate();
            return s;
        }

        /// <summary>Build a scenario from managed arrays (tests, procedural scenes).</summary>
        public static FireScenario FromArrays(int[] inOffsets, int[] src, float[] a, float[] b,
            float[] tau0, float[] tg, float[] td, float[] tx, int[] ignitions,
            float qCr, float ftpN, double ftpMu, double ftpSigma, float tEnd,
            Allocator allocator = Allocator.Persistent)
        {
            int n = inOffsets.Length - 1;
            var zeros = new float[n];
            var s = new FireScenario
            {
                NodeCount = n,
                EdgeCount = src.Length,
                QCr = qCr, FtpN = ftpN, FtpMu = ftpMu, FtpSigma = ftpSigma, TEnd = tEnd,
                InOffsets = new NativeArray<int>(inOffsets, allocator),
                Src = new NativeArray<int>(src, allocator),
                A = new NativeArray<float>(a, allocator),
                B = new NativeArray<float>(b, allocator),
                Tau0 = new NativeArray<float>(tau0, allocator),
                Tg = new NativeArray<float>(tg, allocator),
                Td = new NativeArray<float>(td, allocator),
                Tx = new NativeArray<float>(tx, allocator),
                X = new NativeArray<float>(zeros, allocator),
                Y = new NativeArray<float>(zeros, allocator),
                Area = new NativeArray<float>(zeros, allocator),
                Height = new NativeArray<float>(zeros, allocator),
                Ignitions = new NativeArray<int>(ignitions, allocator),
            };
            float l = float.PositiveInfinity;
            for (int i = 0; i < n; i++) l = Mathf.Min(l, tau0[i]);
            s.Lookahead = n > 0 ? l : 0f;
            s.Validate();
            return s;
        }

        void Validate()
        {
            if (InOffsets.Length != NodeCount + 1) throw new InvalidOperationException("in_offsets length != n_nodes + 1");
            if (InOffsets[NodeCount] != EdgeCount || Src.Length != EdgeCount || A.Length != EdgeCount || B.Length != EdgeCount)
                throw new InvalidOperationException("edge array lengths disagree with in_offsets");
            for (int e = 0; e < EdgeCount; e++)
                if ((uint)Src[e] >= (uint)NodeCount) throw new InvalidOperationException($"edge {e}: src out of range");
            for (int k = 0; k < Ignitions.Length; k++)
                if ((uint)Ignitions[k] >= (uint)NodeCount) throw new InvalidOperationException("ignition index out of range");
            for (int i = 0; i < NodeCount; i++)
                if (!(Tau0[i] >= 0f) || !(Tg[i] > 0f) || !(Td[i] >= 0f) || !(Tx[i] > 0f))
                    throw new InvalidOperationException($"node {i}: burning profile needs tau0 >= 0, tg > 0, td >= 0, tx > 0");
        }

        public int MaxInDegree()
        {
            int m = 0;
            for (int i = 0; i < NodeCount; i++) m = Math.Max(m, InOffsets[i + 1] - InOffsets[i]);
            return m;
        }

        static void Free<T>(ref NativeArray<T> a) where T : struct
        {
            if (a.IsCreated) a.Dispose();
            a = default(NativeArray<T>);
        }

        public void Dispose()
        {
            Free(ref InOffsets); Free(ref Src); Free(ref A); Free(ref B);
            Free(ref Tau0); Free(ref Tg); Free(ref Td); Free(ref Tx);
            Free(ref X); Free(ref Y); Free(ref Area); Free(ref Height);
            Free(ref Ignitions); Free(ref RefFtp); Free(ref RefEth);
        }
    }
}
