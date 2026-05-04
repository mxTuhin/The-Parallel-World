using UnityEngine;

/// <summary>
/// Attach to the Camera. Assign the player Transform in the Inspector.
/// The offset is captured from the camera's starting position at runtime,
/// so just position the camera how you like in the editor and it will hold that offset.
/// </summary>
public class CameraController : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private float smoothSpeed = 8f;

    private Vector3 _offset;

    private void Start()
    {
        if (target != null)
            _offset = transform.position - target.position;
    }

    private void LateUpdate()
    {
        if (target == null) return;

        Vector3 desired = target.position + _offset;
        transform.position = Vector3.Lerp(transform.position, desired, smoothSpeed * Time.deltaTime);
    }
}
