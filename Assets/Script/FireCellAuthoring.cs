using UnityEngine;

public class FireCellAuthoring : MonoBehaviour
{
    [Header("Cell Properties")]
    public bool isWall = false;
    public bool canBurn = true;

    [Range(0f, 5f)]
    public float fuelAmount = 1f;

    public bool explosive = false;
    public float explosionBoost = 3f;

    [Header("Initial State")]
    public bool startBurning = false;

    Renderer rend;

    void Awake()
    {
        rend = GetComponent<Renderer>();
    }

    public void SetColor(Color c)
    {
        if (rend != null)
            rend.material.color = c;
    }
}