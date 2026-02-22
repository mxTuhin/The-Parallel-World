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

public struct FireCell
{
    public byte state;

    public float temperature;
    public float fuel;

    public int materialIndex;

    public float burnFinishedTime;
    public float coolingStartTemperature; // NEW
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