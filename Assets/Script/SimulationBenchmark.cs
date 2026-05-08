using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Runtime benchmark harness for The Parallel World.
///
/// WHAT IT MEASURES
///   Reads Unity ProfilerMarker timings via ProfilerRecorder to report per-system
///   costs rather than wall-clock guesses.  Markers are captured from the C# side
///   of the simulation — no Editor profiler window needed.
///
/// HOW TO USE
///   1. Add this component to any persistent GameObject in the scene.
///   2. Assign fireSimMono (either CPU or GPU controller) and enemyManager.
///   3. Press F5 (or click Start Auto-Run) to begin the automated sequence:
///        Warmup (4 s) → Record (12 s) for core count 1 → 4 → 8
///   4. When Done, press F6 (or Export CSV) to write results.
///   5. Toggle the overlay with F1.
///
/// CPU vs CPU+GPU COMPARISON
///   Run with CPU controller active   → records Mode=CPU    rows
///   Run with GPU controller active   → records Mode=CPU+GPU rows
///   Combine both CSVs in a spreadsheet for the side-by-side comparison.
///
/// ENEMY AI NOTE
///   The EnemyMoveJob (Burst IJobParallelForTransform) runs on CPU threads in BOTH
///   modes.  Setting benchmarkEnemyCount = 500 means core-count changes show up in
///   the Enemy AI column even in CPU+GPU mode — which is the expected result: fire
///   spread moves to the GPU but enemy AI stays proportional to CPU core count.
/// </summary>
public class SimulationBenchmark : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("References")]
    [Tooltip("Assign FireSimulationController (CPU) or FireSimulationControllerGPUCompute (CPU+GPU).")]
    [SerializeField] private MonoBehaviour fireSimMono;
    [SerializeField] private EnemyManager  enemyManager;

    [Header("Auto-Run Settings")]
    [Tooltip("Seconds to let the simulation stabilise before recording starts.")]
    [SerializeField] private float warmupDuration = 4f;
    [Tooltip("Seconds to collect samples per core-count step.")]
    [SerializeField] private float recordDuration = 12f;
    [Tooltip("Core counts to test in sequence.  Edit freely.")]
    [SerializeField] private int[] coreCounts = { 1, 4, 8 };
    [Tooltip("Number of enemies present during the benchmark.  Higher values make " +
             "the EnemyAI column more prominent in the results.")]
    [SerializeField] private int benchmarkEnemyCount = 500;

    [Header("Display")]
    [SerializeField] private bool    showOverlay  = true;
    [SerializeField] private KeyCode toggleKey    = KeyCode.F1;
    [SerializeField] private KeyCode startRunKey  = KeyCode.F5;
    [SerializeField] private KeyCode exportCsvKey = KeyCode.F6;

    // ── State machine ──────────────────────────────────────────────────────────

    private enum State { Idle, Warmup, Recording, Done }
    private State _state      = State.Idle;
    private int   _stepIndex  = 0;
    private float _stateTimer = 0f;

    // ── Accumulators ──────────────────────────────────────────────────────────

    // Per-step running sums (reset at start of each Recording phase)
    private double _sumFrame, _sumSim, _sumGpuScan, _sumCpuScan, _sumEnemy;
    private int    _sampleCount;
    private readonly List<double> _frameSamples = new();  // for P95

    // Saved before benchmark changes it
    private int _savedMaxEnemies;

    // ── Results ────────────────────────────────────────────────────────────────

    private readonly List<BenchmarkRow> _rows = new();

    private struct BenchmarkRow
    {
        public string timestamp, mode;
        public int    cores, gridCells;
        public double avgFrameMs, p95FrameMs;
        // SimStepMs: CPU mode = Burst job CPU time; CPU+GPU mode = GPU dispatch overhead (~µs)
        public double simStepMs, gpuScanMs, cpuScanMs, enemyAIMs;

        public static string CsvHeader =>
            "Timestamp,Mode,Cores,GridCells," +
            "AvgFrameMs,P95FrameMs," +
            "FireSimMs,GpuScanMs,CpuScanMs,EnemyAIMs";

        public string ToCsvRow() =>
            $"{timestamp},{mode},{cores},{gridCells}," +
            $"{avgFrameMs:F3},{p95FrameMs:F3}," +
            $"{simStepMs:F3},{gpuScanMs:F3},{cpuScanMs:F3},{enemyAIMs:F3}";
    }

    // ── Profiler recorders ────────────────────────────────────────────────────
    // "FireSim.Schedule+Complete":
    //   CPU mode    → wraps full Burst FireSpreadJob (milliseconds, core-count sensitive)
    //   CPU+GPU mode → wraps only ComputeShader.Dispatch() call (microseconds)
    //   The contrast between these two IS the key benchmark result.

    private ProfilerRecorder _simRecorder;
    private ProfilerRecorder _gpuScanRecorder;
    private ProfilerRecorder _cpuScanRecorder;
    private ProfilerRecorder _enemyRecorder;

    // ── Live display values (updated each frame) ──────────────────────────────

    private double _liveFrame, _liveSim, _liveGpuScan, _liveCpuScan, _liveEnemy;

    // ── GUI ───────────────────────────────────────────────────────────────────

    private Rect     _windowRect = new Rect(12, 12, 460, 0);
    private GUIStyle _boxStyle, _headerStyle, _labelStyle, _valueStyle,
                     _rowStyle, _btnStyle, _warnStyle;
    private bool     _stylesBuilt;

    // ── Runtime ───────────────────────────────────────────────────────────────

    private IFireSimulation _fireSim;
    private string          _statusLine = "Idle — press F5 to start";

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    void OnEnable()
    {
        _fireSim = fireSimMono as IFireSimulation;
        if (_fireSim == null)
            Debug.LogError("[Benchmark] fireSimMono must implement IFireSimulation.");

        // Capacity 60 = rolling buffer of 60 frames (~1 s at 60 fps)
        _simRecorder     = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "FireSim.Schedule+Complete", 60);
        _gpuScanRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "FireSim.BurningCellScan",   60);
        _cpuScanRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "FireVFX.CPUScan",           60);
        _enemyRecorder   = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "EnemyAI.MoveJob",           60);
    }

    void OnDisable()
    {
        SafeDispose(ref _simRecorder);
        SafeDispose(ref _gpuScanRecorder);
        SafeDispose(ref _cpuScanRecorder);
        SafeDispose(ref _enemyRecorder);
    }

    static void SafeDispose(ref ProfilerRecorder r)
    {
        if (r.IsRunning) r.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Update
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        // Refresh live metrics
        _liveFrame   = Time.unscaledDeltaTime * 1000.0;
        _liveSim     = NsToMs(_simRecorder);
        _liveGpuScan = NsToMs(_gpuScanRecorder);
        _liveCpuScan = NsToMs(_cpuScanRecorder);
        _liveEnemy   = NsToMs(_enemyRecorder);

        // Key bindings
        if (Input.GetKeyDown(toggleKey))    showOverlay = !showOverlay;
        if (Input.GetKeyDown(startRunKey)   && _state == State.Idle) StartAutoRun();
        if (Input.GetKeyDown(exportCsvKey)) ExportCsv();

        TickAutoRun();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto-run state machine
    // ─────────────────────────────────────────────────────────────────────────

    void StartAutoRun()
    {
        if (_fireSim == null)
        {
            Debug.LogError("[Benchmark] Cannot start — no IFireSimulation assigned.");
            return;
        }

        _rows.Clear();
        _stepIndex  = 0;
        _stateTimer = 0f;

        // Raise enemy cap and pre-spawn so AI cost is visible from frame 1
        if (enemyManager != null)
        {
            _savedMaxEnemies         = enemyManager.MaxEnemies;
            enemyManager.MaxEnemies  = benchmarkEnemyCount;
            enemyManager.ForceSpawnEnemies(benchmarkEnemyCount);
        }

        EnterWarmup();
    }

    void EnterWarmup()
    {
        if (_stepIndex >= coreCounts.Length) { FinishRun(); return; }

        int cores = coreCounts[_stepIndex];
        _fireSim.SetCoreCount(cores);

        _state      = State.Warmup;
        _stateTimer = 0f;
        _statusLine = $"Warmup — cores={cores}  ({warmupDuration:F0} s)";
        Debug.Log($"[Benchmark] Warmup  step {_stepIndex + 1}/{coreCounts.Length}  cores={cores}");
    }

    void EnterRecording()
    {
        _state       = State.Recording;
        _stateTimer  = 0f;
        _statusLine  = $"Recording — cores={coreCounts[_stepIndex]}  ({recordDuration:F0} s)";

        // Reset accumulators
        _sumFrame = _sumSim = _sumGpuScan = _sumCpuScan = _sumEnemy = 0;
        _sampleCount = 0;
        _frameSamples.Clear();

        Debug.Log($"[Benchmark] Recording step {_stepIndex + 1}/{coreCounts.Length}");
    }

    void TickAutoRun()
    {
        if (_state == State.Idle || _state == State.Done) return;

        _stateTimer += Time.unscaledDeltaTime;

        if (_state == State.Warmup)
        {
            if (_stateTimer >= warmupDuration)
                EnterRecording();
            else
                _statusLine = $"Warmup — cores={coreCounts[_stepIndex]}  " +
                              $"{warmupDuration - _stateTimer:F1} s left";
        }
        else if (_state == State.Recording)
        {
            // Accumulate this frame's samples
            _sumFrame   += _liveFrame;
            _sumSim     += _liveSim;
            _sumGpuScan += _liveGpuScan;
            _sumCpuScan += _liveCpuScan;
            _sumEnemy   += _liveEnemy;
            _sampleCount++;
            _frameSamples.Add(_liveFrame);

            _statusLine = $"Recording — cores={coreCounts[_stepIndex]}  " +
                          $"{recordDuration - _stateTimer:F1} s left  " +
                          $"({_sampleCount} samples)";

            if (_stateTimer >= recordDuration)
            {
                SaveCurrentRow();
                _stepIndex++;
                EnterWarmup();
            }
        }
    }

    void SaveCurrentRow()
    {
        if (_sampleCount == 0) return;

        double avg = _sumFrame / _sampleCount;
        double p95 = ComputeP95(_frameSamples);

        string mode = (_fireSim?.IsGPUMode == true) ? "CPU+GPU" : "CPU";

        _rows.Add(new BenchmarkRow
        {
            timestamp  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            mode       = mode,
            cores      = coreCounts[_stepIndex],
            gridCells  = (_fireSim?.Width ?? 0) * (_fireSim?.Height ?? 0),
            avgFrameMs = avg,
            p95FrameMs = p95,
            simStepMs  = _sumSim     / _sampleCount,  // CPU: Burst job ms; GPU: dispatch µs
            gpuScanMs  = _sumGpuScan / _sampleCount,
            cpuScanMs  = _sumCpuScan / _sampleCount,
            enemyAIMs  = _sumEnemy   / _sampleCount
        });

        Debug.Log($"[Benchmark] Saved  mode={mode}  cores={coreCounts[_stepIndex]}  " +
                  $"avgFrame={avg:F2} ms  sim={_sumSim / _sampleCount:F2} ms");
    }

    void FinishRun()
    {
        _state      = State.Done;
        _statusLine = $"Done — {_rows.Count} row(s) recorded.  Press F6 to export.";

        // Restore original enemy cap
        if (enemyManager != null)
            enemyManager.MaxEnemies = _savedMaxEnemies;

        Debug.Log($"[Benchmark] Auto-run complete.  {_rows.Count} rows.  Press F6 to export CSV.");
        ExportCsv();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CSV export
    // ─────────────────────────────────────────────────────────────────────────

    void ExportCsv()
    {
        if (_rows.Count == 0)
        {
            Debug.LogWarning("[Benchmark] No rows to export yet.  Run the benchmark first.");
            return;
        }

        string folder = Path.Combine(Application.dataPath, "BenchmarkResults");
        Directory.CreateDirectory(folder);

        string filename = $"benchmark_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
        string path     = Path.Combine(folder, filename);

        var sb = new StringBuilder();
        sb.AppendLine(BenchmarkRow.CsvHeader);
        foreach (var row in _rows)
            sb.AppendLine(row.ToCsvRow());

        File.WriteAllText(path, sb.ToString());
        Debug.Log($"[Benchmark] CSV exported → {path}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IMGUI overlay
    // ─────────────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        if (!showOverlay) return;

        BuildStyles();
        _windowRect = GUILayout.Window(9999, _windowRect, DrawWindow, "", _boxStyle);
    }

    void DrawWindow(int id)
    {
        string mode      = (_fireSim?.IsGPUMode == true) ? "CPU + GPU" : "CPU only";
        string gpuBadge  = (_fireSim?.IsGPUMode == true) ? "  ▶ GPU ON" : "";
        int    w         = _fireSim?.Width  ?? 0;
        int    h         = _fireSim?.Height ?? 0;
        int    cores     = _fireSim?.CoreCount ?? 0;
        int    enemies   = enemyManager != null ? enemyManager.EnemyCount : 0;
        int    maxEn     = enemyManager != null ? enemyManager.MaxEnemies  : 0;

        // ── Header ──────────────────────────────────────────────────────────
        GUILayout.Label($"THE PARALLEL WORLD  ·  Benchmark  [F1 toggle]", _headerStyle);
        Divider();

        // ── Mode & grid ─────────────────────────────────────────────────────
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Mode:   {mode}{gpuBadge}", _labelStyle);
        GUILayout.Label($"Grid: {w}×{h}", _valueStyle);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label($"Cores:  {cores}", _labelStyle);
        GUILayout.Label($"Enemies: {enemies} / {maxEn}", _valueStyle);
        GUILayout.EndHorizontal();
        Divider();

        // ── Live metrics ────────────────────────────────────────────────────
        GUILayout.Label("LIVE METRICS", _headerStyle);
        bool gpu = _fireSim?.IsGPUMode == true;

        MetricRow("Frame Time",                  _liveFrame,   "ms", true);
        // CPU mode: Burst FireSpreadJob (ms, core-count sensitive)
        // CPU+GPU mode: Dispatch() overhead only (~µs) — fire is on GPU
        MetricRow(gpu ? "Fire Spread (GPU dsp)" : "Fire Spread (CPU Burst)",
                                                 _liveSim,     "ms", true);
        MetricRow("GPU Cell Scan",  gpu ? _liveGpuScan : -1,  "ms",  gpu);
        MetricRow("CPU Cell Scan", !gpu ? _liveCpuScan : -1,  "ms", !gpu);
        MetricRow("Enemy AI",                    _liveEnemy,   "ms", true);
        Divider();

        // ── Auto-run status ──────────────────────────────────────────────────
        GUILayout.Label("AUTO-RUN", _headerStyle);
        GUILayout.Label(_statusLine, _rowStyle);

        if (_rows.Count > 0)
        {
            GUILayout.Label($"Completed rows: {_rows.Count}", _rowStyle);
        }
        Divider();

        // ── Core-count quick-set ─────────────────────────────────────────────
        GUILayout.BeginHorizontal();
        GUILayout.Label("Set cores:", _labelStyle, GUILayout.Width(72));
        foreach (int c in new[] { 1, 2, 4, 8 })
        {
            GUI.enabled = _state == State.Idle;
            if (GUILayout.Button(c.ToString(), _btnStyle, GUILayout.Width(34)))
                _fireSim?.SetCoreCount(c);
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        Divider();

        // ── Buttons ──────────────────────────────────────────────────────────
        GUILayout.BeginHorizontal();

        GUI.enabled = _state == State.Idle;
        if (GUILayout.Button("Start Auto-Run  [F5]", _btnStyle))
            StartAutoRun();

        GUI.enabled = _rows.Count > 0;
        if (GUILayout.Button("Export CSV  [F6]", _btnStyle))
            ExportCsv();

        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (_state != State.Idle && _state != State.Done)
        {
            float total = coreCounts.Length * (warmupDuration + recordDuration);
            float done  = _stepIndex * (warmupDuration + recordDuration) + _stateTimer;
            Rect  bar   = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                              GUILayout.Height(6), GUILayout.ExpandWidth(true));
            GUI.DrawTexture(bar, Texture2D.whiteTexture, ScaleMode.StretchToFill,
                            false, 0, new Color(0.2f, 0.2f, 0.2f), 0, 0);
            Rect fill = new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(done / total), bar.height);
            GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill,
                            false, 0, new Color(1f, 0.55f, 0.1f), 0, 0);
        }

        // Allow dragging the window
        GUI.DragWindow(new Rect(0, 0, _windowRect.width, 30));
    }

    void MetricRow(string label, double value, string unit, bool active)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _labelStyle, GUILayout.Width(140));
        if (active && value >= 0)
            GUILayout.Label($"{value,7:F2} {unit}", _valueStyle);
        else
            GUILayout.Label("  ——", _valueStyle);
        GUILayout.EndHorizontal();
    }

    void Divider()
    {
        GUILayout.Space(2);
        Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                     GUILayout.Height(1), GUILayout.ExpandWidth(true));
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill,
                        false, 0, new Color(0.4f, 0.4f, 0.4f), 0, 0);
        GUILayout.Space(2);
    }

    void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        var bg = MakeTex(new Color(0.08f, 0.08f, 0.08f, 0.92f));

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding  = new RectOffset(10, 10, 8, 8),
            normal   = { background = bg, textColor = Color.white }
        };

        _headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize  = 11,
            normal    = { textColor = new Color(1f, 0.8f, 0.3f) }
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal   = { textColor = new Color(0.85f, 0.85f, 0.85f) }
        };

        _valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleRight,
            normal    = { textColor = Color.white }
        };

        _rowStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            normal   = { textColor = new Color(0.75f, 0.75f, 0.75f) },
            wordWrap = true
        };

        _btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 10,
            padding  = new RectOffset(6, 6, 4, 4)
        };

        _warnStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal   = { textColor = new Color(1f, 0.4f, 0.4f) }
        };
    }

    static Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Utilities
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ProfilerRecorder.LastValue is in nanoseconds → convert to ms.</summary>
    static double NsToMs(ProfilerRecorder r) =>
        r.IsRunning && r.Count > 0 ? r.LastValue * 1e-6 : 0.0;

    static double ComputeP95(List<double> samples)
    {
        if (samples.Count == 0) return 0;
        var sorted = new List<double>(samples);
        sorted.Sort();
        int idx = Mathf.Clamp((int)(sorted.Count * 0.95f), 0, sorted.Count - 1);
        return sorted[idx];
    }
}
