using UnityEngine;

/// <summary>
/// Lightweight component on every enemy prefab.
/// Actual movement is driven by EnemyMoveJob (Burst) via EnemyManager.
/// Requires a Collider (non-trigger) and a kinematic Rigidbody for bullet collision detection.
/// </summary>
[RequireComponent(typeof(EnemyHealth))]
public class EnemyController : MonoBehaviour
{
    // Index into EnemyManager's internal list — kept in sync by EnemyManager
    [HideInInspector] public int ManagerIndex = -1;

    public EnemyHealth Health { get; private set; }

    private void Awake()
    {
        Health = GetComponent<EnemyHealth>();

        // Ensure the Rigidbody is kinematic so the Burst job owns movement
        // but physics triggers still fire against the bullet
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }

    public void OnDie()
    {
        EnemyManager.Instance?.RemoveEnemy(this);
        gameObject.SetActive(false);
    }
}
