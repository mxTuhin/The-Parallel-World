using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One particle pool per startFireCell. Each pool follows ALL currently-burning
/// cells closest to its assigned source (Voronoi partition), so particles
/// track the spreading fire front rather than disappearing when initial cells
/// burn out.
///
/// Pool size ramps from poolSizeMin → poolSizeMax over poolGrowthDuration seconds.
/// </summary>
public class FireParticleVisualizer : MonoBehaviour
{
    [Header("References")]
    public FireSimulationControllerGPUCompute fireSim;
    public FireZoneController fireZone;

    [Header("VFX")]
    [Tooltip("Your own ParticleSystem prefab. Leave null for the auto-generated placeholder.")]
    public ParticleSystem fireVFXPrefab;

    [Tooltip("Material applied to placeholder particle systems. Assign ParticleMaterial here.")]
    public Material particleMaterial;

    [Tooltip("World-space Y offset above the grid surface.")]
    public float heightOffset = 0.1f;

    [Tooltip("How many grid cells wide each particle system covers (scales with quad size).")]
    [Range(0.5f, 30f)]
    public float cellsPerVFX = 5f;

    [Header("Pool Size — grows over time")]
    [Tooltip("Active particles per source at the start of the simulation.")]
    public int poolSizeMin = 20;

    [Tooltip("Active particles per source at peak. All are pre-allocated at startup — no runtime allocs.")]
    public int poolSizeMax = 50;

    [Tooltip("Seconds to ramp from poolSizeMin to poolSizeMax.")]
    public float poolGrowthDuration = 60f;

    [Header("Sampling")]
    [Tooltip("Seconds between repositioning particles onto new burning cells.")]
    public float resampleInterval = 0.5f;

    // ─── Internals ────────────────────────────────────────────────────────────

    private class SourcePool
    {
        public int assignedGX, assignedGY;
        public ParticleSystem[] particles;  // poolSizeMax pre-allocated
    }

    private SourcePool[] _pools;
    private List<int>[]  _partitions;   // pre-allocated per-pool candidate lists
    private float        _elapsed;
    private float        _resampleTimer;
    private float        _vfxWorldScale;

    void Start()
    {
        _vfxWorldScale = (fireZone != null ? fireZone.CellWorldSize : 1f) * cellsPerVFX;
        if (_vfxWorldScale < 0.001f) _vfxWorldScale = 0.1f;

        BuildPools();
        Resample();
    }

    void Update()
    {
        _elapsed       += Time.deltaTime;
        _resampleTimer += Time.deltaTime;

        if (_resampleTimer >= resampleInterval)
        {
            _resampleTimer = 0f;
            Resample();
        }
    }

    // ─── Pool construction ────────────────────────────────────────────────────

    void BuildPools()
    {
        var startCells = fireSim?.startFireCells;
        if (startCells == null || startCells.Length == 0)
        {
            Debug.LogWarning("[FireVFX] No startFireCells on FireSimulationControllerGPUCompute. " +
                             "Assign at least one — each gets its own particle pool.");
            _pools      = System.Array.Empty<SourcePool>();
            _partitions = System.Array.Empty<List<int>>();
            return;
        }

        _pools      = new SourcePool[startCells.Length];
        _partitions = new List<int>[startCells.Length];

        for (int p = 0; p < startCells.Length; p++)
        {
            _partitions[p] = new List<int>(2048);

            var pool = new SourcePool
            {
                assignedGX = Mathf.Clamp(startCells[p].x, 0, fireSim.width  - 1),
                assignedGY = Mathf.Clamp(startCells[p].y, 0, fireSim.height - 1),
                particles  = new ParticleSystem[poolSizeMax]
            };

            for (int i = 0; i < poolSizeMax; i++)
            {
                GameObject go;
                if (fireVFXPrefab != null)
                {
                    go = Instantiate(fireVFXPrefab.gameObject, transform);
                }
                else
                {
                    go = new GameObject($"FireVFX_src{p}_{i}");
                    go.transform.SetParent(transform, false);
                    var ps = go.AddComponent<ParticleSystem>();
                    ConfigurePlaceholder(ps);
                    pool.particles[i] = ps;
                }

                go.transform.localScale = Vector3.one * _vfxWorldScale;
                go.SetActive(false);

                if (pool.particles[i] == null)
                    pool.particles[i] = go.GetComponent<ParticleSystem>();
            }

            _pools[p] = pool;
        }

        Debug.Log($"[FireVFX] {_pools.Length} source pool(s) built — " +
                  $"{poolSizeMax} particles each (ramp {poolSizeMin}→{poolSizeMax} " +
                  $"over {poolGrowthDuration}s). VFX scale = {_vfxWorldScale:F3}");
    }

