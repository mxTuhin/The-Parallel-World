using UnityEngine;
using UnityEngine.Events;

public class PlayerHealth : MonoBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;

    [Header("Events")]
    public UnityEvent onDeath;

    private float _current;

    public float Current   => _current;
    public float Fraction  => _current / maxHealth;
    public bool  IsDead    => _current <= 0f;

    void OnEnable()
    {
        _current = maxHealth;
    }

    public void TakeDamage(float amount)
    {
        if (IsDead || amount <= 0f) return;
        _current = Mathf.Max(0f, _current - amount);
        if (IsDead) Die();
    }

    public void Heal(float amount)
    {
        if (IsDead || amount <= 0f) return;
        _current = Mathf.Min(maxHealth, _current + amount);
    }

    void Die()
    {
        Debug.Log("[Player] Killed by fire.");
        onDeath?.Invoke();
        // Default: disable the player GameObject. Wire onDeath in inspector for game-over UI.
        gameObject.SetActive(false);
    }
}
