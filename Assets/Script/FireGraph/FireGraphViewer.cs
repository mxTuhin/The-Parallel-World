using System.IO;
using Unity.Collections;
using UnityEngine;

namespace ParallelWorld.FireGraph
{
    /// <summary>
    /// Loads a scenario, solves one run exactly (CPU or GPU) and draws every building
    /// as an instanced box coloured by its state at the current display time.
    ///
    /// The simulation is solved once into ignition times; rendering only samples them
    /// at `simTime`. Frame rate therefore cannot change the fire, and scrubbing or
    /// changing playback speed is free.
    /// </summary>
    public class FireGraphViewer : MonoBehaviour
    {
        public enum Backend { Cpu, Gpu }

        [Tooltip("Absolute path, or relative to StreamingAssets, of a .ffes file from `python -m ffe sim compile`.")]
        public string scenarioPath = "FireGraph/itoigawa2016_base_U5_D180.ffes";
        public Backend backend = Backend.Cpu;
        public CommitRule rule = CommitRule.Local;
        public ulong seed = 1;
        public int replica;
        [Tooltip("Use the reference thresholds stored in the file (matches the Python reference run).")]
        public bool useReferenceThresholds = true;

        [Header("Playback")]
        [Tooltip("Simulated seconds per real second.")]
        public float playbackSpeed = 600f;
        public float simTime;
        public bool playing = true;

        [Header("Rendering")]
        public Material material;          // uses shader FireGraph/BuildingsInstanced
        public Mesh mesh;                  // defaults to the built-in cube

        FireScenario _scenario;
        GraphicsBuffer _posSize, _profile, _tIgn;
        Bounds _bounds;
        float[] _ignitionTimes;
        public int BurnedCount { get; private set; }
        public float LastIgnitionTime { get; private set; }
        public float[] IgnitionTimes => _ignitionTimes;
        public FireScenario Scenario => _scenario;
        /// <summary>Set by FrameRateProbe: playback stops exactly at this simulated time.</summary>
        public float pauseAtSimTime = float.PositiveInfinity;

        void Start()
        {
            string path = Path.IsPathRooted(scenarioPath) ? scenarioPath : Path.Combine(Application.streamingAssetsPath, scenarioPath);
            _scenario = FireScenario.Load(path);
            _ignitionTimes = Solve();
            Upload(_ignitionTimes);
        }

        float[] Solve()
        {
            int n = _scenario.NodeCount;
            NativeArray<float> ftp, eth;
            if (useReferenceThresholds && _scenario.HasRefThresholds)
            {
                ftp = new NativeArray<float>(_scenario.RefFtp, Allocator.TempJob);
                eth = new NativeArray<float>(_scenario.RefEth, Allocator.TempJob);
            }
            else ExactFireCpu.DrawThresholds(_scenario, seed, replica, 1, out ftp, out eth);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ExactFireCpu.Result res;
                if (backend == Backend.Gpu && ExactFireGpu.Supported)
                    using (var gpu = new ExactFireGpu(_scenario, 1)) res = gpu.Run(ftp, eth, rule);
                else res = ExactFireCpu.Run(_scenario, ftp, eth, 1, rule);
                BurnedCount = 0;
                LastIgnitionTime = 0f;
                for (int i = 0; i < n; i++)
                    if (res.TIgn[i] < float.PositiveInfinity) { BurnedCount++; LastIgnitionTime = Mathf.Max(LastIgnitionTime, res.TIgn[i]); }
                Debug.Log($"[FireGraph] {Path.GetFileName(_scenario.SourcePath)}: {BurnedCount}/{n} buildings burn, " +
                          $"last ignition {LastIgnitionTime / 3600f:F2} h, {res.Iterations[0]} iterations, " +
                          $"{sw.Elapsed.TotalMilliseconds:F1} ms ({backend})");
                return res.TIgn;
            }
            finally { ftp.Dispose(); eth.Dispose(); }
        }

        void Upload(float[] tIgn)
        {
            int n = _scenario.NodeCount;
            var posSize = new Vector4[n];
            var profile = new Vector4[n];
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < n; i++)
            {
                float side = Mathf.Sqrt(Mathf.Max(_scenario.Area[i], 1f));
                posSize[i] = new Vector4(_scenario.X[i], _scenario.Y[i], side, Mathf.Max(_scenario.Height[i], 1f));
                profile[i] = new Vector4(_scenario.Tau0[i], _scenario.Tg[i], _scenario.Td[i], _scenario.Tx[i]);
                var p = new Vector3(_scenario.X[i], 0f, _scenario.Y[i]);
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p + Vector3.up * posSize[i].w);
            }
            _bounds = new Bounds((lo + hi) * 0.5f, (hi - lo) + Vector3.one * 50f);
            _posSize = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            _profile = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            _tIgn = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 4);
            _posSize.SetData(posSize);
            _profile.SetData(profile);
            _tIgn.SetData(tIgn);
            if (mesh == null) mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (material == null) material = new Material(Shader.Find("FireGraph/BuildingsInstanced"));
            material.SetBuffer("_PosSize", _posSize);
            material.SetBuffer("_Profile", _profile);
            material.SetBuffer("_TIgnBuf", _tIgn);
        }

        void Update()
        {
            if (_tIgn == null) return;
            if (playing)
            {
                simTime += Time.deltaTime * playbackSpeed;
                if (simTime >= pauseAtSimTime) { simTime = pauseAtSimTime; playing = false; }
            }
            material.SetFloat("_SimTime", simTime);
            var rp = new RenderParams(material) { worldBounds = _bounds };
            Graphics.RenderMeshPrimitives(rp, mesh, 0, _scenario.NodeCount);
        }

        /// <summary>Buildings per state at simulated time t: unburned, incubating, burning, burnt out.
        /// Same rule as the shader (BuildingsInstanced.shader, StateColor).</summary>
        public int[] StateCounts(float t)
        {
            var c = new int[4];
            if (_ignitionTimes == null) return c;
            for (int i = 0; i < _scenario.NodeCount; i++)
            {
                float dt = t - _ignitionTimes[i];
                if (!(dt >= 0f)) { c[0]++; continue; }
                float end = _scenario.Tau0[i] + _scenario.Tg[i] + _scenario.Td[i] + _scenario.Tx[i];
                if (dt <= _scenario.Tau0[i]) c[1]++;
                else if (dt < end) c[2]++;
                else c[3]++;
            }
            return c;
        }

        void OnGUI()
        {
            if (_scenario == null) return;
            GUI.Label(new Rect(10, 10, 600, 22),
                $"t = {simTime / 3600f:F2} h   burned (final) {BurnedCount}/{_scenario.NodeCount}   speed {playbackSpeed:F0}x");
        }

        void OnDestroy()
        {
            _posSize?.Release();
            _profile?.Release();
            _tIgn?.Release();
            _scenario?.Dispose();
        }
    }
}
