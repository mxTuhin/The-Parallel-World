using UnityEngine;

public class EnemyHealth : MonoBehaviour
{
    [SerializeField] private float maxHealth = 100f;

    private float _current;
    private EnemyController _controller;

    public bool IsDead => _current <= 0f;

    private void Awake()
    {
        _controller = GetComponent<EnemyController>();
        _current = maxHealth;
    }

    private void OnEnable()
    {
        _current = maxHealth;
    }

    public void TakeDamage(float damage)
    {
        if (IsDead) return;

        _current -= damage;

        if (_current <= 0f)
            _controller.OnDie();
    }
}
