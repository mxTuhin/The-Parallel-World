using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ParallelWorld.FireGraph.Editor
{
    /// <summary>
    /// Headless entry points (run from a terminal with a GPU; do not pass -nographics):
    ///
    ///   Unity -batchmode -projectPath . -executeMethod ParallelWorld.FireGraph.Editor.FireGraphBatch.Parity
    ///         -ffes FFEData/zones/itoigawa2016/sim/base_U5_D180.ffes -quit
    ///
    ///   Unity -batchmode -projectPath . -executeMethod ParallelWorld.FireGraph.Editor.FireGraphBatch.Benchmark
    ///         -ffes <file> -backend gpu|cpu -replicas 256 -batches 40 -seed 1 -rule local -out <dir> -quit
    ///
    /// Parity compares CPU (all commit rules) and GPU against the Python reference
    /// (<scenario>.ref_tign.f64 written by `python -m ffe sim compile`) and exits with
    /// code 0 on success, 1 on failure. Benchmark writes per-run results as CSV plus a
    /// JSON summary (runs per second), the input for RQ2 and the RQ3 ground truth.
    /// Both are also on the menu: Tools > Fire Graph.
    /// </summary>
    public static class FireGraphBatch
    {
        // ------------------------------------------------------------ arguments

        static string Arg(string name, string fallback = null)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-" + name) return args[i + 1];
            return fallback;
        }

        static string ResolvePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (Path.IsPathRooted(p)) return p;
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, p));
        }

        static CommitRule ParseRule(string s)
        {
            switch ((s ?? "local").ToLowerInvariant())
            {
                case "sequential": return CommitRule.Sequential;
                case "global": return CommitRule.Global;
                default: return CommitRule.Local;
            }
        }

        static void Exit(int code)
        {
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        // ------------------------------------------------------------ parity

        public static void Parity()
        {
            int code = 1;
            try { code = RunParity(ResolvePath(Arg("ffes"))) ? 0 : 1; }
            catch (Exception e) { Debug.LogException(e); }
            Exit(code);
        }

        [MenuItem("Tools/Fire Graph/Parity test (pick .ffes)")]
        static void ParityMenu()
        {
            string p = EditorUtility.OpenFilePanel("Scenario", Directory.GetParent(Application.dataPath).FullName, "ffes");
            if (!string.IsNullOrEmpty(p)) RunParity(p);
        }

        /// <summary>Relative tolerance for float32 engine vs float64 Python ignition times.</summary>
        public const double TimeRelTol = 2e-3;
        /// <summary>Allowed fraction of buildings whose burned/unburned outcome differs (near-ties in float32).</summary>
        public const double OutcomeTol = 0.002;

        public static bool RunParity(string ffesPath)
        {
            using (var s = FireScenario.Load(ffesPath))
            {
                if (!s.HasRefThresholds) throw new InvalidOperationException("scenario has no reference thresholds; recompile with `ffe sim compile`");
                string refPath = Path.ChangeExtension(ffesPath, ".ref_tign.f64");
                double[] py = File.Exists(refPath) ? ReadF64(refPath) : null;
                var report = new StringBuilder();
                report.AppendLine($"Parity: {Path.GetFileName(ffesPath)}  N={s.NodeCount} E={s.EdgeCount}");
                bool ok = true;

                var ftp = new NativeArray<float>(s.RefFtp, Allocator.TempJob);
                var eth = new NativeArray<float>(s.RefEth, Allocator.TempJob);
                try
                {
                    var seq = ExactFireCpu.Run(s, ftp, eth, 1, CommitRule.Sequential);
                    foreach (CommitRule rule in new[] { CommitRule.Global, CommitRule.Local })
                    {
                        var r = ExactFireCpu.Run(s, ftp, eth, 1, rule);
                        bool same = BitEqual(seq.TIgn, r.TIgn);
                        ok &= same;
                        report.AppendLine($"  CPU {rule,-10} vs CPU Sequential: bit-identical={same}  iterations {r.Iterations[0]} vs {seq.Iterations[0]}");
                    }
                    if (py != null)
                    {
                        Compare(py, seq.TIgn, out double maxRel, out double outcomeFrac);
                        bool pass = maxRel <= TimeRelTol && outcomeFrac <= OutcomeTol;
                        ok &= pass;
                        report.AppendLine($"  CPU vs Python: max rel time diff {maxRel:E2}, outcome mismatch {outcomeFrac:P3}  -> {(pass ? "PASS" : "FAIL")}");
                    }
                    else report.AppendLine("  (no Python reference file found)");

                    if (ExactFireGpu.Supported)
                    {
                        using (var gpu = new ExactFireGpu(s, 1))
                        {
                            var g1 = gpu.Run(ftp, eth, CommitRule.Local);
                            var g2 = gpu.Run(ftp, eth, CommitRule.Sequential);
                            bool gpuSelf = BitEqual(g1.TIgn, g2.TIgn);
                            Compare(ToDouble(seq.TIgn), g1.TIgn, out double maxRel, out double outcomeFrac);
                            bool pass = gpuSelf && maxRel <= TimeRelTol && outcomeFrac <= OutcomeTol;
                            ok &= pass;
                            report.AppendLine($"  GPU Local vs GPU Sequential: bit-identical={gpuSelf}");
                            report.AppendLine($"  GPU vs CPU: max rel time diff {maxRel:E2}, outcome mismatch {outcomeFrac:P3}, " +
                                              $"bit-identical={BitEqual(seq.TIgn, g1.TIgn)}  -> {(pass ? "PASS" : "FAIL")}  ({SystemInfo.graphicsDeviceName})");
                        }
                    }
                    else report.AppendLine("  GPU: compute shaders not supported here (skipped)");
                }
                finally { ftp.Dispose(); eth.Dispose(); }

                report.AppendLine(ok ? "PARITY PASS" : "PARITY FAIL");
                Debug.Log(report.ToString());
                File.WriteAllText(Path.ChangeExtension(ffesPath, ".parity.txt"), report.ToString());
                return ok;
            }
        }

        static double[] ReadF64(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            var d = new double[raw.Length / 8];
            Buffer.BlockCopy(raw, 0, d, 0, d.Length * 8);
            return d;
        }

        static double[] ToDouble(float[] a)
        {
            var d = new double[a.Length];
            for (int i = 0; i < a.Length; i++) d[i] = a[i];
            return d;
        }

        static bool BitEqual(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            return true;
        }

        /// <summary>Max relative time difference over buildings burned in both, and the fraction with a different outcome.</summary>
        static void Compare(double[] reference, float[] test, out double maxRelOut, out double outcomeFracOut)
        {
            double maxRel = 0;
            int mismatch = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                bool a = !double.IsInfinity(reference[i]), b = !float.IsInfinity(test[i]);
                if (a != b) { mismatch++; continue; }
                if (!a || reference[i] <= 0) continue;
                maxRel = Math.Max(maxRel, Math.Abs(test[i] - reference[i]) / reference[i]);
            }
            maxRelOut = maxRel;
            outcomeFracOut = reference.Length == 0 ? 0 : (double)mismatch / reference.Length;
        }

        // ------------------------------------------------------------ benchmark

        public static void Benchmark()
        {
            int code = 1;
            try
            {
                RunBenchmark(ResolvePath(Arg("ffes")), (Arg("backend", "gpu") ?? "gpu").ToLowerInvariant(),
                    int.Parse(Arg("replicas", "256"), CultureInfo.InvariantCulture),
                    int.Parse(Arg("batches", "4"), CultureInfo.InvariantCulture),
                    ulong.Parse(Arg("seed", "1"), CultureInfo.InvariantCulture),
                    ParseRule(Arg("rule")), ResolvePath(Arg("out")));
                code = 0;
            }
            catch (Exception e) { Debug.LogException(e); }
            Exit(code);
        }

        [MenuItem("Tools/Fire Graph/Benchmark GPU 256x4 (pick .ffes)")]
        static void BenchmarkMenu()
        {
            string p = EditorUtility.OpenFilePanel("Scenario", Directory.GetParent(Application.dataPath).FullName, "ffes");
            if (!string.IsNullOrEmpty(p)) RunBenchmark(p, ExactFireGpu.Supported ? "gpu" : "cpu", 256, 4, 1, CommitRule.Local, null);
        }

        public static void RunBenchmark(string ffesPath, string backend, int replicas, int batches, ulong seed,
            CommitRule rule, string outDir)
        {
            outDir = string.IsNullOrEmpty(outDir) ? Path.Combine(Path.GetDirectoryName(ffesPath), "unity_runs") : outDir;
            Directory.CreateDirectory(outDir);
            string tag = $"{Path.GetFileNameWithoutExtension(ffesPath)}_{backend}_{rule}_R{replicas}_B{batches}_S{seed}";
            using (var s = FireScenario.Load(ffesPath))
            {
                var csv = new StringBuilder("replica,burned,t_last_s,iterations\n");
                double solveMs = 0, drawMs = 0;
                ExactFireGpu gpu = backend == "gpu" ? new ExactFireGpu(s, replicas) : null;
                try
                {
                    for (int bIdx = 0; bIdx < batches; bIdx++)
                    {
                        var sw = Stopwatch.StartNew();
                        ExactFireCpu.DrawThresholds(s, seed, bIdx * replicas, replicas, out var ftp, out var eth);
                        drawMs += sw.Elapsed.TotalMilliseconds;
                        try
                        {
                            sw.Restart();
                            var res = gpu != null ? gpu.Run(ftp, eth, rule) : ExactFireCpu.Run(s, ftp, eth, replicas, rule);
                            solveMs += sw.Elapsed.TotalMilliseconds;
                            int n = s.NodeCount;
                            for (int r = 0; r < replicas; r++)
                            {
                                int burned = 0;
                                float last = 0f;
                                for (int i = 0; i < n; i++)
                                {
                                    float t = res.TIgn[r * n + i];
                                    if (!float.IsInfinity(t)) { burned++; if (t > last) last = t; }
                                }
                                csv.Append(bIdx * replicas + r).Append(',').Append(burned).Append(',')
                                   .Append(last.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                                   .Append(res.Iterations[r]).Append('\n');
                            }
                        }
                        finally { ftp.Dispose(); eth.Dispose(); }
                    }
                }
                finally { gpu?.Dispose(); }

                int runs = replicas * batches;
                File.WriteAllText(Path.Combine(outDir, tag + ".csv"), csv.ToString());
                string json = "{\n" +
                    $"  \"scenario\": \"{Path.GetFileName(ffesPath)}\",\n  \"backend\": \"{backend}\",\n  \"rule\": \"{rule}\",\n" +
                    $"  \"device\": \"{(backend == "gpu" ? SystemInfo.graphicsDeviceName : SystemInfo.processorType)}\",\n" +
                    $"  \"n_nodes\": {s.NodeCount},\n  \"n_edges\": {s.EdgeCount},\n  \"replicas_per_batch\": {replicas},\n  \"batches\": {batches},\n" +
                    $"  \"runs\": {runs},\n  \"solve_ms\": {solveMs.ToString("F1", CultureInfo.InvariantCulture)},\n" +
                    $"  \"threshold_draw_ms\": {drawMs.ToString("F1", CultureInfo.InvariantCulture)},\n" +
                    $"  \"runs_per_second\": {(runs / (solveMs / 1000.0)).ToString("F1", CultureInfo.InvariantCulture)}\n}}\n";
                File.WriteAllText(Path.Combine(outDir, tag + ".json"), json);
                Debug.Log($"[FireGraph] {tag}: {runs} runs in {solveMs:F0} ms ({runs / (solveMs / 1000.0):F1} runs/s) -> {outDir}");
            }
        }
    }
}
