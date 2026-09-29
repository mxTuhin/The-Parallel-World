using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParallelWorld.FireGraph
{
    /// <summary>Conventional time-stepped update rules (the RQ1/RQ2 baselines).</summary>
    public enum SteppedVariant
    {
        /// <summary>Explicit Euler dose; ignite at the end of the step; firebrands share the exact Exp(1) thresholds.</summary>
        EndHazard = 0,
        /// <summary>As EndHazard, but the ignition time is interpolated inside the step.</summary>
        InterpHazard = 1,
        /// <summary>Explicit Euler dose; one Bernoulli firebrand draw per step, p = 1 - exp(-lambda dt) (usual practice).</summary>
        EndBernoulli = 2,
    }

    /// <summary>
    /// One synchronous step for one building, the same rule as run_stepped() in
    /// Tools/ffe_pipeline/ffe/sim/exact.py and SteppedFire.compute.
    /// </summary>
    public static class SteppedFireCore
    {
        public static bool StepNode(int i, int row, in ScenarioView g, NativeArray<float> tIgn, float t, float dt,
            float ftp, float eth, ref float D, ref float H, SteppedVariant variant, ulong seed, uint replica, int step,
            out float frac)
        {
            float q = 0f, lam = 0f;
            for (int e = g.Off[i]; e < g.Off[i + 1]; e++)
            {
                int j = g.Src[e];
                float tj = tIgn[row + j];
                if (tj < ExactFireCore.Inf && tj <= t)
                {
                    float f = ExactFireCore.Phi(t - tj, g.Tau0[j], g.Tg[j], g.Td[j], g.Tx[j]);
                    q += g.A[e] * f;
                    lam += g.B[e] * f;
                }
            }
            frac = 1f;
            bool fire = false;
            float r = q - g.QCr;
            if (r > 0f)
            {
                float gain = (g.NExp == 1f ? r : math.pow(r, g.NExp)) * dt;
                if (D + gain >= ftp) { fire = true; frac = (ftp - D) / gain; }
                D += gain;
            }
            if (variant == SteppedVariant.EndBernoulli)
            {
                if (lam > 0f)
                {
                    double p = 1.0 - math.exp(-(double)lam * dt);
                    double u = Philox.UniformAt(seed, replica, (uint)i, Philox.StreamStep, (uint)step);
                    if (u < p)
                    {
                        if (!fire) frac = (float)(u / p);
                        fire = true;
                    }
                }
            }
            else
            {
                float gh = lam * dt;
                if (gh > 0f && H + gh >= eth)
                {
                    float fh = (eth - H) / gh;
                    if (!fire || fh < frac) frac = fh;
                    fire = true;
                }
                H += gh;
            }
            return fire;
        }

        public static float BurnEnd(in ScenarioView g, int j, float tIgn) => tIgn + g.Tau0[j] + g.Tg[j] + g.Td[j] + g.Tx[j];
    }

    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    struct StepJob : IJobParallelFor
    {
        public ScenarioView G;
        [ReadOnly] public NativeArray<float> Ftp, Eth, TIgn, LastEnd;
        [ReadOnly] public NativeArray<byte> Done;
        public NativeArray<float> D, H, Frac;
        [WriteOnly] public NativeArray<byte> Fire;
        public float T, Dt;
        public int Step;
        public SteppedVariant Variant;
        public ulong Seed;
        public int FirstReplica;

        public void Execute(int idx)
        {
            int r = idx / G.N;
            Fire[idx] = 0;
            if (Done[r] != 0 || TIgn[idx] < ExactFireCore.Inf || T >= LastEnd[r]) return;
            int i = idx - r * G.N;
            float d = D[idx], h = H[idx];
            bool fire = SteppedFireCore.StepNode(i, r * G.N, G, TIgn, T, Dt, Ftp[idx], Eth[idx], ref d, ref h,
                Variant, Seed, (uint)(FirstReplica + r), Step, out float frac);
            D[idx] = d;
            H[idx] = h;
            if (fire) { Fire[idx] = 1; Frac[idx] = frac; }
        }
    }

    /// <summary>Per replica: apply this step's ignitions, update the burn-out horizon, decide whether to stop.</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    struct StepApplyJob : IJobParallelFor
    {
        public ScenarioView G;
        [ReadOnly] public NativeArray<byte> Fire;
        [ReadOnly] public NativeArray<float> Frac;
        [NativeDisableParallelForRestriction] public NativeArray<float> TIgn;
        public NativeArray<float> LastEnd;
        public NativeArray<byte> Done;
        public NativeArray<int> Steps;
        public float T, Dt, TEnd;
        public bool Interpolate;

        public void Execute(int r)
        {
            if (Done[r] != 0) return;
            int row = r * G.N;
            float last = LastEnd[r];
            if (T < last) Steps[r] = Steps[r] + 1;
            for (int i = 0; i < G.N; i++)
            {
                if (Fire[row + i] == 0) continue;
                float ti = Interpolate ? T + Frac[row + i] * Dt : T + Dt;
                TIgn[row + i] = ti;
                last = math.max(last, SteppedFireCore.BurnEnd(G, i, ti));
            }
            LastEnd[r] = last;
            float next = T + Dt;
            if (next >= last || next >= TEnd) Done[r] = 1;
        }
    }

    /// <summary>Time-stepped baseline on the CPU (Burst), R replicas at once. Result.Iterations = steps taken.</summary>
    public static class SteppedFireCpu
    {
        public static ExactFireCpu.Result Run(FireScenario s, NativeArray<float> ftp, NativeArray<float> eth, int replicas,
            float dt, SteppedVariant variant, ulong seed = 0, int firstReplica = 0)
        {
            int n = s.NodeCount, total = n * replicas;
            if (ftp.Length != total || eth.Length != total) throw new ArgumentException("thresholds must be replicas * N long");
            if (!(dt > 0f)) throw new ArgumentOutOfRangeException(nameof(dt));
            var g = ScenarioView.Of(s);
            var tIgn = new NativeArray<float>(total, Allocator.TempJob);
            var d = new NativeArray<float>(total, Allocator.TempJob);
            var h = new NativeArray<float>(total, Allocator.TempJob);
            var frac = new NativeArray<float>(total, Allocator.TempJob);
            var fire = new NativeArray<byte>(total, Allocator.TempJob);
            var lastEnd = new NativeArray<float>(replicas, Allocator.TempJob);
            var done = new NativeArray<byte>(replicas, Allocator.TempJob);
            var steps = new NativeArray<int>(replicas, Allocator.TempJob);
            try
            {
                for (int k = 0; k < total; k++) tIgn[k] = ExactFireCore.Inf;
                for (int r = 0; r < replicas; r++)
                {
                    float last = 0f;
                    for (int c = 0; c < s.Ignitions.Length; c++)
                    {
                        int i = s.Ignitions[c];
                        tIgn[r * n + i] = 0f;
                        last = math.max(last, SteppedFireCore.BurnEnd(g, i, 0f));
                    }
                    lastEnd[r] = last;
                }
                int nSteps = (int)math.ceil(s.TEnd / dt);
                for (int step = 0; step < nSteps; step++)
                {
                    float t = step * dt;
                    var hnd = new StepJob
                    {
                        G = g, Ftp = ftp, Eth = eth, TIgn = tIgn, LastEnd = lastEnd, Done = done, D = d, H = h, Frac = frac,
                        Fire = fire, T = t, Dt = dt, Step = step, Variant = variant, Seed = seed, FirstReplica = firstReplica,
                    }.Schedule(total, 64);
                    hnd = new StepApplyJob
                    {
                        G = g, Fire = fire, Frac = frac, TIgn = tIgn, LastEnd = lastEnd, Done = done, Steps = steps,
                        T = t, Dt = dt, TEnd = s.TEnd, Interpolate = variant == SteppedVariant.InterpHazard,
                    }.Schedule(replicas, 1, hnd);
                    hnd.Complete();
                    bool all = true;
                    for (int r = 0; r < replicas; r++) all &= done[r] != 0;
                    if (all) break;
                }
                return new ExactFireCpu.Result { TIgn = tIgn.ToArray(), Iterations = steps.ToArray() };
            }
            finally
            {
                tIgn.Dispose(); d.Dispose(); h.Dispose(); frac.Dispose(); fire.Dispose();
                lastEnd.Dispose(); done.Dispose(); steps.Dispose();
            }
        }
    }
}
