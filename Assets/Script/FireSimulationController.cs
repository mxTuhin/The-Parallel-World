using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using System.Collections.Generic;

public class FireSimulationController : MonoBehaviour
{
    [Header("Grid Size")]
    public int width = 50;
    public int height = 50;

    [Header("Chunk Settings")]
    public int chunkSize = 10;

    [Header("Parallel Control")]
    public int virtualCoreCount = 4;

    [Header("Fire Settings")]
    public float ignitionThreshold = 0.3f;
    public float burnRate = 0.02f;

    [Header("Colors")]
    public Color fuelColor = Color.green;
    public Color burningColor = Color.red;
    public Color burnedColor = Color.black;
    public Color wallColor = Color.gray;

    NativeArray<FireCell> currentGrid;
    NativeArray<FireCell> nextGrid;

    FireCellAuthoring[] cells;

    struct ChunkInfo
    {
        public int startX;
        public int startY;
    }

    List<ChunkInfo> chunks = new List<ChunkInfo>();

    void Start()
    {
        cells = FindObjectsOfType<FireCellAuthoring>();

        int total = width * height;

        currentGrid = new NativeArray<FireCell>(total, Allocator.Persistent);
        nextGrid = new NativeArray<FireCell>(total, Allocator.Persistent);

        InitializeFromAuthoring();
        CreateChunks();
    }

    void InitializeFromAuthoring()
    {
        for (int i = 0; i < cells.Length; i++)
        {
            var a = cells[i];

            currentGrid[i] = new FireCell
            {
                state = (byte)(a.startBurning ? 2 : 1),
                fuel = a.fuelAmount,
                isWall = (byte)(a.isWall ? 1 : 0),
                explosive = (byte)(a.explosive ? 1 : 0),
                explosionBoost = a.explosionBoost
            };
        }
    }

    void CreateChunks()
    {
        chunks.Clear();

        for (int y = 0; y < height; y += chunkSize)
        {
            for (int x = 0; x < width; x += chunkSize)
            {
                chunks.Add(new ChunkInfo
                {
                    startX = x,
                    startY = y
                });
            }
        }
    }

    void Update()
    {
        JobHandle handle = default;

        int activeChunks = Mathf.Min(virtualCoreCount, chunks.Count);

        for (int i = 0; i < activeChunks; i++)
        {
            var chunk = chunks[i];

            var job = new FireChunkJob
            {
                currentGrid = currentGrid,
                nextGrid = nextGrid,
                width = width,
                height = height,
                chunkStartX = chunk.startX,
                chunkStartY = chunk.startY,
                chunkSize = chunkSize,
                ignitionThreshold = ignitionThreshold,
                burnRate = burnRate
            };

            handle = job.Schedule(handle);
        }

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
        for (int i = 0; i < cells.Length; i++)
        {
            var data = currentGrid[i];

            if (data.isWall == 1)
                cells[i].SetColor(wallColor);
            else if (data.state == 1)
                cells[i].SetColor(fuelColor);
            else if (data.state == 2)
                cells[i].SetColor(burningColor);
            else if (data.state == 3)
                cells[i].SetColor(burnedColor);
        }
    }

    void OnDestroy()
    {
        if (currentGrid.IsCreated) currentGrid.Dispose();
        if (nextGrid.IsCreated) nextGrid.Dispose();
    }
}