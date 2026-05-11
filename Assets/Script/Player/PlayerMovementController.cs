using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController), typeof(Animator))]
public class PlayerMovementController : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rotationSpeed = 720f;
    [SerializeField] private float gravity = -20f;

    private CharacterController _cc;
    private Animator _animator;
    private Vector3 _verticalVelocity;
    private Vector2 _rawInput;

    // Set to true by PlayerShooterController while player has a target
    public bool IsShootingMode { get; set; }

    private static readonly int IsRunning = Animator.StringToHash("isRunning");

    private void Awake()
    {
        _cc       = GetComponent<CharacterController>();
        _animator = GetComponent<Animator>();
    }

    private void Update()
    {
        // Poll devices directly — works regardless of PlayerInput / action map setup
        float h = 0f, v = 0f;

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  h -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    v += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  v -= 1f;
        }

        var gp = Gamepad.current;
        if (gp != null)
        {
            Vector2 stick = gp.leftStick.ReadValue();
            if (stick.sqrMagnitude > 0.04f) // deadzone
            {
                h = stick.x;
                v = stick.y;
            }
        }

        _rawInput = new Vector2(Mathf.Clamp(h, -1f, 1f), Mathf.Clamp(v, -1f, 1f));
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        if (_cc.isGrounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -2f;

        _verticalVelocity.y += gravity * dt;

        Vector3 moveDir = new Vector3(_rawInput.x, 0f, _rawInput.y).normalized;
        bool isMoving = moveDir.sqrMagnitude > 0.01f;

        // In shooting mode, input moves player but does not rotate — shooter handles rotation
        if (isMoving && !IsShootingMode)
        {
            Quaternion targetRot = Quaternion.LookRotation(moveDir);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, rotationSpeed * dt);
        }

        Vector3 motion = moveDir * moveSpeed;
        motion.y = _verticalVelocity.y;
        _cc.Move(motion * dt);

        _animator.SetBool(IsRunning, isMoving);
    }
}
