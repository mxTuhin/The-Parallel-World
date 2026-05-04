using UnityEngine;

/// <summary>
/// Auto-targeting shooter that sits alongside PlayerMovementController.
///
/// Aim flow:
///  1. Find nearest enemy in shootRange (Burst job, rate-limited).
///  2. Rotate smoothly toward it at aimRotationSpeed.
///  3. Once within aimAngleDegrees AND held there for aimSettleTime → fire.
///  4. If the player rotates away (enemy moves), lock resets and re-aims before firing again.
///  5. On target change the lock always resets — must re-aim at the new target first.
/// </summary>
[RequireComponent(typeof(PlayerMovementController))]
public class PlayerShooterController : MonoBehaviour
{
    [Header("Targeting")]
    [SerializeField] private float shootRange      = 15f;
    [SerializeField] private float targetLossRange = 20f;
    [SerializeField] private float searchInterval  = 0.1f;

    [Header("Aiming")]
    [SerializeField] private float aimRotationSpeed = 720f;   // °/s  — 180° turn in ~0.25 s
    [SerializeField] private float aimAngleDegrees  = 4f;     // must be within this angle to be considered "locked"
    [SerializeField] private float aimSettleTime    = 0.08f;  // seconds player must hold the lock before first shot

    [Header("Firing")]
    [SerializeField] private float fireRate  = 0.25f;
    [SerializeField] private Transform firePoint;

    private PlayerMovementController _movement;
    private EnemyController _currentTarget;
    private float _fireTimer;
    private float _searchTimer;

    // Aim-lock state — only true once angle AND settle time are satisfied
    private float _aimSettleTimer;
    private bool  _aimLocked;

    private void Awake()
    {
        _movement = GetComponent<PlayerMovementController>();
    }

    private void Update()
    {
        UpdateTarget();

        if (_currentTarget != null)
        {
            _movement.IsShootingMode = true;
            AimAndShoot();
        }
        else
        {
            _movement.IsShootingMode = false;
            ResetAimLock();
        }

        _fireTimer = Mathf.Max(0f, _fireTimer - Time.deltaTime);
    }

    // ─── Target management ────────────────────────────────────────────────────

    private void UpdateTarget()
    {
        if (_currentTarget != null)
        {
            bool lost = _currentTarget.Health.IsDead
                     || !_currentTarget.gameObject.activeInHierarchy
                     || Vector3.Distance(transform.position, _currentTarget.transform.position) > targetLossRange;

            if (lost) DropTarget();
        }

        if (_currentTarget == null && EnemyManager.Instance != null)
        {
            _searchTimer -= Time.deltaTime;
            if (_searchTimer <= 0f)
            {
                _searchTimer = searchInterval;
                var found = EnemyManager.Instance.FindNearestEnemy(transform.position, shootRange);
                if (found != null)
                {
                    _currentTarget = found;
                    ResetAimLock(); // must re-aim at fresh target before firing
                }
            }
        }
    }

    private void DropTarget()
    {
        _currentTarget = null;
        ResetAimLock();
    }

    private void ResetAimLock()
    {
        _aimLocked      = false;
        _aimSettleTimer = 0f;
    }

    // ─── Aim & fire ───────────────────────────────────────────────────────────

    private void AimAndShoot()
    {
        Vector3 toTarget = _currentTarget.transform.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.01f) return;

        Vector3 dir = toTarget.normalized;

        // Smooth rotation — always runs so the player visibly tracks the enemy
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            Quaternion.LookRotation(dir),
            aimRotationSpeed * Time.deltaTime
        );

        // Angle-based lock check (more intuitive than dot product threshold)
        float angle = Vector3.Angle(transform.forward, dir);

        if (angle <= aimAngleDegrees)
        {
            // Accumulate settle time — the player must hold the aim for a beat
            _aimSettleTimer += Time.deltaTime;
            if (_aimSettleTimer >= aimSettleTime)
                _aimLocked = true;
        }
        else
        {
            // Drifted off target (enemy moved, or just started rotating) — reset lock
            ResetAimLock();
        }

        // Fire only when truly locked and cooldown is clear
        if (_aimLocked && _fireTimer <= 0f)
        {
            Shoot();
            _fireTimer = fireRate;
        }
    }

    private void Shoot()
    {
        if (BulletPool.Instance == null) return;

        Vector3 origin = firePoint != null
            ? firePoint.position
            : transform.position + transform.forward * 0.6f + Vector3.up * 0.5f;

        BulletPool.Instance.Get().Fire(origin, transform.forward);
    }
}
