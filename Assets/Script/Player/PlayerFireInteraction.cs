using UnityEngine;

/// <summary>
/// Applies damage to the player when standing in an actively burning or smouldering cell.
/// Attach to the Player GameObject alongside PlayerHealth.
///
/// Queries fire state through FireZoneController.FireSim (IFireSimulation) so it works
/// identically with either the CPU or CPU+GPU simulation backend.
/// </summary>
public class PlayerFireInteraction : MonoBehaviour
{
    [Header("References")]
    public FireZoneController fireZone;
    public PlayerHealth playerHealth;

    [Header("Damage")]
    [Tooltip("Health per second lost while standing inside a Burning cell (state == 2).")]
    public float burningDamagePerSecond = 15f;

    [Tooltip("Extra damage per second when standing in a BurntOut cell still radiating heat (state == 3).")]
    public float embersAdditionalDamagePerSecond = 3f;

    [Header("Debug")]
    [Tooltip("Logs fire state every frame.  Disable in shipping builds.")]
    public bool debugLog = false;

    void Update()
    {
        if (fireZone == null || fireZone.FireSim == null) return;
        if (playerHealth == null || playerHealth.IsDead) return;

        if (!fireZone.TryWorldToGrid(transform.position, out int gx, out int gy)) return;

        int state = fireZone.FireSim.GetCellState(gy * fireZone.FireSim.Width + gx);

        float dmg = 0f;
        if      (state == 2) dmg = burningDamagePerSecond          * Time.deltaTime;
        else if (state == 3) dmg = embersAdditionalDamagePerSecond * Time.deltaTime;

        if (dmg > 0f)
        {
            playerHealth.TakeDamage(dmg);
            if (debugLog)
                Debug.Log($"[FireInteraction] state={state}  dmg={dmg:F3}  hp={playerHealth.Current:F1}");
        }
    }
}
