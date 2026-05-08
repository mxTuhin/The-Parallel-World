using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// CPU-only fire simulation backend.
///
/// Fire spread  : Burst IJobParallelFor (FireSpreadJob) — parallelism controlled
///                by coreCount / JobsUtility.JobWorkerCount.
/// Heatmap      : CPU pixel loop → Texture2D.SetPixelData (single-threaded).
/// Burning scan : CPU loop in FireParticleVisualizer — O(width × height) per resample.
///
/// Implements IFireSimulation so all game systems work identically with either backend.
/// </summary>
public class FireSimulationController : MonoBehaviour, IFireSimulation
{
    [Header("Grid")]
    public int width  = 256;
    public int height = 256;

    [Header("Display")]
    public Renderer targetRenderer;
    public FilterMode filterMode = FilterMode.Point;

    [Header("Heatmap")]
    [Tooltip("Temperature value that saturates the heatmap colour gradient. " +
             "Set close to ignitionTemperature so the warm-up phase is visible.")]
    public float heatMax = 10f;

    [Header("Material Library")]
    public FireMaterialDefinition[] materialDefinitions;

    [Header("Zone Layout")]
    [Tooltip("Same painter-model zone system as the GPU controller.\n" +
             "If empty, all cells use material 0.")]
    public FireZoneDefinition[] zones;

    [Header("Parallel Control (REAL CORE CONTROL)")]
    [Range(1, 8)]
    public int coreCount = 4;

    [Header("Simulation")]
    public float diffusionRate = 2f;
    public float burnRate      = 0.5f;

    [Header("Start Fire")]
    public Vector2Int[] startFireCells;

    // ── IFireSimulation ───────────────────────────────────────────────────────
    public int          Width          => width;
    public int          Height         => height;
    public float        HeatMax        => heatMax;
    public Vector2Int[] StartFireCells => startFireCells;
    public bool         IsGPUMode      => false;
    public int          CoreCount      => coreCount;

    // ── NativeArrays ──────────────────────────────────────────────────────────
    private NativeArray<FireCell>            currentGrid;
    private NativeArray<FireCell>            nextGrid;
    private NativeArray<FireMaterialRuntime> runtimeMaterials;
    private NativeArray<int>                 neighborIndices;

    private Texture2D heatmapTexture;
    private Color32[] pixelBuffer;

    // Profiler marker — same name as GPU controller so SimulationBenchmark
    // can record both modes with the same ProfilerRecorder query.
    private static readonly ProfilerMarker SimMarker =
        new ProfilerMarker("FireSim.Schedule+Complete");

    private static readonly Color32 WallColor    = new Color32(128,128,128,255);
    private static readonly Color32 EmptyColor   = new Color32(  0,255,  0,255);
    private static readonly Color32 BurningColor = new Color32(255,  0,  0,255);
    private static readonly Color32 BlackColor   = new Color32(  0,  0,  0,255);
    private static readonly Color32 BlueColor    = new Color32(  0,  0,255,255);
    private static readonly Color32 CyanColor    = new Color32(  0,255,255,255);
    private static readonly Color32 WhiteColor   = new Color32(255,255,255,255);
    private static readonly Color32 YellowColor  = new Color32(255,255,  0,255);
    private static readonly Color32 OrangeColor  = new Color32(255,128,  0,255);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake() => ApplyCoreCount();

    void OnValidate()
    {
        if (Application.isPlaying) ApplyCoreCount();
    }

    void ApplyCoreCount()
    {
        int clamped = Mathf.Clamp(coreCount, 1, SystemInfo.processorCount);
        JobsUtility.JobWorkerCount = clamped - 1;
        Debug.Log($"[FireSim CPU] Worker threads: {clamped}");
    }

    void Start()
    {
        int total = width * height;
        currentGrid      = new NativeArray<FireCell>(total, Allocator.Persistent);
        nextGrid         = new NativeArray<FireCell>(total, Allocator.Persistent);

        InitializeMaterials();
        InitializeGrid();
        BuildNeighborIndices();
        ApplyStartFire();
        InitializeHeatmap();
        UpdateHeatmapVisuals();
    }

