using Unity.Burst;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;

public class FireSimulationController : MonoBehaviour
{
    [Header("Grid")]
    public int width = 20;
    public int height = 20;
    public GameObject cellPrefab;

    [Header("Material Library")]
    public FireMaterialDefinition[] materialDefinitions;

    [Header("Parallel Control (REAL CORE CONTROL)")]
    [Range(0,8)]
    public int coreCount = 4;

    [Header("Simulation")]
    public float diffusionRate = 2f;
    public float burnRate = 0.5f;

    [Header("Start Fire")]
    public Vector2Int[] startFireCells;

    NativeArray<FireCell> currentGrid;
    NativeArray<FireCell> nextGrid;
    NativeArray<FireMaterialRuntime> runtimeMaterials;

    Renderer[] renderers;

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

        // main thread counts as one
        JobsUtility.JobWorkerCount = clamped - 1;

        Debug.Log("Active Logical Cores: " + clamped);
    }

    void Start()
    {
        int total = width * height;

        currentGrid = new NativeArray<FireCell>(total, Allocator.Persistent);
        nextGrid = new NativeArray<FireCell>(total, Allocator.Persistent);

        renderers = new Renderer[total];

        InitializeMaterials();
        CreateGrid();
        ApplyStartFire();
    }

    void InitializeMaterials()
    {
        runtimeMaterials = new NativeArray<FireMaterialRuntime>(
            materialDefinitions.Length,
            Allocator.Persistent);

        for(int i=0;i<materialDefinitions.Length;i++)
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

    void CreateGrid()
    {
        int total = width * height;

        // 1️⃣ Parallel initialize grid data
        var initJob = new GridInitializationJob
        {
            grid = currentGrid,
            materials = runtimeMaterials,
            width = width,
            height = height
        };

        JobHandle handle = initJob.Schedule(total, 64);
        handle.Complete();

        // 2️⃣ Main thread: create GameObjects
        for(int i = 0; i < total; i++)
        {
            int x = i % width;
            int y = i / width;

            GameObject obj = Instantiate(cellPrefab);
            obj.transform.position = new Vector3(x,0,y);

            renderers[i] = obj.GetComponent<Renderer>();
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
                fuel = materials[materialIndex].fuelAmount
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
        foreach(var pos in startFireCells)
        {
            int index = pos.y*width + pos.x;

            if(index>=0 && index<currentGrid.Length)
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
            width = width,
            height = height,
            diffusionRate = diffusionRate,
            burnRate = burnRate,
            deltaTime = Time.deltaTime
        };

        // fixed batch size for stable scaling
        int batchSize = 64;

        JobHandle handle = job.Schedule(total, batchSize);

        handle.Complete();

        SwapGrids();
        UpdateVisuals();
    }

    void SwapGrids()
    {
        var temp = currentGrid;
        currentGrid = nextGrid;
        nextGrid = temp;
    }

    void UpdateVisuals()
    {
        for(int i=0;i<currentGrid.Length;i++)
        {
            var data = currentGrid[i];
            var mat = runtimeMaterials[data.materialIndex];

            Renderer r = renderers[i];

            if(mat.isWall == 1)
            {
                r.material.color = Color.gray;
                continue;
            }

            if(data.state == 2)
            {
                r.material.color = Color.red;
                continue;
            }

            if(data.state == 3)
            {
                if(data.burnFinishedTime < 0.4f)
                {
                    r.material.color = Color.black;
                    continue;
                }

                if(data.temperature > 0.01f)
                {
                    float progress = 1f -
                        Mathf.Clamp01(
                            data.temperature /
                            Mathf.Max(0.0001f, data.coolingStartTemperature)
                        );

                    Color coolingColor;

                    if(progress < 0.33f)
                        coolingColor = Color.Lerp(Color.black, Color.blue, progress * 3f);
                    else if(progress < 0.66f)
                        coolingColor = Color.Lerp(Color.blue, Color.cyan, (progress - 0.33f) * 3f);
                    else
                        coolingColor = Color.Lerp(Color.cyan, Color.white, (progress - 0.66f) * 3f);

                    r.material.color = coolingColor;
                }
                else
                {
                    r.material.color = Color.black;
                }

                continue;
            }

            if(data.state == 1 && data.temperature > 0.05f)
            {
                float heatRatio =
                    Mathf.Clamp01(data.temperature / mat.ignitionTemperature);

                Color heatColor;

                if(heatRatio < 0.33f)
                    heatColor = Color.Lerp(Color.green, Color.yellow, heatRatio*3f);
                else if(heatRatio < 0.66f)
                    heatColor = Color.Lerp(Color.yellow, new Color(1f,0.5f,0f), (heatRatio-0.33f)*3f);
                else
                    heatColor = Color.Lerp(new Color(1f,0.5f,0f), Color.red, (heatRatio-0.66f)*3f);

                r.material.color = heatColor;
                continue;
            }

            r.material.color = Color.green;
        }
    }

    void OnDestroy()
    {
        if(currentGrid.IsCreated) currentGrid.Dispose();
        if(nextGrid.IsCreated) nextGrid.Dispose();
        if(runtimeMaterials.IsCreated) runtimeMaterials.Dispose();
    }
}