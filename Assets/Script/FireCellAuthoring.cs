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

/// <summary>
/// Sequential layout required so Marshal.SizeOf and GPU StructuredBuffer stride agree.
/// 7 floats + 1 int = 8 × 4 = 32 bytes  (matches FireMaterial struct in FireSimulation.compute).
/// isWall was formerly 'byte' — changed to 'int' to align to 4-byte GPU boundary.
/// </summary>
[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct FireMaterialRuntime
{
    public float ignitionTemperature;
    public float heatAbsorption;
    public float spreadMultiplier;
    public float heatEmission;
    public float coolingRate;
    public float coolingVisualDelay;
    public float fuelAmount;
    public int   isWall;   // 0 = flammable, 1 = wall  (int keeps 4-byte GPU alignment)
}

// Burst-compatible zone record — converted from FireZoneDefinition at runtime
public struct FireZoneRuntime
{
    public int xMin, yMin, xMax, yMax;
    public int materialIndex;
}

/// <summary>
/// Inspector-friendly zone definition.  Rect is in normalised 0-1 grid space:
///   (0,0) = bottom-left corner   (1,1) = top-right corner.
/// Shared by both FireSimulationController (CPU) and FireSimulationControllerGPUCompute (CPU+GPU).
/// </summary>
[System.Serializable]
public struct FireZoneDefinition
{
    [UnityEngine.Tooltip("Normalised rect in grid space.  X/Y = bottom-left (0-1),  W/H = size (0-1).")]
    public UnityEngine.Rect normalizedRect;
    [UnityEngine.Tooltip("Index into the Material Definitions array on the controller.")]
    public int materialIndex;
}