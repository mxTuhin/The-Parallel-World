using UnityEngine;

[CreateAssetMenu(menuName = "Fire/Material Definition")]
public class FireMaterialDefinition : ScriptableObject
{
    public string materialName;

    public float ignitionTemperature = 3f;
    public float heatAbsorption = 1f;
    public float spreadMultiplier = 1f;

    public float heatEmission = 5f;
    public float coolingRate = 0.5f;
    
    public float coolingVisualDelay = 0.5f;

    public float fuelAmount = 5f;

    public bool isWall = false;
}