using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Runtime benchmark harness for The Parallel World.
///
/// New Input System version.
/// This version does NOT require fireSimMono to implement IFireSimulation.
/// It works with any assigned MonoBehaviour and uses reflection to set core count.
/// </summary>
public class SimulationBenchmark : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("References")]
    [Tooltip("Assign FireSimulationController CPU or FireSimulationControllerGPUCompute CPU+GPU.")]
    [SerializeField] private MonoBehaviour fireSimMono;

    [SerializeField] private EnemyManager enemyManager;

    [Header("Auto-Run Settings")]
    [Tooltip("Seconds to let the simulation stabilise before recording starts.")]
    [SerializeField] private float warmupDuration = 4f;

    [Tooltip("Seconds to collect samples per core-count step.")]
    [SerializeField] private float recordDuration = 12f;

    [Tooltip("Core counts to test in sequence.")]
    [SerializeField] private int[] coreCounts = { 1, 4, 8 };

    [Tooltip("Number of enemies present during the benchmark.")]
    [SerializeField] private int benchmarkEnemyCount = 500;

    [Header("Display")]
    [SerializeField] private bool showOverlay = true;

    [Header("Input - New Input System")]
    [SerializeField] private Key toggleOverlayKey = Key.F1;
    [SerializeField] private Key startRunKey = Key.F5;
    [SerializeField] private Key exportCsvKey = Key.F6;

    // ── State machine ──────────────────────────────────────────────────────────

    private enum State
    {
        Idle,
        Warmup,
        Recording,
        Done
    }

    private State _state = State.Idle;
    private int _stepIndex = 0;
    private float _stateTimer = 0f;

    // ── Accumulators ──────────────────────────────────────────────────────────

    private double _sumFrame;
    private double _sumSim;
    private double _sumGpuScan;
    private double _sumCpuScan;
    private double _sumEnemy;

    private int _sampleCount;

    private readonly List<double> _frameSamples = new();

    private int _savedMaxEnemies;

    // ── Results ────────────────────────────────────────────────────────────────

    private readonly List<BenchmarkRow> _rows = new();

    private struct BenchmarkRow
    {
        public string timestamp;
        public string mode;
        public int cores;
        public int gridCells;

        public double avgFrameMs;
        public double p95FrameMs;
        public double simStepMs;
        public double gpuScanMs;
        public double cpuScanMs;
        public double enemyAIMs;

        public static string CsvHeader =>
            "Timestamp,Mode,Cores,GridCells," +
            "AvgFrameMs,P95FrameMs," +
            "FireSimMs,GpuScanMs,CpuScanMs,EnemyAIMs";

        public string ToCsvRow()
        {
            return $"{timestamp},{mode},{cores},{gridCells}," +
                   $"{avgFrameMs:F3},{p95FrameMs:F3}," +
                   $"{simStepMs:F3},{gpuScanMs:F3},{cpuScanMs:F3},{enemyAIMs:F3}";
        }
    }

    // ── Profiler recorders ────────────────────────────────────────────────────

    private ProfilerRecorder _simRecorder;
    private ProfilerRecorder _gpuScanRecorder;
    private ProfilerRecorder _cpuScanRecorder;
    private ProfilerRecorder _enemyRecorder;

    // ── Live display values ───────────────────────────────────────────────────

    private double _liveFrame;
    private double _liveSim;
    private double _liveGpuScan;
    private double _liveCpuScan;
    private double _liveEnemy;

    // ── GUI ───────────────────────────────────────────────────────────────────

    private Rect _windowRect = new Rect(12, 12, 460, 0);

    private GUIStyle _boxStyle;
    private GUIStyle _headerStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _valueStyle;
    private GUIStyle _rowStyle;
    private GUIStyle _btnStyle;
    private GUIStyle _warnStyle;

    private bool _stylesBuilt;

    // ── Runtime ───────────────────────────────────────────────────────────────

    private MonoBehaviour _fireSim;
    private string _statusLine = "Idle — press F5 to start";

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        _fireSim = fireSimMono;

        if (_fireSim == null)
        {
            Debug.LogError("[Benchmark] fireSimMono is not assigned.");
        }

        _simRecorder = ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts,
            "FireSim.Schedule+Complete",
            60
        );

        _gpuScanRecorder = ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts,
            "FireSim.BurningCellScan",
            60
        );

        _cpuScanRecorder = ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts,
            "FireVFX.CPUScan",
            60
        );

        _enemyRecorder = ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts,
            "EnemyAI.MoveJob",
            60
        );
    }

    private void OnDisable()
    {
        SafeDispose(ref _simRecorder);
        SafeDispose(ref _gpuScanRecorder);
        SafeDispose(ref _cpuScanRecorder);
        SafeDispose(ref _enemyRecorder);
    }

    private static void SafeDispose(ref ProfilerRecorder recorder)
    {
        if (recorder.Valid)
        {
            recorder.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Update
    // ─────────────────────────────────────────────────────────────────────────

    private void Update()
    {
        UpdateLiveMetrics();
        HandleNewInputSystemKeys();
        UpdateBenchmarkState();
    }

    private void UpdateLiveMetrics()
    {
        _liveFrame = Time.unscaledDeltaTime * 1000.0;
        _liveSim = GetRecorderMs(_simRecorder);
        _liveGpuScan = GetRecorderMs(_gpuScanRecorder);
        _liveCpuScan = GetRecorderMs(_cpuScanRecorder);
        _liveEnemy = GetRecorderMs(_enemyRecorder);
    }

    private void HandleNewInputSystemKeys()
    {
        if (WasKeyPressed(toggleOverlayKey))
        {
            showOverlay = !showOverlay;
        }

        if (WasKeyPressed(startRunKey))
        {
            StartAutoRun();
        }

        if (WasKeyPressed(exportCsvKey))
        {
            ExportCsv();
        }
    }

    private bool WasKeyPressed(Key key)
    {
        if (Keyboard.current == null)
        {
            return false;
        }

        KeyControl keyControl = Keyboard.current[key];

        return keyControl != null && keyControl.wasPressedThisFrame;
    }

    private void UpdateBenchmarkState()
    {
        if (_state == State.Idle || _state == State.Done)
        {
            return;
        }

        _stateTimer += Time.unscaledDeltaTime;

        switch (_state)
        {
            case State.Warmup:
                UpdateWarmupState();
                break;

            case State.Recording:
                UpdateRecordingState();
                break;
        }
    }

    private void UpdateWarmupState()
    {
        int currentCores = GetCurrentCoreCount();
        _statusLine = $"Warmup — cores {currentCores}";

        if (_stateTimer >= warmupDuration)
        {
            BeginRecording();
        }
    }

    private void UpdateRecordingState()
    {
        AccumulateSample();

        int currentCores = GetCurrentCoreCount();
        _statusLine = $"Recording — cores {currentCores}";

        if (_stateTimer >= recordDuration)
        {
            FinishRecordingStep();
            MoveToNextStep();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Benchmark control
    // ─────────────────────────────────────────────────────────────────────────

    public void StartAutoRun()
    {
        _fireSim = fireSimMono;

        if (_fireSim == null)
        {
            Debug.LogError("[Benchmark] Cannot start. fireSimMono is not assigned.");
            return;
        }

        if (coreCounts == null || coreCounts.Length == 0)
        {
            Debug.LogError("[Benchmark] Cannot start. coreCounts is empty.");
            return;
        }

        _rows.Clear();
        _stepIndex = 0;

        SaveEnemySettings();
        ApplyBenchmarkEnemyCount();

        ApplyCoreCount(coreCounts[_stepIndex]);

        _state = State.Warmup;
        _stateTimer = 0f;

        _statusLine = $"Warmup — cores {coreCounts[_stepIndex]}";

        Debug.Log("[Benchmark] Auto-run started.");
    }

    private void BeginRecording()
    {
        ResetAccumulators();

        _state = State.Recording;
        _stateTimer = 0f;

        int currentCores = GetCurrentCoreCount();
        _statusLine = $"Recording — cores {currentCores}";
    }

    private void FinishRecordingStep()
    {
        int cores = GetCurrentCoreCount();

        BenchmarkRow row = new BenchmarkRow
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            mode = DetectMode(),
            cores = cores,
            gridCells = GetGridCellCount(),

            avgFrameMs = SafeAverage(_sumFrame, _sampleCount),
            p95FrameMs = CalculateP95(_frameSamples),
            simStepMs = SafeAverage(_sumSim, _sampleCount),
            gpuScanMs = SafeAverage(_sumGpuScan, _sampleCount),
            cpuScanMs = SafeAverage(_sumCpuScan, _sampleCount),
            enemyAIMs = SafeAverage(_sumEnemy, _sampleCount)
        };

        _rows.Add(row);

        Debug.Log("[Benchmark] Recorded row: " + row.ToCsvRow());
    }

    private void MoveToNextStep()
    {
        _stepIndex++;

        if (_stepIndex >= coreCounts.Length)
        {
            _state = State.Done;
            _stateTimer = 0f;

            RestoreEnemySettings();

            _statusLine = "Done — press F6 to export CSV";

            Debug.Log("[Benchmark] Auto-run complete.");
            return;
        }

        ApplyCoreCount(coreCounts[_stepIndex]);

        _state = State.Warmup;
        _stateTimer = 0f;

        _statusLine = $"Warmup — cores {coreCounts[_stepIndex]}";
    }

    private void ResetAccumulators()
    {
        _sumFrame = 0;
        _sumSim = 0;
        _sumGpuScan = 0;
        _sumCpuScan = 0;
        _sumEnemy = 0;

        _sampleCount = 0;
        _frameSamples.Clear();
    }

    private void AccumulateSample()
    {
        _sumFrame += _liveFrame;
        _sumSim += _liveSim;
        _sumGpuScan += _liveGpuScan;
        _sumCpuScan += _liveCpuScan;
        _sumEnemy += _liveEnemy;

        _frameSamples.Add(_liveFrame);

        _sampleCount++;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CSV export
    // ─────────────────────────────────────────────────────────────────────────

    public void ExportCsv()
    {
        if (_rows.Count == 0)
        {
            Debug.LogWarning("[Benchmark] No benchmark rows to export.");
            _statusLine = "No rows to export. Run benchmark first.";
            return;
        }

        StringBuilder sb = new StringBuilder();

        sb.AppendLine(BenchmarkRow.CsvHeader);

        foreach (BenchmarkRow row in _rows)
        {
            sb.AppendLine(row.ToCsvRow());
        }

        string fileName = $"simulation_benchmark_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        string filePath = Path.Combine(Application.persistentDataPath, fileName);

        File.WriteAllText(filePath, sb.ToString());

        _statusLine = $"CSV exported: {fileName}";

        Debug.Log("[Benchmark] CSV exported to: " + filePath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Profiler helpers
    // ─────────────────────────────────────────────────────────────────────────

    private double GetRecorderMs(ProfilerRecorder recorder)
    {
        if (!recorder.Valid || recorder.Count == 0)
        {
            return 0.0;
        }

        return recorder.LastValue / 1_000_000.0;
    }

    private static double SafeAverage(double sum, int count)
    {
        if (count <= 0)
        {
            return 0.0;
        }

        return sum / count;
    }

    private static double CalculateP95(List<double> samples)
    {
        if (samples == null || samples.Count == 0)
        {
            return 0.0;
        }

        samples.Sort();

        int index = Mathf.CeilToInt(samples.Count * 0.95f) - 1;
        index = Mathf.Clamp(index, 0, samples.Count - 1);

        return samples[index];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fire simulation reflection helpers
    // ─────────────────────────────────────────────────────────────────────────

    private int GetCurrentCoreCount()
    {
        if (coreCounts == null || coreCounts.Length == 0)
        {
            return 0;
        }

        if (_stepIndex < 0 || _stepIndex >= coreCounts.Length)
        {
            return coreCounts[0];
        }

        return coreCounts[_stepIndex];
    }

    private string DetectMode()
    {
        if (fireSimMono == null)
        {
            return "Unknown";
        }

        string typeName = fireSimMono.GetType().Name.ToLowerInvariant();

        if (typeName.Contains("gpu") || typeName.Contains("compute"))
        {
            return "CPU+GPU";
        }

        return "CPU";
    }

    private int GetGridCellCount()
    {
        if (fireSimMono == null)
        {
            return 0;
        }

        Type type = fireSimMono.GetType();

        string[] possibleNames =
        {
            "GridCells",
            "gridCells",
            "CellCount",
            "cellCount",
            "TotalCells",
            "totalCells",
            "width",
            "height"
        };

        int width = GetIntMemberValue(type, fireSimMono, "width");
        int height = GetIntMemberValue(type, fireSimMono, "height");

        if (width > 0 && height > 0)
        {
            return width * height;
        }

        foreach (string name in possibleNames)
        {
            int value = GetIntMemberValue(type, fireSimMono, name);

            if (value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private int GetIntMemberValue(Type type, object target, string memberName)
    {
        FieldInfo field = type.GetField(
            memberName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (field != null && field.FieldType == typeof(int))
        {
            return (int)field.GetValue(target);
        }

        PropertyInfo property = type.GetProperty(
            memberName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (property != null && property.PropertyType == typeof(int) && property.CanRead)
        {
            return (int)property.GetValue(target);
        }

        return 0;
    }

    private void ApplyCoreCount(int cores)
    {
        if (_fireSim == null)
        {
            return;
        }

        Type type = _fireSim.GetType();

        MethodInfo method = type.GetMethod(
            "SetCoreCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (method != null)
        {
            method.Invoke(_fireSim, new object[] { cores });
            Debug.Log($"[Benchmark] Applied core count using SetCoreCount({cores}) on {type.Name}");
            return;
        }

        bool applied = false;

        applied |= SetIntMember(_fireSim, "coreCount", cores);
        applied |= SetIntMember(_fireSim, "workerCount", cores);
        applied |= SetIntMember(_fireSim, "jobWorkerCount", cores);
        applied |= SetIntMember(_fireSim, "maxCores", cores);
        applied |= SetIntMember(_fireSim, "coreLimit", cores);
        applied |= SetIntMember(_fireSim, "threadCount", cores);
        applied |= SetIntMember(_fireSim, "workerThreadCount", cores);

        if (applied)
        {
            Debug.Log($"[Benchmark] Applied core count: {cores} to {type.Name}");
        }
        else
        {
            Debug.LogWarning(
                $"[Benchmark] Could not find a core-count field or SetCoreCount() method on {type.Name}. " +
                $"Benchmark will continue, but core count may not actually change."
            );
        }
    }

    private bool SetIntMember(object target, string memberName, int value)
    {
        if (target == null)
        {
            return false;
        }

        Type type = target.GetType();

        FieldInfo field = type.GetField(
            memberName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (field != null && field.FieldType == typeof(int))
        {
            field.SetValue(target, value);
            return true;
        }

        PropertyInfo property = type.GetProperty(
            memberName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (property != null && property.PropertyType == typeof(int) && property.CanWrite)
        {
            property.SetValue(target, value);
            return true;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Enemy helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SaveEnemySettings()
    {
        if (enemyManager == null)
        {
            return;
        }

        _savedMaxEnemies = GetEnemyMaxCount();
    }

    private void ApplyBenchmarkEnemyCount()
    {
        if (enemyManager == null)
        {
            return;
        }

        SetEnemyMaxCount(benchmarkEnemyCount);
    }

    private void RestoreEnemySettings()
    {
        if (enemyManager == null)
        {
            return;
        }

        SetEnemyMaxCount(_savedMaxEnemies);
    }

    private int GetEnemyMaxCount()
    {
        if (enemyManager == null)
        {
            return 0;
        }

        Type type = enemyManager.GetType();

        string[] possibleNames =
        {
            "maxEnemies",
            "MaxEnemies",
            "enemyCount",
            "EnemyCount",
            "currentEnemyCount",
            "CurrentEnemyCount"
        };

        foreach (string name in possibleNames)
        {
            int value = GetIntMemberValue(type, enemyManager, name);

            if (value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private void SetEnemyMaxCount(int value)
    {
        if (enemyManager == null)
        {
            return;
        }

        bool applied = false;

        applied |= SetIntMember(enemyManager, "maxEnemies", value);
        applied |= SetIntMember(enemyManager, "MaxEnemies", value);
        applied |= SetIntMember(enemyManager, "enemyCount", value);
        applied |= SetIntMember(enemyManager, "EnemyCount", value);
        applied |= SetIntMember(enemyManager, "currentEnemyCount", value);
        applied |= SetIntMember(enemyManager, "CurrentEnemyCount", value);

        if (!applied)
        {
            Debug.LogWarning("[Benchmark] Could not find enemy count field on EnemyManager.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GUI
    // ─────────────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!showOverlay)
        {
            return;
        }

        BuildStylesIfNeeded();

        _windowRect = GUILayout.Window(
            98731,
            _windowRect,
            DrawWindow,
            "Simulation Benchmark",
            _boxStyle,
            GUILayout.Width(460)
        );
    }

    private void DrawWindow(int id)
    {
        GUILayout.Space(6);

        GUILayout.Label(_statusLine, _headerStyle);

        GUILayout.Space(8);

        DrawMetricRow("Frame", $"{_liveFrame:F3} ms");
        DrawMetricRow("Fire Simulation", $"{_liveSim:F3} ms");
        DrawMetricRow("GPU Burning Scan", $"{_liveGpuScan:F3} ms");
        DrawMetricRow("CPU VFX Scan", $"{_liveCpuScan:F3} ms");
        DrawMetricRow("Enemy AI", $"{_liveEnemy:F3} ms");

        GUILayout.Space(8);

        DrawMetricRow("Mode", DetectMode());
        DrawMetricRow("Current Cores", GetCurrentCoreCount().ToString());
        DrawMetricRow("Grid Cells", GetGridCellCount().ToString());
        DrawMetricRow("Rows Recorded", _rows.Count.ToString());

        GUILayout.Space(10);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Start Auto-Run", _btnStyle, GUILayout.Height(30)))
        {
            StartAutoRun();
        }

        if (GUILayout.Button("Export CSV", _btnStyle, GUILayout.Height(30)))
        {
            ExportCsv();
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(6);

        GUILayout.Label("F1: Toggle Overlay   F5: Start   F6: Export", _warnStyle);

        GUI.DragWindow();
    }

    private void DrawMetricRow(string label, string value)
    {
        GUILayout.BeginHorizontal(_rowStyle);

        GUILayout.Label(label, _labelStyle, GUILayout.Width(180));
        GUILayout.Label(value, _valueStyle);

        GUILayout.EndHorizontal();
    }

    private void BuildStylesIfNeeded()
    {
        if (_stylesBuilt)
        {
            return;
        }

        _boxStyle = new GUIStyle(GUI.skin.window)
        {
            padding = new RectOffset(12, 12, 24, 12)
        };

        _headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft
        };

        _valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleRight
        };

        _rowStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(8, 8, 3, 3)
        };

        _btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold
        };

        _warnStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            wordWrap = true,
            alignment = TextAnchor.MiddleCenter
        };

        _stylesBuilt = true;
    }
}