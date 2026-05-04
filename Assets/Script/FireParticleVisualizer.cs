using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maintains a pool of fire particle systems repositioned every resampleInterval
/// seconds onto randomly-selected burning cells.
///
/// Particle scale is derived from the actual grid cell world size so fire
/// looks proportional regardless of how large the quad is in the scene.
/// </summary>
public class FireParticleVisualizer : MonoBehaviour
{
    [Header("References")]
    public FireSimulationControllerGPUCompute fireSim;
    public FireZoneController fireZone;

    [Header("VFX Pool")]
    [Tooltip("Your own ParticleSystem prefab. Leave null to auto-generate a placeholder.")]
    public ParticleSystem fireVFXPrefab;

    [Range(5, 200)]
    public int poolSize = 40;

    [Tooltip("World-space Y offset above the grid surface.")]
    public float heightOffset = 0.1f;

    [Header("Particle Size")]
    [Tooltip("How many grid cells wide each particle system covers. " +
             "Increase for bigger, more dramatic fire columns. " +
             "Computed from the actual quad size so it scales with your scene.")]
    [Range(0.5f, 20f)]
    public float cellsPerVFX = 5f;

    [Header("Sampling")]
    [Tooltip("Seconds between repositioning the VFX pool onto new burning cells.")]
    public float resampleInterval = 0.5f;

    private ParticleSystem[] _pool;
    private float _timer;
    private float _vfxWorldScale;

    private readonly List<int> _burningIndices = new(4096);

    void Start()
    {
        // Compute the world-space size of one grid cell, then scale by cellsPerVFX
        _vfxWorldScale = fireZone != null
            ? fireZone.CellWorldSize * cellsPerVFX
            : cellsPerVFX;

        if (_vfxWorldScale < 0.001f) _vfxWorldScale = 0.1f;

        Debug.Log($"[FireVFX] Cell world size = {(fireZone != null ? fireZone.CellWorldSize : 0f):F4}  " +
                  $"VFX scale = {_vfxWorldScale:F4}");

        BuildPool();
        Resample();
    }

    void Update()
    {
        _timer += Time.deltaTime;
        if (_timer >= resampleInterval)
        {
            _timer = 0f;
            Resample();
        }
    }

    // ─── Pool construction ────────────────────────────────────────────────────

    void BuildPool()
    {
        _pool = new ParticleSystem[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            GameObject go;
            if (fireVFXPrefab != null)
            {
                go = Instantiate(fireVFXPrefab.gameObject, transform);
            }
            else
            {
                go = new GameObject($"FireVFX_{i}");
                go.transform.SetParent(transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                ConfigurePlaceholder(ps);
                _pool[i] = ps;
            }

            // Scale relative to one grid cell so fire fits the visual grid
            go.transform.localScale = Vector3.one * _vfxWorldScale;
            go.SetActive(false);

            if (_pool[i] == null)
                _pool[i] = go.GetComponent<ParticleSystem>();
        }
    }

    // Auto-generated placeholder — configure in local space (scale handles world size).
    void ConfigurePlaceholder(ParticleSystem ps)
    {
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles    = 50;
        main.startLifetime   = new ParticleSystem.MinMaxCurve(0.8f, 1.5f);
        main.startSpeed      = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);  // local units → scaled by vfxWorldScale
        main.startSize       = new ParticleSystem.MinMaxCurve(0.3f, 0.7f); // local units
        main.startColor      = new ParticleSystem.MinMaxGradient(
            new Color(1f, 0.25f, 0f, 0.9f),
            new Color(1f, 0.80f, 0f, 0.7f));
        main.gravityModifier = 0f;

        var emission = ps.emission;
        emission.rateOverTime = 20f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius    = 0.4f;          // local — fills roughly one cell
        shape.radiusThickness = 1f;      // emit from disk interior

        // Rise upward (world Y)
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space   = ParticleSystemSimulationSpace.World;
        vel.y       = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
        vel.x       = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
        vel.z       = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);

        // Fade: ignite orange → dark smoke
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f,   0.55f, 0f),   0f),
                new GradientColorKey(new Color(0.9f, 0.15f, 0f),   0.35f),
                new GradientColorKey(new Color(0.12f,0.12f, 0.12f),1f)
            },
            new[]
            {
                new GradientAlphaKey(0f,  0f),
                new GradientAlphaKey(1f,  0.1f),
                new GradientAlphaKey(0.7f,0.45f),
                new GradientAlphaKey(0f,  1f)
            });
        col.color = new ParticleSystem.MinMaxGradient(gradient);

        // Grow then shrink over lifetime
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        var curve = new AnimationCurve(
            new Keyframe(0f,   0.1f),
            new Keyframe(0.3f, 1f),
            new Keyframe(1f,   0f));
        size.size = new ParticleSystem.MinMaxCurve(1f, curve);

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled  = true;
        limit.limit    = new ParticleSystem.MinMaxCurve(2f);
        limit.dampen   = 0.15f;
    }

    // ─── Resampling ───────────────────────────────────────────────────────────

    void Resample()
    {
        if (fireSim == null || fireZone == null) return;

        _burningIndices.Clear();
        int total = fireSim.width * fireSim.height;
        for (int i = 0; i < total; i++)
        {
            if (fireSim.GetCellState(i) == 2)
                _burningIndices.Add(i);
        }

        int count = Mathf.Min(_pool.Length, _burningIndices.Count);

        // Partial Fisher-Yates to pick `count` random burning cells
        for (int i = 0; i < count; i++)
        {
            int j = Random.Range(i, _burningIndices.Count);
            (_burningIndices[i], _burningIndices[j]) = (_burningIndices[j], _burningIndices[i]);
        }

        for (int i = 0; i < _pool.Length; i++)
        {
            if (i < count)
            {
                int cellIdx = _burningIndices[i];
                int gx = cellIdx % fireSim.width;
                int gy = cellIdx / fireSim.width;

                Vector3 worldPos  = fireZone.GridToWorld(gx, gy);
                worldPos.y       += heightOffset;

                _pool[i].transform.position = worldPos;

                if (!_pool[i].gameObject.activeSelf)
                    _pool[i].gameObject.SetActive(true);
            }
            else
            {
                if (_pool[i].gameObject.activeSelf)
                    _pool[i].gameObject.SetActive(false);
            }
        }
    }
}
