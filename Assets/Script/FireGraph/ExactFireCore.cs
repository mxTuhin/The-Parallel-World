using Unity.Collections;
using Unity.Mathematics;

namespace ParallelWorld.FireGraph
{
    /// <summary>Which ignitions an iteration of the exact solver may commit.</summary>
    public enum CommitRule
    {
        /// <summary>Only the earliest prediction (classic next-event simulation; the reference).</summary>
        Sequential = 0,
        /// <summary>Every prediction earlier than t_min + L, with L = min tau0.</summary>
        Global = 1,
        /// <summary>Prediction p_i earlier than t_min + min tau0 over i's unburned in-neighbours.</summary>
        Local = 2,
    }

    /// <summary>Read-only scenario arrays passed into jobs.</summary>
    public struct ScenarioView
    {
        [ReadOnly] public NativeArray<int> Off;
        [ReadOnly] public NativeArray<int> Src;
        [ReadOnly] public NativeArray<float> A;
        [ReadOnly] public NativeArray<float> B;
        [ReadOnly] public NativeArray<float> Tau0;
        [ReadOnly] public NativeArray<float> Tg;
        [ReadOnly] public NativeArray<float> Td;
        [ReadOnly] public NativeArray<float> Tx;
        public int N;
        public float QCr;
        public float NExp;
        public float Horizon;
        public float Lookahead;

        public static ScenarioView Of(FireScenario s) => new ScenarioView
        {
            Off = s.InOffsets, Src = s.Src, A = s.A, B = s.B,
            Tau0 = s.Tau0, Tg = s.Tg, Td = s.Td, Tx = s.Tx,
            N = s.NodeCount, QCr = s.QCr, NExp = s.FtpN, Horizon = s.TEnd, Lookahead = s.Lookahead,
        };
    }

    /// <summary>
    /// The exact per-building clock, line for line the same algorithm as
    /// node_clock() in Tools/ffe_pipeline/ffe/sim/exact.py and NodeClock() in
    /// Resources/FireGraph/ExactFire.compute. Keep all three in sync.
    ///
    /// The heat flux on building i is piecewise linear between the breakpoints of
    /// its burning neighbours' intensity curves. Segments are visited in time
    /// order by a minimum scan (no sort, no scratch memory, so it runs unchanged on
    /// the GPU). Each segment is described by its start value and slope only,
    /// which keeps the crossing time bit-identical when a later neighbour's
    /// breakpoint splits the segment.
    /// </summary>
    public static class ExactFireCore
    {
        public const float Inf = float.PositiveInfinity;

        public static float Phi(float tau, float t0, float tg, float td, float tx)
        {
            if (tau <= t0) return 0f;
            float s = tau - t0;
            if (s < tg) return s / tg;
            s -= tg;
            if (s < td) return 1f;
            s -= td;
            if (s < tx) return 1f - s / tx;
            return 0f;
        }

        public static float DPhi(float tauMid, float t0, float tg, float td, float tx)
        {
            if (tauMid <= t0) return 0f;
            float s = tauMid - t0;
            if (s < tg) return 1f / tg;
            s -= tg;
            if (s < td) return 0f;
            s -= td;
            if (s < tx) return -1f / tx;
            return 0f;
        }

        public static float LinCross(float r0, float m, float rem)
        {
            float disc = r0 * r0 + 2f * m * rem;
            if (disc < 0f) disc = 0f;
            float den = r0 + math.sqrt(disc);
            if (den <= 0f) return Inf;
            return 2f * rem / den;
        }

        public static float PosGain(float rs, float m, float T, float n)
        {
            if (n == 1f) return (rs + 0.5f * m * T) * T;
            if (m == 0f) return math.pow(rs, n) * T;
            float re = rs + m * T;
            if (re < 0f) re = 0f;
            return (math.pow(re, n + 1f) - math.pow(rs, n + 1f)) / ((n + 1f) * m);
        }

