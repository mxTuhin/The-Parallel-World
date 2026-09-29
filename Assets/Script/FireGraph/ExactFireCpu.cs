using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParallelWorld.FireGraph
{
    // All jobs index a flat array of R replicas x N buildings: index = r * N + i.
    // An iteration: Predict (dirty buildings) -> Reduce (earliest prediction per replica)
    // -> Decide (which predictions are safe to commit) -> Apply (commit them).
    // Every step reads only state fixed before the step, so the result is independent
    // of thread count and batch size.

    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    struct PredictJob : IJobParallelFor
    {
        public ScenarioView G;
        [ReadOnly] public NativeArray<float> Ftp, Eth, TIgn;
        [ReadOnly] public NativeArray<int> IgnIter;
        [ReadOnly] public NativeArray<byte> Done;
        public NativeArray<float> Pred;
        public int Iter;

        public void Execute(int idx)
        {
            int r = idx / G.N;
            if (Done[r] != 0 || TIgn[idx] < ExactFireCore.Inf) return;
            int i = idx - r * G.N, row = r * G.N;
            bool dirty = false;
            for (int e = G.Off[i]; e < G.Off[i + 1]; e++)
                if (IgnIter[row + G.Src[e]] == Iter - 1) { dirty = true; break; }
            if (!dirty) return;
            Pred[idx] = ExactFireCore.NodeClock(i, row, G, TIgn, Ftp[idx], Eth[idx], out _);
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    struct ReduceJob : IJobParallelFor
    {
        public int N;
        [ReadOnly] public NativeArray<float> Pred, TIgn;
        public NativeArray<float> TMin;
        public NativeArray<byte> Done;
        public NativeArray<int> Iterations;

        public void Execute(int r)
        {
            if (Done[r] != 0) return;
            float m = ExactFireCore.Inf;
            int row = r * N;
            for (int i = 0; i < N; i++)
                if (!(TIgn[row + i] < ExactFireCore.Inf)) m = math.min(m, Pred[row + i]);
            TMin[r] = m;
            if (m == ExactFireCore.Inf) Done[r] = 1;
            else Iterations[r] = Iterations[r] + 1;
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    struct DecideJob : IJobParallelFor
    {
        public ScenarioView G;
        public CommitRule Rule;
        [ReadOnly] public NativeArray<float> Pred, TIgn, TMin;
        [ReadOnly] public NativeArray<byte> Done;
        [WriteOnly] public NativeArray<byte> Commit;

        public void Execute(int idx)
        {
            int r = idx / G.N;
            Commit[idx] = 0;
            if (Done[r] != 0 || TIgn[idx] < ExactFireCore.Inf) return;
            float p = Pred[idx];
            if (p == ExactFireCore.Inf) return;
            float tmin = TMin[r];
            bool ok;
            if (p == tmin) ok = true;
            else if (Rule == CommitRule.Sequential) ok = false;
            else if (Rule == CommitRule.Global) ok = p < tmin + G.Lookahead;
            else
            {
                int i = idx - r * G.N, row = r * G.N;
                float safe = ExactFireCore.Inf;
                for (int e = G.Off[i]; e < G.Off[i + 1]; e++)
                {
                    int k = G.Src[e];
                    if (!(TIgn[row + k] < ExactFireCore.Inf)) safe = math.min(safe, tmin + G.Tau0[k]);
                }
                ok = p < safe;
            }
            if (ok) Commit[idx] = 1;
        }
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    struct ApplyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Commit;
        public NativeArray<float> Pred, TIgn;
        public NativeArray<int> IgnIter;
        public int Iter;

        public void Execute(int idx)
        {
            if (Commit[idx] == 0) return;
            TIgn[idx] = Pred[idx];
            IgnIter[idx] = Iter;
            Pred[idx] = ExactFireCore.Inf;
        }
    }

    /// <summary>
    /// Exact solver on the CPU (Burst jobs), R replicas at once.
    /// thresholds are flat R*N arrays (see DrawThresholdsJob).
    /// </summary>
    public static class ExactFireCpu
    {
        public struct Result
        {
            public float[] TIgn;        // R*N, inf = not ignited
            public int[] Iterations;    // per replica
        }

        public static Result Run(FireScenario s, NativeArray<float> ftp, NativeArray<float> eth, int replicas,
            CommitRule rule = CommitRule.Local)
        {
            int n = s.NodeCount, total = n * replicas;
            if (ftp.Length != total || eth.Length != total) throw new ArgumentException("thresholds must be replicas * N long");
            var g = ScenarioView.Of(s);
            var tIgn = new NativeArray<float>(total, Allocator.TempJob);
            var pred = new NativeArray<float>(total, Allocator.TempJob);
            var ignIter = new NativeArray<int>(total, Allocator.TempJob);
            var commit = new NativeArray<byte>(total, Allocator.TempJob);
            var tmin = new NativeArray<float>(replicas, Allocator.TempJob);
            var done = new NativeArray<byte>(replicas, Allocator.TempJob);
            var iters = new NativeArray<int>(replicas, Allocator.TempJob);
            try
            {
                for (int k = 0; k < total; k++) { tIgn[k] = ExactFireCore.Inf; pred[k] = ExactFireCore.Inf; ignIter[k] = -1; }
                for (int r = 0; r < replicas; r++)
                    for (int c = 0; c < s.Ignitions.Length; c++)
                    {
                        tIgn[r * n + s.Ignitions[c]] = 0f;
                        ignIter[r * n + s.Ignitions[c]] = 0;
                    }
                const int batch = 64;
                for (int iter = 1; iter <= n + 1; iter++)
                {
                    var h = new PredictJob { G = g, Ftp = ftp, Eth = eth, TIgn = tIgn, IgnIter = ignIter, Done = done, Pred = pred, Iter = iter }
                        .Schedule(total, batch);
                    h = new ReduceJob { N = n, Pred = pred, TIgn = tIgn, TMin = tmin, Done = done, Iterations = iters }
                        .Schedule(replicas, 1, h);
                    h.Complete();
                    bool all = true;
                    for (int r = 0; r < replicas; r++) all &= done[r] != 0;
                    if (all) break;
                    h = new DecideJob { G = g, Rule = rule, Pred = pred, TIgn = tIgn, TMin = tmin, Done = done, Commit = commit }
                        .Schedule(total, batch);
                    h = new ApplyJob { Commit = commit, Pred = pred, TIgn = tIgn, IgnIter = ignIter, Iter = iter }
                        .Schedule(total, batch, h);
                    h.Complete();
                }
                return new Result { TIgn = tIgn.ToArray(), Iterations = iters.ToArray() };
            }
            finally
            {
                tIgn.Dispose(); pred.Dispose(); ignIter.Dispose(); commit.Dispose();
                tmin.Dispose(); done.Dispose(); iters.Dispose();
            }
        }

        /// <summary>Thresholds for replicas [firstReplica, firstReplica + replicas) from the counter-based RNG.</summary>
        public static void DrawThresholds(FireScenario s, ulong seed, int firstReplica, int replicas,
            out NativeArray<float> ftp, out NativeArray<float> eth, Allocator allocator = Allocator.TempJob)
        {
            int total = s.NodeCount * replicas;
            ftp = new NativeArray<float>(total, allocator);
            eth = new NativeArray<float>(total, allocator);
            new DrawThresholdsJob
            {
                Seed = seed, FirstReplica = firstReplica, NodeCount = s.NodeCount,
                FtpMu = s.FtpMu, FtpSigma = s.FtpSigma, Ftp = ftp, Eth = eth,
            }.Schedule(total, 256).Complete();
        }
    }
}
