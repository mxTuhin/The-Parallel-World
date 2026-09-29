using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParallelWorld.FireGraph
{
    /// <summary>
    /// Time-stepped baseline on the GPU (Resources/FireGraph/SteppedFire.compute), R replicas at once.
    /// Result.Iterations = steps taken per replica.
    /// </summary>
    public sealed class SteppedFireGpu : IDisposable
    {
        readonly FireScenario _s;
        readonly ComputeShader _cs;
        readonly int _replicas;
        readonly int _kInit, _kSeed, _kStep, _kApply, _kFinish;
        readonly GraphicsBuffer[] _all;
        readonly GraphicsBuffer _ftp, _eth, _tIgn, _done, _steps;
        readonly CommandBuffer _cmd = new CommandBuffer { name = "SteppedFire" };

        public SteppedFireGpu(FireScenario s, int replicas, ComputeShader shader = null)
        {
            if (!ExactFireGpu.Supported) throw new NotSupportedException("compute shaders are not supported on this device");
            int groups = (s.NodeCount + ExactFireGpu.ThreadsPerGroup - 1) / ExactFireGpu.ThreadsPerGroup;
            if (groups > ExactFireGpu.MaxGroups) throw new ArgumentException("too many buildings for one dispatch");
            if (replicas < 1 || replicas > ExactFireGpu.MaxGroups) throw new ArgumentOutOfRangeException(nameof(replicas));
            _s = s;
            _replicas = replicas;
            _cs = shader != null ? shader : Resources.Load<ComputeShader>("FireGraph/SteppedFire");
            if (_cs == null) throw new InvalidOperationException("Resources/FireGraph/SteppedFire.compute not found");
            _kInit = _cs.FindKernel("Init");
            _kSeed = _cs.FindKernel("SeedIgnitions");
            _kStep = _cs.FindKernel("Step");
            _kApply = _cs.FindKernel("Apply");
            _kFinish = _cs.FindKernel("Finish");

            int total = s.NodeCount * replicas;
            var off = Upload(s.InOffsets); var src = Upload(s.Src); var a = Upload(s.A); var b = Upload(s.B);
            var tau0 = Upload(s.Tau0); var tg = Upload(s.Tg); var td = Upload(s.Td); var tx = Upload(s.Tx);
            var ign = Upload(s.Ignitions);
            _ftp = New(total); _eth = New(total); _tIgn = New(total);
            var d = New(total); var h = New(total); var frac = New(total); var fire = New(total);
            var lastEnd = New(replicas); _done = New(replicas); _steps = New(replicas);
            _all = new[] { off, src, a, b, tau0, tg, td, tx, ign, _ftp, _eth, _tIgn, d, h, frac, fire, lastEnd, _done, _steps };
            var names = new[] { "_Off", "_Src", "_A", "_B", "_Tau0", "_Tg", "_Td", "_Tx", "_Ign", "_Ftp", "_Eth",
                                "_TIgn", "_D", "_H", "_Frac", "_Fire", "_LastEnd", "_Done", "_Steps" };
            foreach (int k in new[] { _kInit, _kSeed, _kStep, _kApply, _kFinish })
                for (int q = 0; q < names.Length; q++) _cs.SetBuffer(k, names[q], _all[q]);
        }

        static GraphicsBuffer New(int count) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(count, 1), 4);

        static GraphicsBuffer Upload<T>(NativeArray<T> data) where T : struct
        {
            var buf = New(data.Length);
            if (data.Length > 0) buf.SetData(data);
            return buf;
        }

        public ExactFireCpu.Result Run(NativeArray<float> ftp, NativeArray<float> eth, float dt, SteppedVariant variant,
            ulong seed = 0, int firstReplica = 0, int stepsPerSubmit = 256)
        {
            int n = _s.NodeCount, total = n * _replicas;
            if (ftp.Length != total || eth.Length != total) throw new ArgumentException("thresholds must be replicas * N long");
            _ftp.SetData(ftp);
            _eth.SetData(eth);
            _cs.SetInt("_N", n);
            _cs.SetInt("_R", _replicas);
            _cs.SetInt("_NumIgn", _s.Ignitions.Length);
            _cs.SetInt("_Variant", (int)variant);
            _cs.SetFloat("_Dt", dt);
            _cs.SetFloat("_TEnd", _s.TEnd);
            _cs.SetFloat("_QCr", _s.QCr);
            _cs.SetFloat("_NExp", _s.FtpN);
            _cs.SetInt("_SeedLo", unchecked((int)(uint)seed));
            _cs.SetInt("_SeedHi", unchecked((int)(uint)(seed >> 32)));
            _cs.SetInt("_FirstReplica", firstReplica);

            int gx = (n + ExactFireGpu.ThreadsPerGroup - 1) / ExactFireGpu.ThreadsPerGroup;
            int gr = (_replicas + ExactFireGpu.ThreadsPerGroup - 1) / ExactFireGpu.ThreadsPerGroup;
            int gIgn = Math.Max(1, (_s.Ignitions.Length + ExactFireGpu.ThreadsPerGroup - 1) / ExactFireGpu.ThreadsPerGroup);
            _cs.Dispatch(_kInit, gx, _replicas, 1);
            if (_s.Ignitions.Length > 0) _cs.Dispatch(_kSeed, gIgn, _replicas, 1);

            int nSteps = (int)Math.Ceiling(_s.TEnd / dt);
            var done = new uint[_replicas];
            int step = 0;
            while (step < nSteps)
            {
                _cmd.Clear();
                for (int k = 0; k < stepsPerSubmit && step < nSteps; k++, step++)
                {
                    _cmd.SetComputeIntParam(_cs, "_Step", step);
                    _cmd.DispatchCompute(_cs, _kStep, gx, _replicas, 1);
                    _cmd.DispatchCompute(_cs, _kApply, gx, _replicas, 1);
                    _cmd.DispatchCompute(_cs, _kFinish, gr, 1, 1);
                }
                Graphics.ExecuteCommandBuffer(_cmd);
                _done.GetData(done);
                bool all = true;
                for (int r = 0; r < _replicas; r++) all &= done[r] != 0;
                if (all) break;
            }
            var tIgn = new float[total];
            var steps = new int[_replicas];
            _tIgn.GetData(tIgn);
            _steps.GetData(steps);
            return new ExactFireCpu.Result { TIgn = tIgn, Iterations = steps };
        }

        public void Dispose()
        {
            foreach (var b in _all) b?.Release();
            _cmd.Release();
        }
    }
}
