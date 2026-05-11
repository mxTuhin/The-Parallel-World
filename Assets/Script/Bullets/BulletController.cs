using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Lifecycle contract with BulletPool:
///
///   GET  → Pool.Get() returns a DISABLED bullet (pool does not activate it).
///          Caller must call Fire() — which positions, velocities, then activates.
///
///   FIRE → transform.position / rotation / linearVelocity set FIRST,
///          then SetActive(true) so physics never sees a stale position.
///
///   RETURN → _fired = false → Pool.Release() → SetActive(false) → OnDisable
///            zeros all velocity so the bullet is fully inert before sleeping.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class BulletController : MonoBehaviour
{
    [SerializeField] private float speed    = 20f;
    [SerializeField] private float damage   = 25f;
    [SerializeField] private float lifetime = 5f;

    public IObjectPool<BulletController> Pool { private get; set; }

    private Rigidbody _rb;
    private float     _timer;
    private bool      _fired;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.useGravity             = false;
        _rb.linearDamping          = 0f;
        _rb.angularDamping         = 0f;
        _rb.interpolation          = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _rb.constraints            = RigidbodyConstraints.FreezePositionY
                                   | RigidbodyConstraints.FreezeRotation;

        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    // Called by the pool's actionOnGet — intentionally minimal.
    // Position and velocity are NOT valid yet; Fire() sets them before activating.
    private void OnEnable()
    {
        _timer = 0f;
        // _fired is set by Fire() AFTER SetActive so it never gets overwritten here
    }

    // Full reset: everything inert before the object goes back to sleep in the pool
    private void OnDisable()
    {
        _fired                    = false;
        _rb.linearVelocity        = Vector3.zero;
        _rb.angularVelocity       = Vector3.zero;
        _rb.MovePosition(transform.position); // flush any pending physics move
    }

    /// <summary>
    /// Set position/velocity first, THEN activate — physics never sees a stale state.
    /// </summary>
    public void Fire(Vector3 origin, Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
        {
            Pool?.Release(this); // bad direction — return immediately without activating
            return;
        }
        direction.Normalize();

        // 1. Place and orient while still DISABLED so no phantom collisions
        transform.position = origin;
        transform.rotation = Quaternion.LookRotation(direction);

        // 2. Prime the Rigidbody velocity while still disabled
        _rb.linearVelocity  = direction * speed;
        _rb.angularVelocity = Vector3.zero;

        // 3. Activate — OnEnable resets _timer, then we immediately set _fired = true
        gameObject.SetActive(true);
        _fired = true;              // set AFTER SetActive so OnEnable cannot overwrite it
    }

    private void FixedUpdate()
    {
        if (!_fired) return;

        _timer += Time.fixedDeltaTime;
        if (_timer >= lifetime)
            ReturnToPool();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!_fired) return; // guard against phantom triggers during pool transitions

        var health = other.GetComponent<EnemyHealth>()
                  ?? other.GetComponentInParent<EnemyHealth>();

        if (health != null && !health.IsDead)
        {
            health.TakeDamage(damage);
            ReturnToPool();
        }
    }

    private void ReturnToPool()
    {
        if (!_fired) return; // prevent double-release if hit + lifetime expire same frame
        _fired = false;
        Pool?.Release(this);
    }
}
