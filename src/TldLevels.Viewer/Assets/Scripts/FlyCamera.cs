using UnityEngine;

namespace TldLevels.Viewer
{

public sealed class FlyCamera : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 35f;
    [SerializeField] private float lookSensitivity = 2.5f;

    private float yaw;
    private float pitch;

    /// <summary>
    /// Set while the UI owns the keyboard (a text field has focus) or the
    /// pointer is locked, so WASD goes to the field instead of the camera.
    /// </summary>
    public bool InputBlocked { get; set; }

    private void Start()
    {
        Vector3 euler = transform.eulerAngles;
        yaw = euler.y;
        pitch = euler.x;
    }

    public void SetMoveSpeed(float speed)
    {
        moveSpeed = Mathf.Clamp(speed, 1f, 10000f);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (InputBlocked)
        {
            return;
        }

        if (Input.GetMouseButtonDown(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            yaw += Input.GetAxisRaw("Mouse X") * lookSensitivity;
            pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * lookSensitivity, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(1.15f, Input.mouseScrollDelta.y), 1f, 10000f);
        float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? 4f : 1f);
        Vector3 movement = new(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        movement += Vector3.up * ((Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f));
        transform.position += transform.TransformDirection(movement.normalized) * (speed * Time.unscaledDeltaTime);
    }

    public float MoveSpeed => moveSpeed;
}
}