    // ─── Resampling ───────────────────────────────────────────────────────────

    void Resample()
    {
        if (fireSim == null || fireZone == null || _pools == null || _pools.Length == 0) return;

        int activeCount = Mathf.RoundToInt(
            Mathf.Lerp(poolSizeMin, poolSizeMax,
                       Mathf.Clamp01(_elapsed / Mathf.Max(0.001f, poolGrowthDuration))));

        int w    = fireSim.width;
        int h    = fireSim.height;
        int numP = _pools.Length;

        // Clear partition lists
        for (int p = 0; p < numP; p++) _partitions[p].Clear();

        // Single pass over the whole grid: assign each burning cell to the nearest source
        for (int idx = 0; idx < w * h; idx++)
        {
            if (fireSim.GetCellState(idx) != 2) continue;

            int gx = idx % w;
            int gy = idx / w;

            if (numP == 1)
            {
                _partitions[0].Add(idx);
            }
            else
            {
                int   nearest = 0;
                float minD2   = float.MaxValue;
                for (int p = 0; p < numP; p++)
                {
                    float dx = gx - _pools[p].assignedGX;
                    float dy = gy - _pools[p].assignedGY;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < minD2) { minD2 = d2; nearest = p; }
                }
                _partitions[nearest].Add(idx);
            }
        }

        // Position each pool from its partition
        for (int p = 0; p < numP; p++)
            PositionPool(_pools[p], _partitions[p], activeCount, w);
    }

    void PositionPool(SourcePool pool, List<int> candidates, int activeCount, int w)
    {
        int count = Mathf.Min(activeCount, candidates.Count);

        // Partial Fisher-Yates — pick `count` random candidates in-place
        for (int i = 0; i < count; i++)
        {
            int j = Random.Range(i, candidates.Count);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        for (int i = 0; i < pool.particles.Length; i++)
        {
            if (i < count)
            {
                int     idx = candidates[i];
                Vector3 pos = fireZone.GridToWorld(idx % w, idx / w);
                pos.y += heightOffset;

                pool.particles[i].transform.position = pos;

                if (!pool.particles[i].gameObject.activeSelf)
                    pool.particles[i].gameObject.SetActive(true);
            }
            else if (pool.particles[i].gameObject.activeSelf)
            {
                pool.particles[i].gameObject.SetActive(false);
            }
        }
    }

    // ─── Placeholder particle config ──────────────────────────────────────────

    void ConfigurePlaceholder(ParticleSystem ps)
    {
        if (particleMaterial != null)
            ps.GetComponent<ParticleSystemRenderer>().material = particleMaterial;

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles    = 50;
        main.startLifetime   = new ParticleSystem.MinMaxCurve(0.8f, 1.5f);
        main.startSpeed      = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
        main.startSize       = new ParticleSystem.MinMaxCurve(0.3f, 0.7f);
        main.startColor      = new ParticleSystem.MinMaxGradient(
            new Color(1f, 0.25f, 0f, 0.9f),
            new Color(1f, 0.80f, 0f, 0.7f));
        main.gravityModifier = 0f;

        var emission = ps.emission;
        emission.rateOverTime = 20f;

        var shape = ps.shape;
        shape.shapeType       = ParticleSystemShapeType.Circle;
        shape.radius          = 0.4f;
        shape.radiusThickness = 1f;

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space   = ParticleSystemSimulationSpace.World;
        vel.y       = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
        vel.x       = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
        vel.z       = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f,    0.55f, 0f),    0f),
                new GradientColorKey(new Color(0.9f,  0.15f, 0f),    0.35f),
                new GradientColorKey(new Color(0.12f, 0.12f, 0.12f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f,   0f),
                new GradientAlphaKey(1f,   0.1f),
                new GradientAlphaKey(0.7f, 0.45f),
                new GradientAlphaKey(0f,   1f)
            });
        col.color = new ParticleSystem.MinMaxGradient(gradient);

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size    = new ParticleSystem.MinMaxCurve(1f,
            new AnimationCurve(
                new Keyframe(0f,   0.1f),
                new Keyframe(0.3f, 1f),
                new Keyframe(1f,   0f)));

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit   = new ParticleSystem.MinMaxCurve(2f);
        limit.dampen  = 0.15f;
    }
}
