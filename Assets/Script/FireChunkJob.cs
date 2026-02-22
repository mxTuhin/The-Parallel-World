using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

public struct FireCell
{
    public byte state; // 0 empty, 1 fuel, 2 burning, 3 burned
    public float fuel;
    public byte isWall;
    public byte explosive;
    public float explosionBoost;
}

[BurstCompile]
public struct FireChunkJob : IJob
{
    [ReadOnly] public NativeArray<FireCell> currentGrid;
    public NativeArray<FireCell> nextGrid;

    public int width;
    public int height;

    public int chunkStartX;
    public int chunkStartY;
    public int chunkSize;

    public float ignitionThreshold;
    public float burnRate;

    public void Execute()
    {
        for (int y = 0; y < chunkSize; y++)
        {
            for (int x = 0; x < chunkSize; x++)
            {
                int gx = chunkStartX + x;
                int gy = chunkStartY + y;

                if (gx >= width || gy >= height)
                    continue;

                int index = gy * width + gx;

                FireCell cell = currentGrid[index];

                if (cell.isWall == 1)
                {
                    nextGrid[index] = cell;
                    continue;
                }

                if (cell.state == 2) // burning
                {
                    cell.fuel -= burnRate;

                    if (cell.fuel <= 0)
                        cell.state = 3;
                }
                else if (cell.state == 1) // fuel
                {
                    float heat = GetNeighborHeat(gx, gy);

                    if (cell.explosive == 1)
                        heat *= cell.explosionBoost;

                    if (heat > ignitionThreshold)
                        cell.state = 2;
                }

                nextGrid[index] = cell;
            }
        }
    }

    float GetNeighborHeat(int x, int y)
    {
        float heat = 0f;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;

                int nx = x + dx;
                int ny = y + dy;

                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    continue;

                int nIndex = ny * width + nx;

                if (currentGrid[nIndex].state == 2)
                    heat += 0.25f;
            }
        }

        return heat;
    }
}