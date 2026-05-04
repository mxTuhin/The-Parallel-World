using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

public class FireSimulationController : MonoBehaviour
{
    [Header("Grid")]
    public int width = 256;
    public int height = 256;

    [Header("Display")]
    public Renderer targetRenderer;
    public FilterMode filterMode = FilterMode.Point;

    [Header("Material Library")]
    public FireMaterialDefinition[] materialDefinitions;

    [Header("Parallel Control (REAL CORE CONTROL)")]
    [Range(1, 8)]
    public int coreCount = 4;

    [Header("Simulation")]
    public float diffusionRate = 2f;
    public float burnRate = 0.5f;

    [Header("Start Fire")]
    public Vector2Int[] startFireCells;

    private NativeArray<FireCell> currentGrid;
    private NativeArray<FireCell> nextGrid;
    private NativeArray<FireMaterialRuntime> runtimeMaterials;
    private NativeArray<int> neighborIndices;   // NEW

    private Texture2D heatmapTexture;
    private Color32[] pixelBuffer;

    private static readonly Color32 WallColor = new Color32(128, 128, 128, 255);
    private static readonly Color32 EmptyColor = new Color32(0, 255, 0, 255);
    private static readonly Color32 BurningColor = new Color32(255, 0, 0, 255);
    private static readonly Color32 BlackColor = new Color32(0, 0, 0, 255);
    private static readonly Color32 BlueColor = new Color32(0, 0, 255, 255);
    private static readonly Color32 CyanColor = new Color32(0, 255, 255, 255);
    private static readonly Color32 WhiteColor = new Color32(255, 255, 255, 255);
    private static readonly Color32 YellowColor = new Color32(255, 255, 0, 255);
    private static readonly Color32 OrangeColor = new Color32(255, 128, 0, 255);

    void Awake()
    {
        ApplyCoreCount();
    }

    void OnValidate()
    {
        if (Application.isPlaying)
            ApplyCoreCount();
    }

    void ApplyCoreCount()
    {
        int logicalCores = SystemInfo.processorCount;
        int clamped = Mathf.Clamp(coreCount, 1, logicalCores);

        JobsUtility.JobWorkerCount = clamped - 1;

        Debug.Log("Active Logical Cores: " + clamped);
    }

    void Start()
    {
        int total = width * height;

        currentGrid = new NativeArray<FireCell>(total, Allocator.Persistent);
        nextGrid = new NativeArray<FireCell>(total, Allocator.Persistent);

        InitializeMaterials();
        InitializeGrid();
        BuildNeighborIndices();   // NEW
        ApplyStartFire();

        InitializeHeatmap();
        UpdateHeatmapVisuals();
    }

    void InitializeMaterials()
    {
        runtimeMaterials = new NativeArray<FireMaterialRuntime>(
            materialDefinitions.Length,
            Allocator.Persistent);

        for (int i = 0; i < materialDefinitions.Length; i++)
        {
            var def = materialDefinitions[i];

            runtimeMaterials[i] = new FireMaterialRuntime
            {
                ignitionTemperature = def.ignitionTemperature,
                heatAbsorption = def.heatAbsorption,
                spreadMultiplier = def.spreadMultiplier,
                heatEmission = def.heatEmission,
                coolingRate = def.coolingRate,
                fuelAmount = def.fuelAmount,
                isWall = (byte)(def.isWall ? 1 : 0)
            };
        }
    }