    void InitializeMaterials()
    {
        runtimeMaterials = new NativeArray<FireMaterialRuntime>(
            materialDefinitions.Length, Allocator.Persistent);

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
                coolingVisualDelay  = def.coolingVisualDelay,
                fuelAmount          = def.fuelAmount,
                isWall              = def.isWall ? 1 : 0
            };
        }
    }

    void InitializeGrid()
    {
        int zoneCount    = zones?.Length ?? 0;
        var zoneRuntimes = new NativeArray<FireZoneRuntime>(zoneCount, Allocator.TempJob);

        for (int i = 0; i < zoneCount; i++)
        {
            var z      = zones[i];
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

        new GridInitializationJob
        {
            grid          = currentGrid,
            materials     = runtimeMaterials,
            zones         = zoneRuntimes,
            materialCount = materialDefinitions.Length,
            width         = width,
            height        = height
        }.Schedule(currentGrid.Length, 64).Complete();

        zoneRuntimes.Dispose();
    }

    void BuildNeighborIndices()
    {
        neighborIndices = new NativeArray<int>(width * height * 8, Allocator.Persistent);
        int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dy = { -1,-1,-1,  0, 0,  1, 1, 1 };

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width;  x++)
        {
            int index      = y * width + x;
            int baseOffset = index * 8;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + dx[k];
                int ny = y + dy[k];
                neighborIndices[baseOffset + k] =
                    (nx < 0 || nx >= width || ny < 0 || ny >= height)
                    ? -1 : ny * width + nx;
            }
        }
    }

    void InitializeHeatmap()
    {
        pixelBuffer    = new Color32[width * height];
        heatmapTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        heatmapTexture.filterMode = filterMode;
        heatmapTexture.wrapMode   = TextureWrapMode.Clamp;

        if (targetRenderer != null)
            targetRenderer.material.mainTexture = heatmapTexture;
        else
            Debug.LogWarning("[FireSim CPU] Target Renderer is not assigned.");
    }

    void ApplyStartFire()
    {
        if (startFireCells == null) return;
        foreach (var pos in startFireCells)
        {
            int cx  = Mathf.Clamp(pos.x, 0, width  - 1);
            int cy  = Mathf.Clamp(pos.y, 0, height - 1);
            int idx = cy * width + cx;
            var c   = currentGrid[idx];
            c.state = 2;
            currentGrid[idx] = c;
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    void Update()
    {
        using (SimMarker.Auto())
        {
            new FireSpreadJob
            {
                currentGrid           = currentGrid,
                nextGrid              = nextGrid,
                materials             = runtimeMaterials,
                neighborIndices       = neighborIndices,
                width                 = width,
                height                = height,
                diffusionRate         = diffusionRate,
                burnRate              = burnRate,
                deltaTime             = Time.deltaTime,
                maxBurningTemperature = 20f
            }.Schedule(currentGrid.Length, 64).Complete();
        }

        (currentGrid, nextGrid) = (nextGrid, currentGrid);
        UpdateHeatmapVisuals();
    }

    void UpdateHeatmapVisuals()
    {
        for (int i = 0; i < currentGrid.Length; i++)
        {
            var data = currentGrid[i];
            var mat  = runtimeMaterials[data.materialIndex];
            Color32 color;

            if (mat.isWall == 1)
            {
                color = WallColor;
            }
            else if (data.state == 2)
            {
                color = BurningColor;
            }
            else if (data.state == 3)
            {
                if (data.burnFinishedTime < 0.4f)
                    color = BlackColor;
                else if (data.temperature > 0.01f)
                {
                    float p = 1f - Mathf.Clamp01(
                        data.temperature / Mathf.Max(0.0001f, data.coolingStartTemperature));
                    if      (p < 0.33f) color = LerpColor32(BlackColor, BlueColor,  p * 3f);
                    else if (p < 0.66f) color = LerpColor32(BlueColor,  CyanColor,  (p - 0.33f) * 3f);
                    else                color = LerpColor32(CyanColor,   WhiteColor, (p - 0.66f) * 3f);
                }
                else
                    color = BlackColor;
            }
            else if (data.state == 1 && data.temperature > 0.05f)
            {
                float h = Mathf.Clamp01(data.temperature / Mathf.Max(0.0001f, mat.ignitionTemperature));
                if      (h < 0.33f) color = LerpColor32(EmptyColor,  YellowColor, h * 3f);
                else if (h < 0.66f) color = LerpColor32(YellowColor, OrangeColor, (h - 0.33f) * 3f);
                else                color = LerpColor32(OrangeColor, BurningColor,(h - 0.66f) * 3f);
            }
            else
            {
                color = EmptyColor;
            }

            int x = i % width;
            int y = i / width;
            pixelBuffer[(height - 1 - y) * width + x] = color;
        }

        heatmapTexture.SetPixelData(pixelBuffer, 0);
        heatmapTexture.Apply(false, false);
    }

    static Color32 LerpColor32(Color32 a, Color32 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Color32(
            (byte)Mathf.RoundToInt(Mathf.Lerp(a.r, b.r, t)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(a.g, b.g, t)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(a.b, b.b, t)),
            255);
    }

    // ── IFireSimulation ───────────────────────────────────────────────────────

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
        int idx = Mathf.Clamp(y, 0, height - 1) * width + Mathf.Clamp(x, 0, width - 1);
        var c = currentGrid[idx];
        if (c.state != 2) { c.state = 2; currentGrid[idx] = c; }
    }

    public void SetCoreCount(int count)
    {
        coreCount = Mathf.Clamp(count, 1, SystemInfo.processorCount);
        ApplyCoreCount();
    }

    // CPU mode — no GPU burning-cell buffers
    public ComputeBuffer GetBurningCellsBuffer()      => null;
    public ComputeBuffer GetBurningCellsCountBuffer() => null;

    // ── Inner jobs ────────────────────────────────────────────────────────────

    [BurstCompile]
    public struct GridInitializationJob : IJobParallelFor
    {
        public  NativeArray<FireCell>            grid;
        [ReadOnly] public NativeArray<FireMaterialRuntime> materials;
        [ReadOnly] public NativeArray<FireZoneRuntime>     zones;
        public int materialCount, width, height;

        public void Execute(int index)
        {
            int x = index % width;
            int y = index / width;

            // Painter model: later zones override earlier ones.
            // If no zones are defined, all cells get material 0.
            int matIndex = 0;
            for (int z = 0; z < zones.Length; z++)
            {
                var zone = zones[z];
                if (x >= zone.xMin && x < zone.xMax &&
                    y >= zone.yMin && y < zone.yMax)
                    matIndex = math.clamp(zone.materialIndex, 0, materialCount - 1);
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

    // ── Cleanup ───────────────────────────────────────────────────────────────

    void OnDestroy()
    {
        if (currentGrid.IsCreated)      currentGrid.Dispose();
        if (nextGrid.IsCreated)         nextGrid.Dispose();
        if (runtimeMaterials.IsCreated) runtimeMaterials.Dispose();
        if (neighborIndices.IsCreated)  neighborIndices.Dispose();
        if (heatmapTexture != null)     Destroy(heatmapTexture);
    }
}
