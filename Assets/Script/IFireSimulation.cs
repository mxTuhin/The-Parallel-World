using UnityEngine;

/// <summary>
/// Common contract for both fire simulation backends.
///
///   FireSimulationController            → CPU-only mode
///     Burst IJobParallelFor runs fire spread every frame.
///     CPU pixel loop writes the heatmap texture.
///     GetBurningCellsBuffer() returns null — particle scan stays on CPU.
///
///   FireSimulationControllerGPUCompute  → CPU+GPU mode
///     Burst IJobParallelFor still runs fire spread (CPU side).
///     ComputeShader (CSMain) renders the heatmap on GPU.
///     ComputeShader (BurningCellScanKernel) compacts burning-cell indices
///       into an AppendStructuredBuffer — used by FireParticleVisualizer
///       to skip the 360 k-cell CPU scan.
///
/// FireZoneController, FireParticleVisualizer, SimulationBenchmark all
/// hold an IFireSimulation reference so they work with either backend.
/// </summary>
public interface IFireSimulation
{
    // ── Grid dimensions ───────────────────────────────────────────────────────

    int   Width  { get; }
    int   Height { get; }

    /// <summary>
    /// Temperature value that maps to the fully-saturated end of the heatmap
    /// colour gradient.  Set close to ignitionTemperature so the warm-up is
    /// visible before cells snap to Burning.
    /// </summary>
    float HeatMax { get; }

    /// <summary>Grid cells (x,y) that start in state=Burning at simulation start.</summary>
    Vector2Int[] StartFireCells { get; }

    // ── Per-cell queries (safe to call from the main thread between frames) ──

    /// <summary>Returns 1=Empty, 2=Burning, 3=BurntOut, 0=invalid index.</summary>
    int   GetCellState(int index);
    float GetCellTemperature(int index);

    /// <summary>Safely ignites the cell at (x,y).  Clamped to grid bounds.</summary>
    void  IgniteCell(int x, int y);

    // ── Core-count control ───────────────────────────────────────────────────

    /// <summary>
    /// Changes the Unity Job System worker thread count at runtime.
    /// Effective immediately; value is clamped to [1, SystemInfo.processorCount].
    /// </summary>
    void SetCoreCount(int count);

    int CoreCount { get; }

    // ── GPU mode identification ───────────────────────────────────────────────

    /// <summary>True for the GPU controller; false for the CPU-only controller.</summary>
    bool IsGPUMode { get; }

    // ── GPU burning-cell compact list (GPU mode only) ─────────────────────────

    /// <summary>
    /// AppendStructuredBuffer&lt;uint&gt; populated each frame by BurningCellScanKernel.
    /// Contains the flat cell indices of every cell currently in state==2.
    /// Reset to counter 0 before each dispatch.
    /// Returns null in CPU mode — callers must fall back to a CPU scan.
    /// </summary>
    ComputeBuffer GetBurningCellsBuffer();

    /// <summary>
    /// ComputeBuffer(1, sizeof(int), Raw) written by ComputeBuffer.CopyCount each
    /// frame.  Read with GetData to know how many entries are in the append buffer.
    /// Returns null in CPU mode.
    /// </summary>
    ComputeBuffer GetBurningCellsCountBuffer();
}
