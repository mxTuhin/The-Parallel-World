using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Singleton that owns the enemy object pool, spawning, the TransformAccessArray
/// fed to EnemyMoveJob, and the Burst-based nearest-target search.
///
/// POOLING
/// ───────
/// On Awake, <see cref="poolSize"/> enemies are pre-instantiated and deactivated.
/// Spawning dequeues one, resets its transform/health, and activates it.
/// Death returns it: deactivated and re-enqueued — zero runtime allocations.
///
/// maxEnemies is clamped to poolSize so the queue never runs dry during normal play.
/// SimulationBenchmark may call ForceSpawnEnemies(count) to pre-fill to the cap.
///
/// TRANSFORM ACCESS ARRAY
/// ──────────────────────
/// Only *active* enemies live in the TransformAccessArray.  RegisterEnemy adds on
/// acquire; RemoveEnemy uses swap-back on release — identical to the pre-pool code,
/// just without any Instantiate / Destroy calls at runtime.
/// </summary>
public class EnemyManager : MonoBehaviour
{
    public static EnemyManager Instance { get; private set; }

    [Header("References")]
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] private Transform  playerTransform;

    [Header("Spawning")]
    [SerializeField] private float spawnRadius   = 25f;
    [SerializeField] private float spawnInterval = 1.5f;
    [SerializeField] private int   maxEnemies    = 150;

    [Header("Enemy Movement")]
    [SerializeField] private float enemyMoveSpeed = 3f;

    [Header("Pool")]
    [Tooltip("Total enemies pre-instantiated at startup.  maxEnemies is clamped to this value.\n" +
             "All 2500 are created in Awake — expect a one-time load hitch of ~100–300 ms.")]
    [SerializeField] private int poolSize = 2500;

    // ── Active-enemy lists (parallel: _enemies[i] ↔ TAA entry i) ─────────────
    private readonly List<EnemyController> _enemies = new();
    private TransformAccessArray _transformAccess;
    private JobHandle            _moveJobHandle;
    private float                _spawnTimer;

    // ── Pool state ────────────────────────────────────────────────────────────
    private Queue<EnemyController> _available;   // inactive, ready to acquire
    private Transform              _poolRoot;    // parent for all pool objects

    // ── Profiler marker ───────────────────────────────────────────────────────
    private static readonly ProfilerMarker EnemyMoveMarker =
        new ProfilerMarker("EnemyAI.MoveJob");

    // ── Public accessors ──────────────────────────────────────────────────────

    public int EnemyCount  => _enemies.Count;
    public int PoolAvailable => _available?.Count ?? 0;

    /// <summary>
    /// Maximum concurrent active enemies.  Clamped to poolSize so the queue is
    /// never exhausted.  Writable at runtime by SimulationBenchmark.
    /// </summary>
    public int MaxEnemies
    {
        get => maxEnemies;
        set => maxEnemies = Mathf.Clamp(value, 0, poolSize);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _transformAccess = new TransformAccessArray(0);

        // Clamp maxEnemies to pool size before anyone reads it
        maxEnemies = Mathf.Clamp(maxEnemies, 0, poolSize);

        PrewarmPool();
    }

    private void OnDestroy()
    {
        _moveJobHandle.Complete();
        if (_transformAccess.isCreated) _transformAccess.Dispose();
    }

    private void Update()
    {
        _spawnTimer += Time.deltaTime;
        if (_spawnTimer >= spawnInterval && _enemies.Count < maxEnemies)
        {
            _spawnTimer = 0f;
            SpawnEnemy();
        }
    }

    private void FixedUpdate()
    {
        if (_enemies.Count == 0 || playerTransform == null) return;

        _moveJobHandle.Complete();

        using (EnemyMoveMarker.Auto())
        {
            _moveJobHandle = new EnemyMoveJob
            {
                PlayerPosition = (float3)playerTransform.position,
                MoveSpeed      = enemyMoveSpeed,
                DeltaTime      = Time.fixedDeltaTime
            }.Schedule(_transformAccess);
        }
    }

    private void LateUpdate()
    {
        _moveJobHandle.Complete();
    }

    // ── Pool initialisation ───────────────────────────────────────────────────

    /// <summary>
    /// Pre-instantiates all <see cref="poolSize"/> enemies synchronously.
    /// Runs once in Awake — a brief load hitch (~100–300 ms) is expected and
    /// is far cheaper than 2500 runtime Instantiate calls during gameplay.
    /// </summary>
    private void PrewarmPool()
    {
        _poolRoot  = new GameObject("EnemyPool").transform;
        _poolRoot.SetParent(transform);

        _available = new Queue<EnemyController>(poolSize);

        for (int i = 0; i < poolSize; i++)
        {
            // Instantiate under poolRoot; Awake runs immediately.
            var go   = Instantiate(enemyPrefab, _poolRoot);
            go.SetActive(false);  // OnEnable deferred until first acquire

            var ctrl = go.GetComponent<EnemyController>();
            if (ctrl == null)
            {
                Debug.LogError("[EnemyPool] enemyPrefab is missing EnemyController.");
                Destroy(go);
                continue;
            }

            _available.Enqueue(ctrl);
        }

        Debug.Log($"[EnemyPool] Pre-warmed {_available.Count}/{poolSize} enemies. " +
                  $"maxEnemies clamped to {maxEnemies}.");
    }

    // ── Spawning (pool acquire) ───────────────────────────────────────────────

    private void SpawnEnemy()
    {
        if (_available.Count == 0) return;   // pool exhausted — skip this tick

        // Random point on a circle around the player
        float   angle    = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        Vector3 offset   = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
        Vector3 spawnPos = playerTransform != null
            ? playerTransform.position + offset
            : offset;

        AcquireFromPool(spawnPos, Quaternion.identity);
    }

    /// <summary>
    /// Dequeues an enemy from the pool, resets its state, places it at
    /// <paramref name="position"/> / <paramref name="rotation"/>, activates it,
    /// and registers it with the move job.
    ///
    /// Returns the activated controller, or null if the pool is empty.
    /// </summary>
    public EnemyController AcquireFromPool(Vector3 position, Quaternion rotation)
    {
        if (_available.Count == 0)
        {
            Debug.LogWarning("[EnemyPool] Pool exhausted — no enemy available to spawn.");
            return null;
        }

        var enemy = _available.Dequeue();

        // ── Reset transform BEFORE SetActive so OnEnable sees the right position ──
        // SetParent with worldPositionStays=false keeps the local coords clean.
        enemy.transform.SetPositionAndRotation(position, rotation);

        // ── Activate: triggers EnemyHealth.OnEnable → _current = maxHealth ────────
        enemy.gameObject.SetActive(true);

        // ── Register with the Burst move job ──────────────────────────────────────
        RegisterEnemy(enemy);

        return enemy;
    }

    /// <summary>
    /// Instantly activates up to <paramref name="count"/> enemies at random spawn
    /// positions.  Called by SimulationBenchmark at auto-run start.
    /// </summary>
    public void ForceSpawnEnemies(int count)
    {
        int toSpawn = Mathf.Min(count, maxEnemies) - _enemies.Count;
        for (int i = 0; i < toSpawn; i++)
            SpawnEnemy();
    }

    // ── Pool release (death / despawn) ────────────────────────────────────────

    /// <summary>
    /// Returns an enemy to the pool.  Called by <see cref="EnemyController.OnDie"/>.
    ///
    /// Sequence:
    ///   1. Guard: already returned (ManagerIndex == -1) → skip.
    ///   2. Complete any in-flight Burst move job.
    ///   3. Swap-back remove from _enemies and TransformAccessArray.
    ///   4. Reset position to origin and re-parent under poolRoot.
    ///   5. SetActive(false) → OnDisable fires.
    ///   6. Enqueue back into _available.
    /// </summary>
    public void ReturnToPool(EnemyController enemy)
    {
        // Guard: already returned or never registered
        if (enemy.ManagerIndex < 0) return;

        // Complete any in-flight job before touching the list / TAA
        _moveJobHandle.Complete();

        // Swap-back remove from active list + TransformAccessArray
        RemoveEnemyInternal(enemy);

        // ── Reset transform so pooled enemies don't clutter world space ──────────
        enemy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        // ── Deactivate ────────────────────────────────────────────────────────────
        enemy.gameObject.SetActive(false);

        // ── Re-enqueue ────────────────────────────────────────────────────────────
        _available.Enqueue(enemy);
    }

    // ── Registry ──────────────────────────────────────────────────────────────

    /// <summary>Adds an enemy to the active list and TransformAccessArray.</summary>
    public void RegisterEnemy(EnemyController enemy)
    {
        _moveJobHandle.Complete();
        enemy.ManagerIndex = _enemies.Count;
        _enemies.Add(enemy);
        _transformAccess.Add(enemy.transform);
    }

    /// <summary>
    /// Swap-back remove from the active list and TransformAccessArray.
    /// Kept public for legacy callers — prefer <see cref="ReturnToPool"/> on death.
    /// </summary>
    public void RemoveEnemy(EnemyController enemy)
    {
        _moveJobHandle.Complete();
        RemoveEnemyInternal(enemy);
    }

    // Internal swap-back — callers are responsible for completing the job first.
    private void RemoveEnemyInternal(EnemyController enemy)
    {
        int idx = enemy.ManagerIndex;
        if (idx < 0 || idx >= _enemies.Count) return;

        int last = _enemies.Count - 1;
        if (idx != last)
        {
            _enemies[last].ManagerIndex = idx;
            _enemies[idx]               = _enemies[last];
            // TransformAccessArray mirrors this swap:
        }

        _enemies.RemoveAt(last);
        _transformAccess.RemoveAtSwapBack(idx);
        enemy.ManagerIndex = -1;
    }

    // ── Nearest target search (Burst) ─────────────────────────────────────────

    /// <summary>
    /// Returns the nearest active EnemyController within <paramref name="range"/>.
    /// Runs synchronously — schedule + immediate Complete.
    /// </summary>
    public EnemyController FindNearestEnemy(Vector3 playerPos, float range)
    {
        _moveJobHandle.Complete();
        if (_enemies.Count == 0) return null;

        var positions = new NativeArray<float3>(_enemies.Count, Allocator.TempJob);
        var result    = new NativeArray<int>(1,                 Allocator.TempJob);

        for (int i = 0; i < _enemies.Count; i++)
            positions[i] = (float3)_enemies[i].transform.position;

        new FindNearestTargetJob
        {
            EnemyPositions = positions,
            PlayerPosition = (float3)playerPos,
            MaxRange       = range,
            ResultIndex    = result
        }.Schedule().Complete();

        int idx = result[0];
        positions.Dispose();
        result.Dispose();

        return (idx >= 0 && idx < _enemies.Count) ? _enemies[idx] : null;
    }

    public EnemyController GetEnemyAt(int index) =>
        (index >= 0 && index < _enemies.Count) ? _enemies[index] : null;
}
