using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Singleton that owns enemy spawning, the TransformAccessArray fed to EnemyMoveJob,
/// and the Burst-based nearest-target search used by the player shooter.
///
/// Setup: Create an empty GameObject in the scene, attach this component,
/// assign EnemyPrefab (needs EnemyController + EnemyHealth + Rigidbody(kinematic) + Collider)
/// and the PlayerTransform.
/// </summary>
public class EnemyManager : MonoBehaviour
{
    public static EnemyManager Instance { get; private set; }

    [Header("References")]
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] private Transform playerTransform;

    [Header("Spawning")]
    [SerializeField] private float spawnRadius = 25f;
    [SerializeField] private float spawnInterval = 1.5f;
    [SerializeField] private int maxEnemies = 150;

    [Header("Enemy Movement")]
    [SerializeField] private float enemyMoveSpeed = 3f;

    // Parallel lists: _enemies[i] ↔ transform in _transformAccess at same index i
    private readonly List<EnemyController> _enemies = new();
    private TransformAccessArray _transformAccess;
    private JobHandle _moveJobHandle;
    private float _spawnTimer;

    public int EnemyCount => _enemies.Count;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _transformAccess = new TransformAccessArray(0);
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

        // Complete the previous frame's job before scheduling a new one
        _moveJobHandle.Complete();

        _moveJobHandle = new EnemyMoveJob
        {
            PlayerPosition = (float3)playerTransform.position,
            MoveSpeed = enemyMoveSpeed,
            DeltaTime = Time.fixedDeltaTime
        }.Schedule(_transformAccess);
    }

    private void LateUpdate()
    {
        // Ensure all enemy transforms are settled before the frame renders
        _moveJobHandle.Complete();
    }

    // ─── Spawning ────────────────────────────────────────────────────────────

    private void SpawnEnemy()
    {
        float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
        Vector3 spawnPos = playerTransform.position + offset;

        GameObject go = Instantiate(enemyPrefab, spawnPos, Quaternion.identity);
        var enemy = go.GetComponent<EnemyController>();
        if (enemy != null) RegisterEnemy(enemy);
    }

    // ─── Registry (keep _enemies list and TransformAccessArray in sync) ───────

    public void RegisterEnemy(EnemyController enemy)
    {
        _moveJobHandle.Complete();
        enemy.ManagerIndex = _enemies.Count;
        _enemies.Add(enemy);
        _transformAccess.Add(enemy.transform);
    }

    public void RemoveEnemy(EnemyController enemy)
    {
        _moveJobHandle.Complete();

        int idx = enemy.ManagerIndex;
        if (idx < 0 || idx >= _enemies.Count) return;

        // Swap-back O(1) removal — keep both structures in sync
        int last = _enemies.Count - 1;
        if (idx != last)
        {
            _enemies[last].ManagerIndex = idx;
            _enemies[idx] = _enemies[last];
        }

        _enemies.RemoveAt(last);
        _transformAccess.RemoveAtSwapBack(idx);
        enemy.ManagerIndex = -1;
    }

    // ─── Target Search (Burst) ────────────────────────────────────────────────

    /// <summary>
    /// Returns the nearest EnemyController within <paramref name="range"/> using a Burst job.
    /// Runs synchronously — schedule + immediate Complete — so the result is available inline.
    /// Call this sparingly (e.g. when target is lost, not every frame).
    /// </summary>
    public EnemyController FindNearestEnemy(Vector3 playerPos, float range)
    {
        _moveJobHandle.Complete();
        if (_enemies.Count == 0) return null;

        var positions = new NativeArray<float3>(_enemies.Count, Allocator.TempJob);
        var result    = new NativeArray<int>(1, Allocator.TempJob);

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
