using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Singleton wrapper around Unity's ObjectPool for bullets.
/// Place on an empty GameObject in the scene and assign the BulletPrefab.
/// BulletPrefab must have: BulletController, Rigidbody, SphereCollider (trigger).
/// </summary>
public class BulletPool : MonoBehaviour
{
    public static BulletPool Instance { get; private set; }

    [SerializeField] private BulletController bulletPrefab;
    [SerializeField] private int defaultCapacity = 30;
    [SerializeField] private int maxPoolSize = 200;

    private ObjectPool<BulletController> _pool;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _pool = new ObjectPool<BulletController>(
            createFunc:       CreateBullet,
            actionOnGet:      _ => { },                          // Fire() activates after setting all values
            actionOnRelease:  b => b.gameObject.SetActive(false), // OnDisable zeros velocity + resets state
            actionOnDestroy:  b => Destroy(b.gameObject),
            collectionCheck:  false,
            defaultCapacity:  defaultCapacity,
            maxSize:          maxPoolSize
        );
    }

    private BulletController CreateBullet()
    {
        var bullet = Instantiate(bulletPrefab, transform);
        bullet.Pool = _pool;
        bullet.gameObject.SetActive(false);
        return bullet;
    }

    public BulletController Get() => _pool.Get();
}
