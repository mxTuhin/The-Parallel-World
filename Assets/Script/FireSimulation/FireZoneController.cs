using UnityEngine;

/// <summary>
/// World ↔ grid coordinate bridge for the fire simulation.
///
/// Assign either FireSimulationController (CPU) or FireSimulationControllerGPUCompute
/// (CPU+GPU) to the fireSimMono field in the inspector.  Both implement IFireSimulation
/// so border ignition and state queries work identically with either backend.
///
/// Uses Renderer.bounds (world-space AABB, rotation-invariant) so the mapping is
/// correct for Quads in any orientation or scale.
/// </summary>
public class FireZoneController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Assign FireSimulationController (CPU) or FireSimulationControllerGPUCompute (CPU+GPU).")]
    public MonoBehaviour fireSimMono;

    [Tooltip("The Quad/Plane that the fire heatmap texture is projected onto.")]
    public Transform gridPlane;

    [Header("Battle Royale Border")]
    [Tooltip("If true, the outer borderWidth ring of cells ignites on Start " +
             "(Battle-Royale closing-fire mechanic).  " +
             "Leave FALSE to let fire spread only from startFireCells.")]
    public bool igniteOnStart = false;
    [Range(1, 30)]
    public int borderWidth = 3;

    // ── Public interface reference ─────────────────────────────────────────────
    /// <summary>
    /// Resolved from fireSimMono at Awake.  Use this from external scripts
    /// (e.g. PlayerFireInteraction) instead of accessing fireSimMono directly.
    /// </summary>
    public IFireSimulation FireSim { get; private set; }

    private Renderer _gridRenderer;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    void Awake()
    {
        FireSim = fireSimMono as IFireSimulation;
        if (FireSim == null)
            Debug.LogError("[FireZone] fireSimMono must implement IFireSimulation. " +
                           "Assign FireSimulationController or FireSimulationControllerGPUCompute.");
        CacheRenderer();
    }

    void Start()
    {
        if (igniteOnStart) IgniteBorder();
    }

    void CacheRenderer()
    {
        if (gridPlane != null)
            _gridRenderer = gridPlane.GetComponent<Renderer>();
    }

    // ── Grid bounds ────────────────────────────────────────────────────────────

    Bounds GridBounds()
    {
        if (_gridRenderer == null) CacheRenderer();
        return _gridRenderer != null
            ? _gridRenderer.bounds
            : new Bounds(Vector3.zero, Vector3.one);
    }

    /// <summary>Smallest world-space dimension of a single grid cell.</summary>
    public float CellWorldSize
    {
        get
        {
            if (FireSim == null) return 1f;
            var b = GridBounds();
            return Mathf.Min(b.size.x / FireSim.Width, b.size.z / FireSim.Height);
        }
    }

    // ── Border ignition ────────────────────────────────────────────────────────

    void IgniteBorder()
    {
        if (FireSim == null) return;
        int w = FireSim.Width;
        int h = FireSim.Height;

        for (int x = 0; x < w; x++)
            for (int b = 0; b < borderWidth; b++)
            {
                FireSim.IgniteCell(x, b);
                FireSim.IgniteCell(x, h - 1 - b);
            }

        for (int y = borderWidth; y < h - borderWidth; y++)
            for (int b = 0; b < borderWidth; b++)
            {
                FireSim.IgniteCell(b,         y);
                FireSim.IgniteCell(w - 1 - b, y);
            }
    }

    // ── Coordinate conversion ──────────────────────────────────────────────────

    /// <summary>
    /// Maps a world position to grid (gx, gy).  Returns false if outside the quad.
    /// </summary>
    public bool TryWorldToGrid(Vector3 worldPos, out int gx, out int gy)
    {
        gx = gy = 0;
        if (FireSim == null) return false;

        Bounds b     = GridBounds();
        float  normX = (worldPos.x - b.min.x) / b.size.x;
        float  normZ = (worldPos.z - b.min.z) / b.size.z;

        if (normX < 0f || normX >= 1f || normZ < 0f || normZ >= 1f) return false;

        gx = Mathf.Clamp(Mathf.FloorToInt(normX        * FireSim.Width),  0, FireSim.Width  - 1);
        gy = Mathf.Clamp(Mathf.FloorToInt((1f - normZ) * FireSim.Height), 0, FireSim.Height - 1);
        return true;
    }

    /// <summary>
    /// Converts grid cell (gx, gy) to world-space centre of that cell, at ground height.
    /// </summary>
    public Vector3 GridToWorld(int gx, int gy)
    {
        if (FireSim == null) return Vector3.zero;

        Bounds b     = GridBounds();
        float  normX = (gx + 0.5f) / FireSim.Width;
        float  normZ = 1f - (gy + 0.5f) / FireSim.Height;  // Y-flip matches compute shader

        return new Vector3(
            b.min.x + normX * b.size.x,
            b.center.y,
            b.min.z + normZ * b.size.z);
    }

    // ── State queries ──────────────────────────────────────────────────────────

    public bool IsPositionBurning(Vector3 worldPos)
    {
        if (!TryWorldToGrid(worldPos, out int gx, out int gy)) return false;
        return FireSim.GetCellState(gy * FireSim.Width + gx) == 2;
    }

    public bool IsPositionOnFire(Vector3 worldPos)
    {
        if (!TryWorldToGrid(worldPos, out int gx, out int gy)) return false;
        int s = FireSim.GetCellState(gy * FireSim.Width + gx);
        return s == 2 || s == 3;
    }

    public float GetHeatNormalized(Vector3 worldPos)
    {
        if (!TryWorldToGrid(worldPos, out int gx, out int gy)) return 0f;
        float t = FireSim.GetCellTemperature(gy * FireSim.Width + gx);
        return Mathf.Clamp01(t / Mathf.Max(0.001f, FireSim.HeatMax));
    }

    // ── Debug ──────────────────────────────────────────────────────────────────

    [ContextMenu("Debug: Print Grid Bounds")]
    void DebugPrintBounds()
    {
        Bounds b = GridBounds();
        Debug.Log($"[FireZone] Bounds center={b.center}  size={b.size}  " +
                  $"cellWorldSize={CellWorldSize:F4}  " +
                  $"grid={FireSim?.Width}×{FireSim?.Height}");
    }
}
