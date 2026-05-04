using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Unity.Profiling;

public class FireSimulationControllerGPUCompute : MonoBehaviour
{
    [Header("Grid")]
    public int width  = 600;
    public int height = 600;

    [Header("Display")]
    public Renderer targetRenderer;
    public ComputeShader heatmapCompute;
    public FilterMode filterMode = FilterMode.Point;

    [Header("Visualization Toggle")]
    [Tooltip("Uncheck to freeze the heatmap texture and skip GPU uploads each frame (saves GPU bandwidth while debugging simulation logic).")]
    public bool showHeatmapVisuals = true;

    [Header("Material Library")]
    public FireMaterialDefinition[] materialDefinitions;

    [Header("Parallel Control")]
    [Range(1, 8)]
    public int coreCount = 4;

    [Header("Simulation")]
    public float diffusionRate = 0.5f;      // was 2f — at 2f, entire 600x600 grid ignites in seconds
    public float burnRate      = 0.3f;

    [Tooltip("Hard cap on how hot a burning cell can get. Prevents unbounded temperature growth " +
             "which would cause exponential cascade ignition across the whole grid.")]
    public float maxBurningTemperature = 20f;

    [Tooltip("Caps Time.deltaTime fed to the simulation. Prevents editor focus-switch spikes " +
             "from running seconds of simulation in one frame and instantly igniting the grid.")]
    public float maxSimulationDeltaTime = 0.033f;

    [Header("Performance")]
    [Range(1, 8)]
    public int visualUpdateInterval = 1;
    public int fireBatchSize = 64;

    [Header("Heatmap")]
    [Tooltip("Temperature at which the heatmap color gradient is fully saturated. " +
             "Set close to your material's ignitionTemperature so the warm-up is visible " +
             "instead of snapping straight from green to burning red.")]
    public float heatMax = 10f;             // was 100f — at 100f, pre-ignition gradient is invisible

    [Header("Zone Layout")]
    [Tooltip("Zones are evaluated top-to-bottom. Later zones paint over earlier ones " +
             "(painter model). Leave empty to use material 0 everywhere.\n\n" +
             "Rect fields use normalized 0-1 grid space:\n" +
             "  X / Y  = left / bottom edge of the zone (0 = grid edge)\n" +
             "  W / H  = width / height of the zone (1 = full grid span)\n\n" +
             "Example — top-third of grid: X=0  Y=0.66  W=1  H=0.34")]
    public FireZoneDefinition[] zones;

    [Header("Start Fire")]
    [Tooltip("Grid coordinates (x, y) of cells that begin in state=Burning at startup. " +
             "Note: grid Y=0 is the BOTTOM row. The compute shader Y-flips the texture, " +
             "so Y=0 in the inspector appears at the TOP of the plane in the scene.")]
    public Vector2Int[] startFireCells;

    private NativeArray<FireCell> currentGrid;
    private NativeArray<FireCell> nextGrid;
    private NativeArray<FireMaterialRuntime> runtimeMaterials;
    private NativeArray<int> neighborIndices;

    private ComputeBuffer gridBuffer;
    private RenderTexture heatmapRT;
    private int heatmapKernel;
    private int visualFrameCounter;

    private static readonly ProfilerMarker SimMarker    = new ProfilerMarker("FireSim.Schedule+Complete");
    private static readonly ProfilerMarker UploadMarker = new ProfilerMarker("FireHeatmap.GPUUpload+Dispatch");

    void Awake() => ApplyCoreCount();

    void OnValidate()
    {
        if (Application.isPlaying) ApplyCoreCount();

        fireBatchSize        = Mathf.Max(1, fireBatchSize);
        visualUpdateInterval = Mathf.Max(1, visualUpdateInterval);
        width                = Mathf.Max(1, width);
        height               = Mathf.Max(1, height);
        heatMax              = Mathf.Max(0.0001f, heatMax);
        maxBurningTemperature   = Mathf.Max(0.001f, maxBurningTemperature);
        maxSimulationDeltaTime  = Mathf.Max(0.001f, maxSimulationDeltaTime);
    }

    void Start()
    {
        if (materialDefinitions == null || materialDefinitions.Length == 0)
        {
            Debug.LogError("[FireSim] No MaterialDefinitions assigned. Simulation cannot start.");
            enabled = false;
            return;
        }

        int total = width * height;

        currentGrid      = new NativeArray<FireCell>(total, Allocator.Persistent);
        nextGrid         = new NativeArray<FireCell>(total, Allocator.Persistent);
        runtimeMaterials = new NativeArray<FireMaterialRuntime>(materialDefinitions.Length, Allocator.Persistent);

        InitializeMaterials();
        InitializeGrid();
        BuildNeighborIndices();
        ApplyStartFire();
        InitializeGpuHeatmap();
        UpdateGpuHeatmap();
    }

    void ApplyCoreCount()
    {
        int clamped = Mathf.Clamp(coreCount, 1, SystemInfo.processorCount);
        JobsUtility.JobWorkerCount = clamped - 1;
        Debug.Log($"[FireSim] Worker threads: {clamped}");
    }

