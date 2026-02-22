using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct FireSpreadJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<FireCell> currentGrid;
    public NativeArray<FireCell> nextGrid;

    [ReadOnly] public NativeArray<FireMaterialRuntime> materials;

    public int width;
    public int height;

    public float diffusionRate;
    public float burnRate;
    public float deltaTime;

    public void Execute(int index)
    {
        FireCell cell = currentGrid[index];
        FireMaterialRuntime mat = materials[cell.materialIndex];

        // Wall — skip computation
        if (mat.isWall == 1)
        {
            nextGrid[index] = cell;
            return;
        }

        int localWidth = width;

        int x = index % localWidth;
        int y = index / localWidth;

        float diffusionHeat = GetDiffusionHeat(x, y);

        // ------------------------
        // Heat Diffusion
        // ------------------------
        cell.temperature += diffusionHeat
                            * diffusionRate
                            * mat.heatAbsorption
                            * deltaTime;

        // ------------------------
        // Burning
        // ------------------------
        if (cell.state == 2)
        {
            cell.temperature += mat.heatEmission * deltaTime;
            cell.fuel -= burnRate * deltaTime;

            if (cell.fuel <= 0f)
            {
                cell.state = 3;
                cell.burnFinishedTime = 0f;
                cell.coolingStartTemperature = cell.temperature;
            }
        }

        // ------------------------
        // Post-burn timer
        // ------------------------
        if (cell.state == 3)
        {
            cell.burnFinishedTime += deltaTime;
        }

        // ------------------------
        // Cooling
        // ------------------------
        cell.temperature -= mat.coolingRate * cell.temperature * deltaTime;
        cell.temperature = math.max(0f, cell.temperature);

        // ------------------------
        // Ignition
        // ------------------------
        if (cell.state == 1 && cell.temperature >= mat.ignitionTemperature)
        {
            cell.state = 2;
        }

        nextGrid[index] = cell;
    }

    float GetDiffusionHeat(int x, int y)
    {
        float heat = 0f;
        int localWidth = width;
        int localHeight = height;

        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if (ny < 0 || ny >= localHeight)
                continue;

            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                int nx = x + dx;
                if (nx < 0 || nx >= localWidth)
                    continue;

                int idx = ny * localWidth + nx;

                FireCell neighbor = currentGrid[idx];
                FireMaterialRuntime nMat = materials[neighbor.materialIndex];

                heat += neighbor.temperature * nMat.spreadMultiplier;
            }
        }

        return heat * 0.125f;
    }
}