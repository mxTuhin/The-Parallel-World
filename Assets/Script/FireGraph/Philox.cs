using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParallelWorld.FireGraph
{
    /// <summary>
    /// Philox4x32-10 counter-based RNG (Salmon et al. 2011), bit-identical to
    /// Tools/ffe_pipeline/ffe/sim/rng.py. Counter = (node, stream, replica, extra),
    /// key = (seed low 32 bits, seed high 32 bits).
    /// </summary>
    public static class Philox
    {
        const uint M0 = 0xD2511F53u, M1 = 0xCD9E8D57u;
        const uint W0 = 0x9E3779B9u, W1 = 0xBB67AE85u;

        public const uint StreamFtp = 0, StreamBrand = 1, StreamStep = 2;

        public static uint4 Hash(uint4 ctr, uint2 key)
        {
            for (int r = 0; r < 10; r++)
            {
                ulong p0 = (ulong)M0 * ctr.x;
                ulong p1 = (ulong)M1 * ctr.z;
                uint hi0 = (uint)(p0 >> 32), lo0 = (uint)p0;
                uint hi1 = (uint)(p1 >> 32), lo1 = (uint)p1;
                ctr = new uint4(hi1 ^ ctr.y ^ key.x, lo1, hi0 ^ ctr.w ^ key.y, lo0);
                if (r < 9) key += new uint2(W0, W1);
            }
            return ctr;
        }

        /// <summary>53-bit uniform in the open interval (0, 1).</summary>
        public static double Uniform01(uint x0, uint x1)
        {
            return ((x0 >> 5) * 67108864.0 + (x1 >> 6) + 0.5) * (1.0 / 9007199254740992.0);
        }

        public static double UniformAt(ulong seed, uint replica, uint node, uint stream, uint extra)
        {
            uint4 h = Hash(new uint4(node, stream, replica, extra), new uint2((uint)seed, (uint)(seed >> 32)));
            return Uniform01(h.x, h.y);
        }

        /// <summary>Inverse standard normal CDF (Acklam; relative error below 1.15e-9).</summary>
        public static double NormPpf(double p)
        {
            const double a1 = -3.969683028665376e+01, a2 = 2.209460984245205e+02, a3 = -2.759285104469687e+02;
            const double a4 = 1.383577518672690e+02, a5 = -3.066479806614716e+01, a6 = 2.506628277459239e+00;
            const double b1 = -5.447609879822406e+01, b2 = 1.615858368580409e+02, b3 = -1.556989798598866e+02;
            const double b4 = 6.680131188771972e+01, b5 = -1.328068155288572e+01;
            const double c1 = -7.784894002430293e-03, c2 = -3.223964580411365e-01, c3 = -2.400758277161838e+00;
            const double c4 = -2.549732539343734e+00, c5 = 4.374664141464968e+00, c6 = 2.938163982698783e+00;
            const double d1 = 7.784695709041462e-03, d2 = 3.224671290700398e-01, d3 = 2.445134137142996e+00, d4 = 3.754408661907416e+00;
            const double plow = 0.02425;
            if (p < plow)
            {
                double q = math.sqrt(-2.0 * math.log(p));
                return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
            }
            if (p > 1.0 - plow)
            {
                double q = math.sqrt(-2.0 * math.log(1.0 - p));
                return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
            }
            {
                double q = p - 0.5;
                double r = q * q;
                return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q / (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
            }
        }

        /// <summary>Radiation (FTP) and firebrand (Exp(1)) thresholds of one building in one run.</summary>
        public static void Thresholds(ulong seed, uint replica, uint node, double ftpMu, double ftpSigma,
            out float ftp, out float eth)
        {
            double z = NormPpf(UniformAt(seed, replica, node, StreamFtp, 0));
            ftp = (float)math.exp(ftpMu + ftpSigma * z);
            eth = (float)(-math.log(UniformAt(seed, replica, node, StreamBrand, 0)));
        }
    }

    /// <summary>Fill thresholds for R replicas (index = replica * N + node).</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    public struct DrawThresholdsJob : IJobParallelFor
    {
        public ulong Seed;
        public int FirstReplica;
        public int NodeCount;
        public double FtpMu, FtpSigma;
        [WriteOnly] public NativeArray<float> Ftp;
        [WriteOnly] public NativeArray<float> Eth;

        public void Execute(int index)
        {
            int r = index / NodeCount;
            int i = index - r * NodeCount;
            Philox.Thresholds(Seed, (uint)(FirstReplica + r), (uint)i, FtpMu, FtpSigma, out float f, out float e);
            Ftp[index] = f;
            Eth[index] = e;
        }
    }
}