    // ─── Initialisation ───────────────────────────────────────────────────────

    void InitializeMaterials()
    {
        for (int i = 0; i < materialDefinitions.Length; i++)
        {
            var def = materialDefinitions[i];
            runtimeMaterials[i] = new FireMaterialRuntime
            {
                ignitionTemperature = def.ignitionTemperature,
                heatAbsorption      = def.heatAbsorption,
                spreadMultiplier    = def.spreadMultiplier,
                heatEmission        = def.heatEmission,
                coolingRate         = def.coolingRate,
                coolingVisualDelay  = def.coolingVisualDelay, // was missing — burnt cells never showed the delay
                fuelAmount          = def.fuelAmount,
                isWall              = (byte)(def.isWall ? 1 : 0)
            };
        }
    }

    void InitializeGrid()
    {
        // Convert inspector zones (normalized Rect) → pixel-coord FireZoneRuntime for the Burst job
        int zoneCount = zones?.Length ?? 0;
        var zoneRuntimes = new NativeArray<FireZoneRuntime>(zoneCount, Allocator.TempJob);

        for (int i = 0; i < zoneCount; i++)
        {
            var z = zones[i];
            int matIdx = Mathf.Clamp(z.materialIndex, 0, materialDefinitions.Length - 1);
            zoneRuntimes[i] = new FireZoneRuntime
            {
                xMin          = Mathf.Clamp(Mathf.RoundToInt(z.normalizedRect.xMin * width),  0, width),
                xMax          = Mathf.Clamp(Mathf.RoundToInt(z.normalizedRect.xMax * width),  0, width),
                yMin          = Mathf.Clamp(Mathf.RoundToInt(z.normalizedRect.yMin * height), 0, height),
                yMax          = Mathf.Clamp(Mathf.RoundToInt(z.normalizedRect.yMax * height), 0, height),
                materialIndex = matIdx
            };
        }

        var initJob = new GridInitializationJob
        {
            grid          = currentGrid,
            materials     = runtimeMaterials,
            zones         = zoneRuntimes,
            materialCount = materialDefinitions.Length,
            width         = width,
            height        = height
        };
        initJob.Schedule(currentGrid.Length, 64).Complete();
        zoneRuntimes.Dispose();
    }

    [BurstCompile]
    public struct GridInitializationJob : IJobParallelFor
    {
        public NativeArray<FireCell> grid;
        [ReadOnly] public NativeArray<FireMaterialRuntime> materials;
        [ReadOnly] public NativeArray<FireZoneRuntime>     zones;

        public int materialCount;
        public int width;
        public int height;

        public void Execute(int index)
        {
            int x = index % width;
            int y = index / width;

            // Default material is 0. Zones are evaluated in order — later zones
            // paint over earlier ones, so put broad base zones first and
            // specific overrides after (painter / layer model).
            int matIndex = 0;
            for (int z = 0; z < zones.Length; z++)
            {
                var zone = zones[z];
                if (x >= zone.xMin && x < zone.xMax &&
                    y >= zone.yMin && y < zone.yMax)
                {
                    matIndex = math.clamp(zone.materialIndex, 0, materialCount - 1);
                }
            }

            grid[index] = new FireCell
            {
                state                   = 1,
                temperature             = 0f,
                materialIndex           = matIndex,
                fuel                    = materials[matIndex].fuelAmount,
                burnFinishedTime        = 0f,
                coolingStartTemperature = 0f
            };
        }
    }

