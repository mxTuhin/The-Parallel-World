using UnityEngine;

/// <summary>
/// World ↔ grid coordinate bridge for the fire simulation.
///
/// Uses Renderer.bounds (always world-space, rotation-invariant) rather than
/// localScale so the mapping is correct for Quads in any orientation.
/// </summary>
public class FireZoneController : MonoBehaviour
{
    [Header("References")]
    public FireSimulationControllerGPUCompute fireSim;

    [Tooltip("The Quad/Plane that the fire heatmap texture is projected onto.")]
    public Transform gridPlane;

    [Header("Battle Royale Border")]
    public bool igniteOnStart = true;
    [Range(1, 30)]
    public int borderWidth = 3;

    private Renderer _gridRenderer;

    void Awake()
    {
        CacheRenderer();
    }

    void Start()
    {
        if (igniteOnStart)
            IgniteBorder();
    }

    void CacheRenderer()
    {
        if (gridPlane != null)
            _gridRenderer = gridPlane.GetComponent<Renderer>();
    }

    // Returns world-space AABB of the grid quad.
    // Using bounds means rotation/scale are baked in — works for any Quad orientation.
    Bounds GridBounds()
    {
        if (_gridRenderer == null) CacheRenderer();
        return _gridRenderer != null ? _gridRenderer.bounds : new Bounds(Vector3.zero, Vector3.one);
    }

    // Smallest world-space dimension of a single grid cell.
    public float CellWorldSize
    {
        get
        {
            if (fireSim == null) return 1f;
            var b = GridBounds();
            return Mathf.Min(b.size.x / fireSim.width, b.size.z / fireSim.height);
        }
    }

    // ─── Border ignition ──────────────────────────────────────────────────────

    void IgniteBorder()
    {
        if (fireSim == null) return;
        int w = fireSim.width;
        int h = fireSim.height;

        for (int x = 0; x < w; x++)
            for (int b = 0; b < borderWidth; b++)
            {
                fireSim.IgniteCell(x, b);
                fireSim.IgniteCell(x, h - 1 - b);
            }

        for (int y = borderWidth; y < h - borderWidth; y++)
            for (int b = 0; b < borderWidth; b++)
            {
                fireSim.IgniteCell(b,         y);
                fireSim.IgniteCell(w - 1 - b, y);
            }
    }

    // ─── Coordinate conversion ────────────────────────────────────────────────

    /// <summary>
    /// Maps a world position to grid (gx, gy). Returns false if outside the quad.
    /// </summary>
    public bool TryWorldToGrid(Vector3 worldPos, out int gx, out int gy)
    {
        gx = gy = 0;
        if (fireSim == null) return false;

        Bounds b = GridBounds();

        // Normalized 0-1 position within the quad bounds
        float normX = (worldPos.x - b.min.x) / b.size.x;
        float normZ = (worldPos.z - b.min.z) / b.size.z;

        if (normX < 0f || normX >= 1f || normZ < 0f || normZ >= 1f) return false;

        gx = Mathf.Clamp(Mathf.FloorToInt(normX         * fireSim.width),  0, fireSim.width  - 1);
        // Mirror the GridToWorld flip so world↔grid is a true inverse.
        gy = Mathf.Clamp(Mathf.FloorToInt((1f - normZ)  * fireSim.height), 0, fireSim.height - 1);
        return true;
    }

    /// <summary>
    /// Converts grid cell (gx, gy) to world-space centre of that cell, at ground height.
    /// </summary>
    public Vector3 GridToWorld(int gx, int gy)
    {
        if (fireSim == null) return Vector3.zero;

        Bounds b = GridBounds();

        float normX = (gx + 0.5f) / fireSim.width;
        // Flip Z: the compute shader writes Row[Height-1-y], so gy=0 is the visual
        // top of the texture which maps to world Z=max, not Z=min.
        float normZ = 1f - (gy + 0.5f) / fireSim.height;

        return new Vector3(
            b.min.x + normX * b.size.x,
            b.center.y,
            b.min.z + normZ * b.size.z
        );
    }

    // ─── State queries ────────────────────────────────────────────────────────

    public bool IsPositionBurning(Vector3 worldPos)
    {
        if (!TryWorldToGrid(worldPos, out int gx, out int gy)) return false;
        return fireSim.GetCellState(gy * fireSim.width + gx) == 2;
    }

    public bool IsPositionOnFire(Vector3 worldPos)
    {
        if (!TryWorldToGrid(worldPos, out int gx, out int gy)) return false;
        int s = fireSim.GetCellState(gy * fireSim.width + gx);
        return s == 2 || s == 3;
    }

    public float GetHeatNormalized(Vector3 worldPos)
    {
        if (!TryWorldToGrid(worldPos, out int gx, out int gy)) return 0f;
        float t = fireSim.GetCellTemperature(gy * fireSim.width + gx);
        return Mathf.Clamp01(t / Mathf.Max(0.001f, fireSim.heatMax));
    }

    // ─── Debug helper ─────────────────────────────────────────────────────────

    [ContextMenu("Debug: Print Grid Bounds")]
    void DebugPrintBounds()
    {
        Bounds b = GridBounds();
        Debug.Log($"[FireZone] Bounds center={b.center}  size={b.size}  " +
                  $"cellWorldSize={CellWorldSize:F4}  " +
                  $"grid={fireSim?.width}×{fireSim?.height}");
    }
}
