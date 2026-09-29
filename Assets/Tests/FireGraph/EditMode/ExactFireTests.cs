using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ParallelWorld.FireGraph.Tests
{
    /// <summary>
    /// EditMode tests for the exact fire solver. Fixtures/small.ffes and small.ref_tign.f64 are
    /// written by `python -m ffe sim fixture` (300 synthetic buildings, reference thresholds and
    /// the Python float64 result).
    /// </summary>
    public class ExactFireTests
    {
        static string Fixture(string name) => Path.Combine(Application.dataPath, "Tests", "FireGraph", "Fixtures", name);

        static double[] ReadF64(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            var d = new double[raw.Length / 8];
            Buffer.BlockCopy(raw, 0, d, 0, d.Length * 8);
            return d;
        }

        /// <summary>Row of n buildings, edge k-1 -> k, identical profiles (see test_sim.py::chain).</summary>
        static FireScenario Chain(int n, float a = 30f, float b = 0f)
        {
            var off = new int[n + 1];
            var src = new int[n - 1];
            for (int k = 1; k < n; k++) { off[k + 1] = k; src[k - 1] = k - 1; }
            off[1] = 0;
            var aa = new float[n - 1];
            var bb = new float[n - 1];
            for (int e = 0; e < n - 1; e++) { aa[e] = a; bb[e] = b; }
            return FireScenario.FromArrays(off, src, aa, bb, Fill(n, 300f), Fill(n, 300f), Fill(n, 1800f), Fill(n, 1200f),
                new[] { 0 }, 10f, 1f, 0.0, 0.0, 1e7f, Allocator.Persistent);
        }

        static float[] Fill(int n, float v)
        {
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = v;
            return x;
        }

        static ExactFireCpu.Result RunFixed(FireScenario s, float ftp, float eth, CommitRule rule)
        {
            var f = new NativeArray<float>(s.NodeCount, Allocator.TempJob);
            var e = new NativeArray<float>(s.NodeCount, Allocator.TempJob);
            for (int i = 0; i < s.NodeCount; i++) { f[i] = ftp; e[i] = eth; }
            try { return ExactFireCpu.Run(s, f, e, 1, rule); }
            finally { f.Dispose(); e.Dispose(); }
        }

        [Test]
        public void PhiloxKnownAnswers()
        {
            Assert.AreEqual(new uint4(0x6627E8D5, 0xE169C58D, 0xBC57AC4C, 0x9B00DBD8), Philox.Hash(uint4.zero, uint2.zero));
            Assert.AreEqual(new uint4(0x408F276D, 0x41C83B0E, 0xA20BC7C6, 0x6D5451FD),
                Philox.Hash(new uint4(uint.MaxValue), new uint2(uint.MaxValue)));
            Assert.AreEqual(new uint4(0xD16CFE09, 0x94FDCCEB, 0x5001E420, 0x24126EA1),
                Philox.Hash(new uint4(0x243F6A88, 0x85A308D3, 0x13198A2E, 0x03707344), new uint2(0xA4093822, 0x299F31D0)));
        }

        [Test]
        public void ThresholdsMatchPython()
        {
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                Assert.IsTrue(s.HasRefThresholds);
                for (int i = 0; i < s.NodeCount; i++)
                {
                    Philox.Thresholds((ulong)s.RefSeed, (uint)s.RefReplica, (uint)i, s.FtpMu, s.FtpSigma, out float f, out float e);
                    Assert.AreEqual(s.RefFtp[i], f, 1e-6f * s.RefFtp[i], $"FTP of building {i}");
                    Assert.AreEqual(s.RefEth[i], e, 1e-6f * math.max(s.RefEth[i], 1e-3f), $"E of building {i}");
                }
            }
        }

        [Test]
        public void TwoBuildingsClosedForm()
        {
            // Dose in growth 2000, remaining 8000 at 20 kW/m2 -> ignition at 300 + 300 + 400 = 1000 s.
            using (var s = Chain(2))
            {
                var r = RunFixed(s, 1e4f, float.PositiveInfinity, CommitRule.Local);
                Assert.AreEqual(1000f, r.TIgn[1], 1e-3f);
            }
        }

        [Test]
        public void ChainIsArithmetic()
        {
            using (var s = Chain(12))
            {
                var r = RunFixed(s, 1e4f, float.PositiveInfinity, CommitRule.Global);
                for (int k = 0; k < 12; k++) Assert.AreEqual(1000f * k, r.TIgn[k], 1e-2f, $"building {k}");
            }
        }

        [Test]
        public void FirebrandClockClosedForm()
        {
            using (var s = Chain(2, a: 0f, b: 1e-3f))
            {
                var r = RunFixed(s, float.PositiveInfinity, 0.5f, CommitRule.Local);
                Assert.AreEqual(300f + 300f + (0.5f - 1e-3f * 150f) / 1e-3f, r.TIgn[1], 1e-2f);
            }
        }

        [Test]
        public void CommitRulesAreBitIdentical()
        {
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                const int R = 8;
                ExactFireCpu.DrawThresholds(s, 123, 0, R, out var ftp, out var eth);
                try
                {
                    var seq = ExactFireCpu.Run(s, ftp, eth, R, CommitRule.Sequential);
                    foreach (var rule in new[] { CommitRule.Global, CommitRule.Local })
                    {
                        var res = ExactFireCpu.Run(s, ftp, eth, R, rule);
                        for (int k = 0; k < seq.TIgn.Length; k++)
                            Assert.AreEqual(BitConverter.SingleToInt32Bits(seq.TIgn[k]), BitConverter.SingleToInt32Bits(res.TIgn[k]), $"{rule} index {k}");
                        for (int r = 0; r < R; r++) Assert.LessOrEqual(res.Iterations[r], seq.Iterations[r]);
                    }
                }
                finally { ftp.Dispose(); eth.Dispose(); }
            }
        }

        [Test]
        public void ReplicaBatchingDoesNotChangeResults()
        {
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                ExactFireCpu.DrawThresholds(s, 77, 0, 4, out var ftp, out var eth);
                try
                {
                    var all = ExactFireCpu.Run(s, ftp, eth, 4);
                    int n = s.NodeCount;
                    for (int r = 0; r < 4; r++)
                    {
                        using (var f1 = new NativeArray<float>(ftp.GetSubArray(r * n, n), Allocator.TempJob))
                        using (var e1 = new NativeArray<float>(eth.GetSubArray(r * n, n), Allocator.TempJob))
                        {
                            var one = ExactFireCpu.Run(s, f1, e1, 1);
                            for (int i = 0; i < n; i++)
                                Assert.AreEqual(BitConverter.SingleToInt32Bits(all.TIgn[r * n + i]), BitConverter.SingleToInt32Bits(one.TIgn[i]));
                        }
                    }
                }
                finally { ftp.Dispose(); eth.Dispose(); }
            }
        }

        [Test]
        public void CpuMatchesPythonReference()
        {
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                double[] py = ReadF64(Fixture("small.ref_tign.f64"));
                var ftp = new NativeArray<float>(s.RefFtp, Allocator.TempJob);
                var eth = new NativeArray<float>(s.RefEth, Allocator.TempJob);
                try
                {
                    var r = ExactFireCpu.Run(s, ftp, eth, 1, CommitRule.Local);
                    int mismatch = 0, burned = 0;
                    for (int i = 0; i < s.NodeCount; i++)
                    {
                        bool a = !double.IsInfinity(py[i]), b = !float.IsInfinity(r.TIgn[i]);
                        if (a) burned++;
                        if (a != b) { mismatch++; continue; }
                        if (a && py[i] > 0) Assert.AreEqual(py[i], r.TIgn[i], 2e-3 * py[i], $"building {i}");
                    }
                    Assert.Greater(burned, 50, "fixture should produce a spreading fire");
                    Assert.LessOrEqual(mismatch, 1, "burned/unburned outcome differs from Python");
                }
                finally { ftp.Dispose(); eth.Dispose(); }
            }
        }

        [Test]
        public void GpuMatchesCpu()
        {
            if (!ExactFireGpu.Supported) Assert.Ignore("compute shaders not supported on this device");
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                const int R = 16;
                ExactFireCpu.DrawThresholds(s, 5, 0, R, out var ftp, out var eth);
                try
                {
                    var cpu = ExactFireCpu.Run(s, ftp, eth, R, CommitRule.Local);
                    using (var gpu = new ExactFireGpu(s, R))
                    {
                        var g = gpu.Run(ftp, eth, CommitRule.Local);
                        var g2 = gpu.Run(ftp, eth, CommitRule.Sequential);
                        int mismatch = 0;
                        for (int k = 0; k < cpu.TIgn.Length; k++)
                        {
                            Assert.AreEqual(BitConverter.SingleToInt32Bits(g.TIgn[k]), BitConverter.SingleToInt32Bits(g2.TIgn[k]),
                                "GPU commit rules must agree bit for bit");
                            bool a = !float.IsInfinity(cpu.TIgn[k]), b = !float.IsInfinity(g.TIgn[k]);
                            if (a != b) { mismatch++; continue; }
                            if (a && cpu.TIgn[k] > 0) Assert.AreEqual(cpu.TIgn[k], g.TIgn[k], 2e-3f * cpu.TIgn[k]);
                        }
                        Assert.LessOrEqual(mismatch, R, "outcomes may differ only at float32 near-ties");
                    }
                }
                finally { ftp.Dispose(); eth.Dispose(); }
            }
        }

        [Test]
        public void SteppedMatchesPythonReference()
        {
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                var ftp = new NativeArray<float>(s.RefFtp, Allocator.TempJob);
                var eth = new NativeArray<float>(s.RefEth, Allocator.TempJob);
                try
                {
                    foreach (var pair in new[] { ("end_hazard", SteppedVariant.EndHazard), ("interp_hazard", SteppedVariant.InterpHazard),
                                                 ("end_bernoulli", SteppedVariant.EndBernoulli) })
                    {
                        double[] py = ReadF64(Fixture($"small.ref_stepped_dt60_{pair.Item1}.f64"));
                        var r = SteppedFireCpu.Run(s, ftp, eth, 1, 60f, pair.Item2, (ulong)s.RefSeed, s.RefReplica);
                        int mismatch = 0, both = 0;
                        double sumAbs = 0;
                        for (int i = 0; i < s.NodeCount; i++)
                        {
                            bool a = !double.IsInfinity(py[i]), b = !float.IsInfinity(r.TIgn[i]);
                            if (a != b) { mismatch++; continue; }
                            if (a) { both++; sumAbs += Math.Abs(r.TIgn[i] - py[i]); }
                        }
                        Assert.LessOrEqual(mismatch, 3, $"{pair.Item1}: burned/unburned outcome differs from Python");
                        Assert.Greater(both, 100, $"{pair.Item1}: fixture should spread");
                        Assert.AreEqual(0.0, sumAbs / both, 60.0, $"{pair.Item1}: mean ignition-time difference");
                    }
                }
                finally { ftp.Dispose(); eth.Dispose(); }
            }
        }

        [Test]
        public void SteppedConvergesToExact()
        {
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                var ftp = new NativeArray<float>(s.RefFtp, Allocator.TempJob);
                var eth = new NativeArray<float>(s.RefEth, Allocator.TempJob);
                try
                {
                    var ex = ExactFireCpu.Run(s, ftp, eth, 1);
                    double prev = double.MaxValue;
                    foreach (float dt in new[] { 120f, 30f, 7.5f })
                    {
                        var st = SteppedFireCpu.Run(s, ftp, eth, 1, dt, SteppedVariant.InterpHazard);
                        double sum = 0;
                        int both = 0;
                        for (int i = 0; i < s.NodeCount; i++)
                            if (!float.IsInfinity(ex.TIgn[i]) && !float.IsInfinity(st.TIgn[i])) { sum += Math.Abs(st.TIgn[i] - ex.TIgn[i]); both++; }
                        double err = sum / both;
                        Assert.Less(err, prev, $"error must shrink with dt (dt={dt})");
                        prev = err;
                    }
                }
                finally { ftp.Dispose(); eth.Dispose(); }
            }
        }

        [Test]
        public void GpuSteppedMatchesCpu()
        {
            if (!ExactFireGpu.Supported) Assert.Ignore("compute shaders not supported on this device");
            using (var s = FireScenario.Load(Fixture("small.ffes")))
            {
                const int R = 8;
                ExactFireCpu.DrawThresholds(s, 11, 0, R, out var ftp, out var eth);
                try
                {
                    foreach (var v in new[] { SteppedVariant.EndHazard, SteppedVariant.InterpHazard, SteppedVariant.EndBernoulli })
                    {
                        var cpu = SteppedFireCpu.Run(s, ftp, eth, R, 60f, v, 11, 0);
                        using (var gpu = new SteppedFireGpu(s, R))
                        {
                            var g = gpu.Run(ftp, eth, 60f, v, 11, 0);
                            int mismatch = 0;
                            for (int k = 0; k < cpu.TIgn.Length; k++)
                                if (float.IsInfinity(cpu.TIgn[k]) != float.IsInfinity(g.TIgn[k])) mismatch++;
                            Assert.LessOrEqual(mismatch, 3 * R, $"{v}: GPU and CPU stepped outcomes");
                        }
                    }
                }
                finally { ftp.Dispose(); eth.Dispose(); }
            }
        }
    }
}
