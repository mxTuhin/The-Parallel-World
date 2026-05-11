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
    [ReadOnly] public NativeArray<int> neighborIndices; // 8 neighbors per cell

    public int width;
    public int height;

    public float diffusionRate;
    public float burnRate;
    public float deltaTime;

    // Caps how hot a burning cell can get, which caps how much heat it radiates.
    // Without this, temperature grows unboundedly → cascade where every cell ignites
    // within seconds regardless of material ignitionTemperature settings.
    public float maxBurningTemperature;

    public void Execute(int index)
    {
        FireCell cell = currentGrid[index];
        FireMaterialRuntime mat = materials[cell.materialIndex];

        if (mat.isWall == 1)
        {
            nextGrid[index] = cell;
            return;
        }

        float diffusionHeat = GetDiffusionHeat(index);

        cell.temperature += diffusionHeat
                            * diffusionRate
                            * mat.heatAbsorption
                            * deltaTime;

        if (cell.state == 2)
        {
            // Only emit heat up to the cap — burning cells can radiate no more than this
            if (cell.temperature < maxBurningTemperature)
                cell.temperature += mat.heatEmission * deltaTime;

            cell.fuel -= burnRate * deltaTime;

            if (cell.fuel <= 0f)
            {
                cell.state = 3;
                cell.burnFinishedTime = 0f;
                cell.coolingStartTemperature = cell.temperature;
            }
        }

        if (cell.state == 3)
        {
            cell.burnFinishedTime += deltaTime;
        }

        cell.temperature -= mat.coolingRate * cell.temperature * deltaTime;
        cell.temperature = math.max(0f, cell.temperature);

        if (cell.state == 1 && cell.temperature >= mat.ignitionTemperature)
        {
            cell.state = 2;
        }

        nextGrid[index] = cell;
    }

    float GetDiffusionHeat(int index)
    {
        float heat = 0f;
        int baseOffset = index * 8;

        for (int k = 0; k < 8; k++)
        {
            int nIndex = neighborIndices[baseOffset + k];
            if (nIndex < 0) continue;

            FireCell neighbor = currentGrid[nIndex];
            FireMaterialRuntime nMat = materials[neighbor.materialIndex];
            heat += neighbor.temperature * nMat.spreadMultiplier;
        }

        return heat * 0.125f;
    }
}