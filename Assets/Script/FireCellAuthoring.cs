using System.Runtime.InteropServices;
using UnityEngine;

public class FireCellAuthoring : MonoBehaviour
{
    public FireMaterialDefinition materialDefinition;

    public bool startBurning = false;

    Renderer rend;

    void Awake()
    {
        rend = GetComponent<Renderer>();
    }

    public void SetColor(Color c)
    {
        rend.material.color = c;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct FireCell
{
    public int state;
    public float temperature;
    public int materialIndex;
    public float fuel;
    public float burnFinishedTime;
    public float coolingStartTemperature;
}

public struct FireMaterialRuntime
{
    public float ignitionTemperature;
    public float heatAbsorption;
    public float spreadMultiplier;
    public float heatEmission;
    public float coolingRate;
    public float coolingVisualDelay;
    public float fuelAmount;
    public byte isWall;
}

// Burst-compatible zone record — converted from FireZoneDefinition at runtime
public struct FireZoneRuntime
{
    public int xMin, yMin, xMax, yMax;
    public int materialIndex;
}