        public static float PosCross(float rs, float m, float n, float rem)
        {
            if (n == 1f) return LinCross(rs, m, rem);
            if (m == 0f) return rem / math.pow(rs, n);
            float val = math.pow(rs, n + 1f) + rem * m * (n + 1f);
            if (val < 0f) val = 0f;
            return (math.pow(val, 1f / (n + 1f)) - rs) / m;
        }

        /// <summary>
        /// Next ignition time of building i (inf if none before the horizon) given the
        /// ignition times of replica row `row` (tIgn[row + j], inf = not ignited).
        /// progress = max(dose / FTP, hazard / E) at the horizon (1 when it ignites).
        /// </summary>
        public static float NodeClock(int i, int row, in ScenarioView g, NativeArray<float> tIgn,
            float ftp, float eth, out float progress)
        {
            int e0 = g.Off[i], e1 = g.Off[i + 1];
            float t0 = Inf;
            for (int e = e0; e < e1; e++)
            {
                int j = g.Src[e];
                float tj = tIgn[row + j];
                if (tj < Inf) t0 = math.min(t0, tj + g.Tau0[j]);
            }
            progress = 0f;
            if (t0 == Inf) return Inf;

            float D = 0f, H = 0f;
            while (t0 < g.Horizon)
            {
                float t1 = Inf;
                for (int e = e0; e < e1; e++)
                {
                    int j = g.Src[e];
                    float tj = tIgn[row + j];
                    if (!(tj < Inf)) continue;
                    float s0 = tj + g.Tau0[j];
                    float s1 = s0 + g.Tg[j];
                    float s2 = s1 + g.Td[j];
                    float s3 = s2 + g.Tx[j];
                    if (s0 > t0 && s0 < t1) t1 = s0;
                    if (s1 > t0 && s1 < t1) t1 = s1;
                    if (s2 > t0 && s2 < t1) t1 = s2;
                    if (s3 > t0 && s3 < t1) t1 = s3;
                }
                if (t1 == Inf) break;
                float t1c = t1 > g.Horizon ? g.Horizon : t1;
                if (t1c > t0)
                {
                    float tm = 0.5f * (t0 + t1c);
                    float q0 = 0f, sq = 0f, l0 = 0f, sl = 0f;
                    for (int e = e0; e < e1; e++)
                    {
                        int j = g.Src[e];
                        float tj = tIgn[row + j];
                        if (!(tj < Inf)) continue;
                        float f0 = Phi(t0 - tj, g.Tau0[j], g.Tg[j], g.Td[j], g.Tx[j]);
                        float gr = DPhi(tm - tj, g.Tau0[j], g.Tg[j], g.Td[j], g.Tx[j]);
                        q0 += g.A[e] * f0;
                        sq += g.A[e] * gr;
                        l0 += g.B[e] * f0;
                        sl += g.B[e] * gr;
                    }
                    float T = t1c - t0;
                    float tc = Inf;
                    float gH = (l0 + 0.5f * sl * T) * T;
                    if (gH > 0f && H + gH >= eth) tc = t0 + LinCross(l0, sl, eth - H);
                    float r0 = q0 - g.QCr;
                    float r1 = r0 + sq * T;
                    if (r0 > 0f || r1 > 0f)
                    {
                        float ts = t0, rs = r0, te = t1c;
                        if (r0 <= 0f) { ts = t0 + (-r0) / sq; rs = 0f; }
                        else if (r1 < 0f) { te = t0 + r0 / (-sq); }
                        if (te > ts)
                        {
                            float gD = PosGain(rs, sq, te - ts, g.NExp);
                            if (gD > 0f && D + gD >= ftp)
                            {
                                float tr = ts + PosCross(rs, sq, g.NExp, ftp - D);
                                if (tr < tc) tc = tr;
                            }
                            D += gD;
                        }
                    }
                    H += gH;
                    if (tc < Inf)
                    {
                        if (tc < t0) tc = t0;
                        if (tc > t1c) tc = t1c;
                        progress = 1f;
                        return tc >= g.Horizon ? Inf : tc;
                    }
                }
                t0 = t1;
            }
            progress = math.max(D / ftp, H / eth);
            return Inf;
        }
    }
}