    void BuildNeighborIndices()
    {
        neighborIndices = new NativeArray<int>(width * height * 8, Allocator.Persistent);

        int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dy = { -1,-1,-1,  0, 0,  1, 1, 1 };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index      = y * width + x;
                int baseOffset = index * 8;

                for (int k = 0; k < 8; k++)
                {
                    int nx = x + dx[k];
                    int ny = y + dy[k];

                    neighborIndices[baseOffset + k] =
                        (nx < 0 || nx >= width || ny < 0 || ny >= height)
                        ? -1
                        : ny * width + nx;
                }
            }
        }
    }

    void ApplyStartFire()
    {
        if (startFireCells == null) return;

        foreach (var pos in startFireCells)
        {
            // Guard: clamp to valid range so out-of-bounds inspector values don't crash
            int cx = Mathf.Clamp(pos.x, 0, width  - 1);
            int cy = Mathf.Clamp(pos.y, 0, height - 1);
            int index = cy * width + cx;

            var c = currentGrid[index];
            c.state = 2;
            currentGrid[index] = c;
        }
    }

    // ─── GPU Heatmap ──────────────────────────────────────────────────────────

    void InitializeGpuHeatmap()
    {
        if (heatmapCompute == null) { Debug.LogError("[FireSim] ComputeShader not assigned."); return; }
        if (targetRenderer  == null) { Debug.LogError("[FireSim] Target Renderer not assigned."); return; }

        heatmapKernel = heatmapCompute.FindKernel("CSMain");

        int stride = Marshal.SizeOf(typeof(FireCell));
        gridBuffer = new ComputeBuffer(width * height, stride);

        heatmapRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            filterMode        = filterMode,
            wrapMode          = TextureWrapMode.Clamp
        };
        heatmapRT.Create();

        heatmapCompute.SetInt("Width",    width);
        heatmapCompute.SetInt("Height",   height);
        heatmapCompute.SetFloat("HeatMax", heatMax);
        heatmapCompute.SetTexture(heatmapKernel, "Result", heatmapRT);

        var mat = targetRenderer.material;
        mat.mainTexture       = heatmapRT;
        mat.mainTextureScale  = Vector2.one;
        mat.mainTextureOffset = Vector2.zero;
        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap",      heatmapRT);
            mat.SetTextureScale("_BaseMap", Vector2.one);
            mat.SetTextureOffset("_BaseMap",Vector2.zero);
        }
    }

    void UpdateGpuHeatmap()
    {
        if (!showHeatmapVisuals) return;
        if (gridBuffer == null || heatmapCompute == null || heatmapRT == null) return;

        using (UploadMarker.Auto())
        {
            gridBuffer.SetData(currentGrid);
            heatmapCompute.SetFloat("HeatMax", heatMax);
            heatmapCompute.SetBuffer(heatmapKernel, "Grid", gridBuffer);
            heatmapCompute.Dispatch(
                heatmapKernel,
                Mathf.CeilToInt(width  / 8f),
                Mathf.CeilToInt(height / 8f),
                1
            );
        }
    }

    // ─── Simulation loop ──────────────────────────────────────────────────────

    void Update()
    {
        RunSimulationStep();

        visualFrameCounter++;
        if (visualFrameCounter >= visualUpdateInterval)
        {
            visualFrameCounter = 0;
            UpdateGpuHeatmap();
        }
    }

    void RunSimulationStep()
    {
        using (SimMarker.Auto())
        {
            var job = new FireSpreadJob
            {
                currentGrid           = currentGrid,
                nextGrid              = nextGrid,
                materials             = runtimeMaterials,
                neighborIndices       = neighborIndices,
                width                 = width,
                height                = height,
                diffusionRate         = diffusionRate,
                burnRate              = burnRate,
                deltaTime             = Mathf.Min(Time.deltaTime, maxSimulationDeltaTime),
                maxBurningTemperature = maxBurningTemperature
            };

            job.Schedule(currentGrid.Length, fireBatchSize).Complete();
            SwapGrids();
        }
    }

    void SwapGrids()
    {
        (currentGrid, nextGrid) = (nextGrid, currentGrid);
    }

    // ─── Public Query API (safe to call from main thread between frames) ──────

    public int GetCellState(int index)
    {
        if (!currentGrid.IsCreated || (uint)index >= (uint)currentGrid.Length) return 0;
        return currentGrid[index].state;
    }

    public float GetCellTemperature(int index)
    {
        if (!currentGrid.IsCreated || (uint)index >= (uint)currentGrid.Length) return 0f;
        return currentGrid[index].temperature;
    }

    public void IgniteCell(int x, int y)
    {
        if (!currentGrid.IsCreated) return;
        int cx = Mathf.Clamp(x, 0, width  - 1);
        int cy = Mathf.Clamp(y, 0, height - 1);
        int idx = cy * width + cx;
        var c = currentGrid[idx];
        if (c.state != 2) { c.state = 2; currentGrid[idx] = c; }
    }

    // ─── Cleanup ──────────────────────────────────────────────────────────────

    void OnDestroy()
    {
        if (currentGrid.IsCreated)      currentGrid.Dispose();
        if (nextGrid.IsCreated)         nextGrid.Dispose();
        if (runtimeMaterials.IsCreated) runtimeMaterials.Dispose();
        if (neighborIndices.IsCreated)  neighborIndices.Dispose();

        gridBuffer?.Release();

        if (heatmapRT != null)
        {
            heatmapRT.Release();
            heatmapRT = null;
        }
    }
}

/// <summary>
/// Inspector-friendly zone definition. Rect is in normalized 0-1 grid space:
///   (0,0) = bottom-left corner of the grid
///   (1,1) = top-right corner of the grid
///
/// Zone Layout (painter model — later entries override earlier ones):
///   Entry 0  →  X=0  Y=0  W=1  H=1  mat=0   — whole grid, base material
///   Entry 1  →  X=0  Y=0.66  W=1  H=0.34  mat=1  — top third, different material
///   Entry 2  →  X=0.4  Y=0.4  W=0.2  H=0.2  mat=2  — small centre patch
/// </summary>
[System.Serializable]
public struct FireZoneDefinition
{
    [Tooltip("Normalized rect in grid space. X/Y = bottom-left corner (0-1), W/H = size (0-1).")]
    public Rect normalizedRect;

    [Tooltip("Index into the Material Definitions array on the controller.")]
    public int materialIndex;
}