    void InitializeGrid()
    {
        int total = width * height;

        var initJob = new GridInitializationJob
        {
            grid = currentGrid,
            materials = runtimeMaterials,
            width = width,
            height = height
        };

        JobHandle handle = initJob.Schedule(total, 64);
        handle.Complete();
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
                int index = y * width + x;
                int baseOffset = index * 8;

                for (int k = 0; k < 8; k++)
                {
                    int nx = x + dx[k];
                    int ny = y + dy[k];

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                        neighborIndices[baseOffset + k] = -1;
                    else
                        neighborIndices[baseOffset + k] = ny * width + nx;
                }
            }
        }
    }

    void InitializeHeatmap()
    {
        pixelBuffer = new Color32[width * height];

        heatmapTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        heatmapTexture.filterMode = filterMode;
        heatmapTexture.wrapMode = TextureWrapMode.Clamp;

        if (targetRenderer != null)
        {
            targetRenderer.material.mainTexture = heatmapTexture;
        }
        else
        {
            Debug.LogWarning("Target Renderer is not assigned.");
        }
    }

    [BurstCompile]
    public struct GridInitializationJob : IJobParallelFor
    {
        public NativeArray<FireCell> grid;
        [ReadOnly] public NativeArray<FireMaterialRuntime> materials;

        public int width;
        public int height;

        public void Execute(int index)
        {
            int x = index % width;
            int y = index / width;

            int materialIndex = GetMaterialIndex(x, y);

            grid[index] = new FireCell
            {
                state = 1,
                temperature = 0f,
                materialIndex = materialIndex,
                fuel = materials[materialIndex].fuelAmount,
                burnFinishedTime = 0f,
                coolingStartTemperature = 0f
            };
        }

        int GetMaterialIndex(int x, int y)
        {
            if (x < 5) return 0;
            if (x < 10) return 1;
            if (y > 15) return 2;
            return 0;
        }
    }

    void ApplyStartFire()
    {
        foreach (var pos in startFireCells)
        {
            int index = pos.y * width + pos.x;

            if (index >= 0 && index < currentGrid.Length)
            {
                var c = currentGrid[index];
                c.state = 2;
                currentGrid[index] = c;
            }
        }
    }

    void Update()
    {
        int total = currentGrid.Length;

        var job = new FireSpreadJob
        {
            currentGrid = currentGrid,
            nextGrid = nextGrid,
            materials = runtimeMaterials,
            neighborIndices = neighborIndices,   // NEW
            width = width,
            height = height,
            diffusionRate = diffusionRate,
            burnRate = burnRate,
            deltaTime = Time.deltaTime
        };

        int batchSize = 64;

        JobHandle handle = job.Schedule(total, batchSize);
        handle.Complete();

        SwapGrids();
        UpdateHeatmapVisuals();
    }

    void SwapGrids()
    {
        var temp = currentGrid;
        currentGrid = nextGrid;
        nextGrid = temp;
    }

    void UpdateHeatmapVisuals()
    {
        for (int i = 0; i < currentGrid.Length; i++)
        {
            var data = currentGrid[i];
            var mat = runtimeMaterials[data.materialIndex];

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
                {
                    color = BlackColor;
                }
                else if (data.temperature > 0.01f)
                {
                    float progress = 1f - Mathf.Clamp01(
                        data.temperature / Mathf.Max(0.0001f, data.coolingStartTemperature));

                    if (progress < 0.33f)
                        color = LerpColor32(BlackColor, BlueColor, progress * 3f);
                    else if (progress < 0.66f)
                        color = LerpColor32(BlueColor, CyanColor, (progress - 0.33f) * 3f);
                    else
                        color = LerpColor32(CyanColor, WhiteColor, (progress - 0.66f) * 3f);
                }
                else
                {
                    color = BlackColor;
                }
            }
            else if (data.state == 1 && data.temperature > 0.05f)
            {
                float heatRatio = Mathf.Clamp01(
                    data.temperature / Mathf.Max(0.0001f, mat.ignitionTemperature));

                if (heatRatio < 0.33f)
                    color = LerpColor32(EmptyColor, YellowColor, heatRatio * 3f);
                else if (heatRatio < 0.66f)
                    color = LerpColor32(YellowColor, OrangeColor, (heatRatio - 0.33f) * 3f);
                else
                    color = LerpColor32(OrangeColor, BurningColor, (heatRatio - 0.66f) * 3f);
            }
            else
            {
                color = EmptyColor;
            }

            int x = i % width;
            int y = i / width;
            int flippedIndex = (height - 1 - y) * width + x;

            pixelBuffer[flippedIndex] = color;
        }

        heatmapTexture.SetPixelData(pixelBuffer, 0);
        heatmapTexture.Apply(false, false);
    }

    static Color32 LerpColor32(Color32 a, Color32 b, float t)
    {
        t = Mathf.Clamp01(t);

        byte r = (byte)Mathf.RoundToInt(Mathf.Lerp(a.r, b.r, t));
        byte g = (byte)Mathf.RoundToInt(Mathf.Lerp(a.g, b.g, t));
        byte bl = (byte)Mathf.RoundToInt(Mathf.Lerp(a.b, b.b, t));
        byte al = (byte)Mathf.RoundToInt(Mathf.Lerp(a.a, b.a, t));

        return new Color32(r, g, bl, al);
    }

    void OnDestroy()
    {
        if (currentGrid.IsCreated) currentGrid.Dispose();
        if (nextGrid.IsCreated) nextGrid.Dispose();
        if (runtimeMaterials.IsCreated) runtimeMaterials.Dispose();
        if (neighborIndices.IsCreated) neighborIndices.Dispose();

        if (heatmapTexture != null)
        {
            Destroy(heatmapTexture);
        }
    }
}