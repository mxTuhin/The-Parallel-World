using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

/// <summary>
/// CPU+GPU fire simulation backend.
///
/// Fire spread  : FireSpreadKernel (ComputeShader) — one GPU thread per cell.
///                Double-buffered StructuredBuffers<FireCell>; the entire
///                cellular-automaton step runs in parallel on the GPU.
///                CPU is completely free of fire physics work.
///
/// Burning scan : BurningCellScanKernel — 360 k GPU threads compact state==2
///                indices into AppendStructuredBuffer<uint> for
///                FireParticleVisualizer (AsyncGPUReadback, O(burning_count)).
///
/// Enemy AI     : EnemyMoveJob (Burst IJobParallelForTransform) — stays on CPU.
///                coreCount controls JobsUtility.JobWorkerCount so the 1/4/8-
///                core comparison still shows meaningful differences in the
///                enemy AI profiler column even though fire is on GPU.
///
/// No heatmap quad.  All visuals come from the particle system.
///
/// Shadow grid  : AsyncGPUReadback updates a CPU-side NativeArray<FireCell>
///                every shadowSyncInterval frames.  GetCellState / GetCell-
///                Temperature read from this shadow (acceptable staleness for
///                player damage queries).  IgniteCell writes directly to the
///                GPU buffer via ComputeBuffer.SetData with per-element offset.
/// </summary>
public class FireSimulationControllerGPUCompute : MonoBehaviour, IFireSimulation
{
    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("Grid")]
    public int width  = 300;
    public int height = 300;

    [Header("GPU Compute")]
    [Tooltip("Assign the FireHeatmap compute shader asset " +
             "(contains CSMain + FireSpreadKernel + BurningCellScanKernel).")]
    [FormerlySerializedAs("heatmapCompute")]
    public ComputeShader fireCompute;

    [Header("Heatmap Display")]
    [Tooltip("The Quad/Plane that receives the heatmap RenderTexture and also " +
             "defines the fire grid area for world↔grid coordinate mapping.")]
    public Renderer targetRenderer;
    public FilterMode filterMode = FilterMode.Point;
    [Tooltip("Enable/disable the heatmap quad visualisation.\n" +
             "When OFF the CSMain dispatch is skipped (zero GPU cost) and the " +
             "quad reverts to its original texture.  Particle visuals are unaffected.")]
    public bool showHeatmapVisuals = true;

    [Header("Material Library")]
    public FireMaterialDefinition[] materialDefinitions;

    [Header("Zone Layout")]
    [Tooltip("Painter model: later entries override earlier ones.  " +
             "Rect fields use normalised 0-1 grid space (origin = bottom-left).  " +
             "Leave empty to use material 0 for every cell.")]
    public FireZoneDefinition[] zones;

    [Header("Parallel Control — Enemy AI Only")]
    [Tooltip("Sets CPU worker-thread count for enemy Burst jobs.\n" +
             "Fire spread runs on GPU and is unaffected by this value.")]
    [Range(1, 8)]
    public int coreCount = 4;

    [Header("Simulation")]
    public float diffusionRate           = 0.5f;
    public float burnRate                = 0.3f;
    public float maxBurningTemperature   = 20f;
    [Tooltip("Clamps Time.deltaTime fed to the GPU kernel — prevents focus-switch " +
             "spikes from injecting large heat bursts in a single dispatch.")]
    public float maxSimulationDeltaTime  = 0.033f;

    [Header("Heatmap")]
    [Tooltip("Reference temperature for IFireSimulation.HeatMax (used by FireZoneController).")]
    public float heatMax = 10f;

    [Header("Start Fire")]
    [Tooltip("Grid coordinates (x,y) of cells that begin Burning.  Y=0 is the BOTTOM row.")]
    public Vector2Int[] startFireCells;

    [Header("Shadow Sync")]
    [Tooltip("Frames between AsyncGPUReadback updates of the CPU shadow grid.\n" +
             "Lower = fresher GetCellState data, higher = less GPU→CPU bandwidth.\n" +
             "At 60 fps, 16 = ~267 ms latency for player damage queries (acceptable).")]
    [Range(1, 60)]
    public int shadowSyncInterval = 16;

    // ── IFireSimulation ───────────────────────────────────────────────────────

    public int          Width          => width;
    public int          Height         => height;
    public float        HeatMax        => heatMax;
    public Vector2Int[] StartFireCells => startFireCells;
    public bool         IsGPUMode      => true;
    public int          CoreCount      => coreCount;

    // ── CPU shadow (initialised once; refreshed via AsyncGPUReadback) ─────────

    /// <summary>
    /// CPU-side mirror of the GPU's CurrentGrid.  Used for:
    ///   • Initial GPU buffer upload (ApplyStartFire writes here first).
    ///   • GetCellState / GetCellTemperature queries (acceptable staleness).
    ///   • Source data for IgniteCell SetData writes.
    /// Refreshed from GPU every shadowSyncInterval frames via async readback.
    /// </summary>
    private NativeArray<FireCell>            _shadowGrid;
    private NativeArray<FireMaterialRuntime> _runtimeMaterials;

    // ── GPU buffers ───────────────────────────────────────────────────────────

    private ComputeBuffer _currentGridBuffer;       // StructuredBuffer<FireCell>    — spread input
    private ComputeBuffer _nextGridBuffer;          // RWStructuredBuffer<FireCell>  — spread output
    private ComputeBuffer _materialsBuffer;         // StructuredBuffer<FireMaterial>
    private ComputeBuffer _burningCellsBuffer;      // AppendStructuredBuffer<uint>  — scan output
    private ComputeBuffer _burningCellsCountBuffer; // Raw(1 int)                    — CopyCount dest

    // ── GPU buffers / textures ────────────────────────────────────────────────

    private RenderTexture _heatmapRT;
    private Texture       _originalTexture;
    private bool          _heatmapApplied;

    // ── Kernel indices ────────────────────────────────────────────────────────

    private int _heatmapKernel;
    private int _spreadKernel;
    private int _scanKernel;

    // ── Ignition queue (rare: startup + optional gameplay events) ─────────────

    private readonly List<int> _pendingIgnitions  = new();
    private          FireCell[] _singleCellUpload = new FireCell[1]; // reused — avoids GC

    // ── Shadow readback ───────────────────────────────────────────────────────

    private bool _shadowReadbackPending;
    private int  _shadowSyncCounter;

    // ── Profiler markers ──────────────────────────────────────────────────────

    // "FireSim.Schedule+Complete" is the SAME name as the CPU controller's marker so
    // SimulationBenchmark's ProfilerRecorder captures both modes with one query.
    // In GPU mode this wraps only the Dispatch() call (microseconds of CPU overhead),
    // clearly demonstrating that fire physics has moved off the CPU.
    private static readonly ProfilerMarker SpreadMarker =
        new ProfilerMarker("FireSim.Schedule+Complete");

    private static readonly ProfilerMarker ScanMarker =
        new ProfilerMarker("FireSim.BurningCellScan");

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake() => ApplyCoreCount();

    void OnValidate()
    {
        if (Application.isPlaying) ApplyCoreCount();
        width                  = Mathf.Max(1, width);
        height                 = Mathf.Max(1, height);
        heatMax                = Mathf.Max(0.0001f, heatMax);
        maxBurningTemperature  = Mathf.Max(0.001f,  maxBurningTemperature);
        maxSimulationDeltaTime = Mathf.Max(0.001f,  maxSimulationDeltaTime);
    }

    void Start()
    {
        if (materialDefinitions == null || materialDefinitions.Length == 0)
        {
            Debug.LogError("[FireSim GPU] No MaterialDefinitions assigned — simulation cannot start.");
            enabled = false;
            return;
        }
        if (fireCompute == null)
        {
            Debug.LogError("[FireSim GPU] fireCompute (ComputeShader) not assigned.");
            enabled = false;
            return;
        }

        int total = width * height;
        _shadowGrid       = new NativeArray<FireCell>(total,                      Allocator.Persistent);
        _runtimeMaterials = new NativeArray<FireMaterialRuntime>(
                                materialDefinitions.Length,                       Allocator.Persistent);

        InitializeMaterials();
        InitializeGrid();
        ApplyStartFire();
        InitializeGpuResources();
    }

    void ApplyCoreCount()
    {
        int clamped = Mathf.Clamp(coreCount, 1, SystemInfo.processorCount);
        JobsUtility.JobWorkerCount = clamped - 1;
        Debug.Log($"[FireSim GPU] Worker threads = {clamped}  " +
                  "(enemy AI Burst jobs — fire spread is on GPU).");
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    void InitializeMaterials()
    {
        for (int i = 0; i < materialDefinitions.Length; i++)
        {
            var def = materialDefinitions[i];
            _runtimeMaterials[i] = new FireMaterialRuntime
            {
                ignitionTemperature = def.ignitionTemperature,
                heatAbsorption      = def.heatAbsorption,
                spreadMultiplier    = def.spreadMultiplier,
                heatEmission        = def.heatEmission,
                coolingRate         = def.coolingRate,
                coolingVisualDelay  = def.coolingVisualDelay,
                fuelAmount          = def.fuelAmount,
                isWall              = def.isWall ? 1 : 0
            };
        }
    }

    /// <summary>
    /// Fills _shadowGrid with initial cell data using the zone painter model.
    /// Single-threaded CPU loop — runs once at startup, performance is irrelevant.
    /// </summary>
    void InitializeGrid()
    {
        int zoneCount = zones?.Length ?? 0;

        for (int index = 0; index < width * height; index++)
        {
            int x = index % width;
            int y = index / width;

            // Painter model: later zones override earlier ones.
            // All cells default to material 0 if no zone covers them.
            int matIndex = 0;
            for (int z = 0; z < zoneCount; z++)
            {
                var zone = zones[z];
                int xMin = Mathf.Clamp(Mathf.RoundToInt(zone.normalizedRect.xMin * width),  0, width);
                int xMax = Mathf.Clamp(Mathf.RoundToInt(zone.normalizedRect.xMax * width),  0, width);
                int yMin = Mathf.Clamp(Mathf.RoundToInt(zone.normalizedRect.yMin * height), 0, height);
                int yMax = Mathf.Clamp(Mathf.RoundToInt(zone.normalizedRect.yMax * height), 0, height);
                if (x >= xMin && x < xMax && y >= yMin && y < yMax)
                    matIndex = Mathf.Clamp(zone.materialIndex, 0, materialDefinitions.Length - 1);
            }

            _shadowGrid[index] = new FireCell
            {
                state                   = 1,
                temperature             = 0f,
                materialIndex           = matIndex,
                fuel                    = _runtimeMaterials[matIndex].fuelAmount,
                burnFinishedTime        = 0f,
                coolingStartTemperature = 0f
            };
        }
    }

    void ApplyStartFire()
    {
        if (startFireCells == null) return;
        foreach (var pos in startFireCells)
        {
            int cx  = Mathf.Clamp(pos.x, 0, width  - 1);
            int cy  = Mathf.Clamp(pos.y, 0, height - 1);
            int idx = cy * width + cx;
            var c   = _shadowGrid[idx];
            c.state = 2;
            _shadowGrid[idx] = c;
        }
    }

    void InitializeGpuResources()
    {
        _heatmapKernel = fireCompute.FindKernel("CSMain");
        _spreadKernel  = fireCompute.FindKernel("FireSpreadKernel");
        _scanKernel    = fireCompute.FindKernel("BurningCellScanKernel");

        int cellStride = Marshal.SizeOf<FireCell>();             // 24 bytes
        int matStride  = Marshal.SizeOf<FireMaterialRuntime>();  // 32 bytes

        // ── Double-buffered fire grid ─────────────────────────────────────────
        _currentGridBuffer = new ComputeBuffer(width * height, cellStride);
        _nextGridBuffer    = new ComputeBuffer(width * height, cellStride);

        // ── Materials (read-only, uploaded once) ──────────────────────────────
        _materialsBuffer = new ComputeBuffer(materialDefinitions.Length, matStride);

        // ── Burning-cell scan buffers (particle visualiser) ───────────────────
        _burningCellsBuffer = new ComputeBuffer(
            width * height, sizeof(uint), ComputeBufferType.Append);
        _burningCellsCountBuffer = new ComputeBuffer(
            1, sizeof(int), ComputeBufferType.Raw);

        // ── Upload initial data ───────────────────────────────────────────────
        // Both grid buffers start identical; NextGrid will be overwritten by the
        // first FireSpreadKernel dispatch before anyone reads it.
        _currentGridBuffer.SetData(_shadowGrid);
        _nextGridBuffer.SetData(_shadowGrid);

        // Materials never change at runtime; upload once.
        var matsManaged = new FireMaterialRuntime[_runtimeMaterials.Length];
        for (int i = 0; i < _runtimeMaterials.Length; i++)
            matsManaged[i] = _runtimeMaterials[i];
        _materialsBuffer.SetData(matsManaged);

        // ── Bind shader constants ─────────────────────────────────────────────
        fireCompute.SetInt("Width",               width);
        fireCompute.SetInt("Height",              height);
        fireCompute.SetFloat("DiffusionRate",     diffusionRate);
        fireCompute.SetFloat("BurnRate",          burnRate);
        fireCompute.SetFloat("MaxBurningTemperature", maxBurningTemperature);
        // DeltaTime is set per-frame in Update()

        // ── Heatmap RenderTexture (CSMain) ────────────────────────────────────
        _heatmapRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            filterMode        = filterMode,
            wrapMode          = TextureWrapMode.Clamp
        };
        _heatmapRT.Create();

        fireCompute.SetFloat("HeatMax", heatMax);

        // ── Bind ALL resources to ALL kernels ─────────────────────────────────
        // DX11/FXC RULE: every globally-declared UAV (RWStructuredBuffer,
        // AppendStructuredBuffer, RWTexture2D) must be bound to EVERY kernel
        // at dispatch time, even if that kernel does not use it.
        // Failing to do this causes "Kernel at index (N) is invalid" at runtime.
        int[] allKernels = { _heatmapKernel, _spreadKernel, _scanKernel };
        foreach (int k in allKernels)
        {
            fireCompute.SetBuffer (k, "CurrentGrid",  _currentGridBuffer);
            fireCompute.SetBuffer (k, "NextGrid",     _nextGridBuffer);
            fireCompute.SetBuffer (k, "Materials",    _materialsBuffer);
            fireCompute.SetBuffer (k, "BurningCells", _burningCellsBuffer);
            fireCompute.SetTexture(k, "Result",       _heatmapRT);
        }

        // Cache original texture so we can restore it when heatmap is toggled off
        if (targetRenderer != null)
            _originalTexture = targetRenderer.sharedMaterial.mainTexture;

        if (showHeatmapVisuals) ApplyHeatmapTexture();

        int kbEach = width * height * cellStride / 1024;
        Debug.Log($"[FireSim GPU] GPU spread initialised. " +
                  $"Grid {width}×{height}  " +
                  $"Buffers: {kbEach} KB each × 2  " +
                  $"Materials: {materialDefinitions.Length}");
    }

    // ── Heatmap texture helpers ───────────────────────────────────────────────

    void ApplyHeatmapTexture()
    {
        if (_heatmapApplied || _heatmapRT == null || targetRenderer == null) return;
        var mat = targetRenderer.material;
        mat.mainTexture       = _heatmapRT;
        mat.mainTextureScale  = Vector2.one;
        mat.mainTextureOffset = Vector2.zero;
        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap",       _heatmapRT);
            mat.SetTextureScale("_BaseMap",  Vector2.one);
            mat.SetTextureOffset("_BaseMap", Vector2.zero);
        }
        _heatmapApplied = true;
    }

    void RestoreOriginalTexture()
    {
        if (!_heatmapApplied || targetRenderer == null) return;
        var mat = targetRenderer.material;
        mat.mainTexture = _originalTexture;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _originalTexture);
        _heatmapApplied = false;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    void Update()
    {
        if (_currentGridBuffer == null) return;

        // Apply any pending CPU→GPU ignition writes before the spread step.
        FlushPendingIgnitions();

        int groupsX = Mathf.CeilToInt(width  / 8f);
        int groupsY = Mathf.CeilToInt(height / 8f);

        // ── 1. GPU fire spread ────────────────────────────────────────────────
        // FireSpreadKernel reads _currentGridBuffer → writes _nextGridBuffer.
        // The CPU-side Dispatch() call is ~microseconds; actual GPU work runs
        // asynchronously.  The ProfilerMarker captures CPU dispatch overhead
        // only — contrast with CPU mode where it captures the full Burst job.
        using (SpreadMarker.Auto())
        {
            fireCompute.SetFloat("DeltaTime",
                Mathf.Min(Time.deltaTime, maxSimulationDeltaTime));
            fireCompute.Dispatch(_spreadKernel, groupsX, groupsY, 1);
        }

        // ── 2. Swap buffer references ─────────────────────────────────────────
        // After the spread dispatch the "next" buffer holds the new state.
        // Swap references so CurrentGrid always points to the latest result.
        // GPU command queue is sequential — the heatmap/scan dispatches queued
        // below will execute after the spread dispatch completes on the GPU.
        (_currentGridBuffer, _nextGridBuffer) = (_nextGridBuffer, _currentGridBuffer);

        // Rebind ALL kernels to the post-swap buffers.
        // Both CurrentGrid and NextGrid pointers have swapped — every kernel
        // that declares either as a global resource must see the new binding.
        int[] allKernels = { _heatmapKernel, _spreadKernel, _scanKernel };
        foreach (int k in allKernels)
        {
            fireCompute.SetBuffer(k, "CurrentGrid", _currentGridBuffer);
            fireCompute.SetBuffer(k, "NextGrid",    _nextGridBuffer);
        }

        // ── 3. Heatmap colourisation (optional — zero cost when disabled) ─────
        if (showHeatmapVisuals)
        {
            ApplyHeatmapTexture();
            fireCompute.SetFloat("HeatMax", heatMax);   // supports live tweaking
            fireCompute.Dispatch(_heatmapKernel, groupsX, groupsY, 1);
        }
        else
        {
            RestoreOriginalTexture();
        }

        // ── 4. GPU burning-cell scan (particle system) ────────────────────────
        // Reads freshly-computed CurrentGrid; produces compact index list for
        // FireParticleVisualizer's AsyncGPUReadback path.
        using (ScanMarker.Auto())
        {
            _burningCellsBuffer.SetCounterValue(0);
            fireCompute.Dispatch(_scanKernel, groupsX, groupsY, 1);
            ComputeBuffer.CopyCount(_burningCellsBuffer, _burningCellsCountBuffer, 0);
        }

        // ── 5. Periodic shadow readback for GetCellState / GetCellTemperature ─
        _shadowSyncCounter++;
        if (_shadowSyncCounter >= shadowSyncInterval && !_shadowReadbackPending)
        {
            _shadowSyncCounter     = 0;
            _shadowReadbackPending = true;
            AsyncGPUReadback.Request(_currentGridBuffer, OnShadowReadbackComplete);
        }
    }

    // ── Ignition flush ────────────────────────────────────────────────────────

    void FlushPendingIgnitions()
    {
        if (_pendingIgnitions.Count == 0) return;

        foreach (int idx in _pendingIgnitions)
        {
            // Write the shadow cell (already has state=2 from IgniteCell) to GPU
            _singleCellUpload[0] = _shadowGrid[idx];
            _currentGridBuffer.SetData(_singleCellUpload, 0, idx, 1);
        }
        _pendingIgnitions.Clear();
    }

    // ── Shadow readback callback ──────────────────────────────────────────────

    void OnShadowReadbackComplete(AsyncGPUReadbackRequest req)
    {
        _shadowReadbackPending = false;

        // Guard: this object or shadow may have been destroyed before callback fires
        if (req.hasError || this == null || !_shadowGrid.IsCreated) return;

        var data = req.GetData<FireCell>();
        if (data.Length == _shadowGrid.Length)
            NativeArray<FireCell>.Copy(data, _shadowGrid);
    }

    // ── IFireSimulation ───────────────────────────────────────────────────────

    public int GetCellState(int index)
    {
        if (!_shadowGrid.IsCreated || (uint)index >= (uint)_shadowGrid.Length) return 0;
        return _shadowGrid[index].state;
    }

    public float GetCellTemperature(int index)
    {
        if (!_shadowGrid.IsCreated || (uint)index >= (uint)_shadowGrid.Length) return 0f;
        return _shadowGrid[index].temperature;
    }

    /// <summary>
    /// Marks a cell as Burning.  Updates the CPU shadow immediately and queues
    /// a per-element ComputeBuffer.SetData write that is flushed at the start
    /// of the next Update() before the spread kernel dispatches.
    ///
    /// Safe to call from Start() (before first Update) — the pending list is
    /// flushed each frame, so the ignition reaches the GPU on the first Update.
    /// </summary>
    public void IgniteCell(int x, int y)
    {
        int cx  = Mathf.Clamp(x, 0, width  - 1);
        int cy  = Mathf.Clamp(y, 0, height - 1);
        int idx = cy * width + cx;

        // Update CPU shadow
        if (_shadowGrid.IsCreated)
        {
            var c = _shadowGrid[idx];
            if (c.state == 1) { c.state = 2; _shadowGrid[idx] = c; }
        }

        // Queue GPU write (flushed in FlushPendingIgnitions before next dispatch)
        if (!_pendingIgnitions.Contains(idx))
            _pendingIgnitions.Add(idx);
    }

    public void SetCoreCount(int count)
    {
        coreCount = Mathf.Clamp(count, 1, SystemInfo.processorCount);
        ApplyCoreCount();
    }

    public ComputeBuffer GetBurningCellsBuffer()      => _burningCellsBuffer;
    public ComputeBuffer GetBurningCellsCountBuffer() => _burningCellsCountBuffer;

    // ── Cleanup ───────────────────────────────────────────────────────────────

    void OnDestroy()
    {
        if (_shadowGrid.IsCreated)       _shadowGrid.Dispose();
        if (_runtimeMaterials.IsCreated) _runtimeMaterials.Dispose();

        _currentGridBuffer?.Release();
        _nextGridBuffer?.Release();
        _materialsBuffer?.Release();
        _burningCellsBuffer?.Release();
        _burningCellsCountBuffer?.Release();

        if (_heatmapRT != null) { _heatmapRT.Release(); _heatmapRT = null; }
    }
}
