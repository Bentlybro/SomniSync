using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking.Samples
{
    /// <summary>
    /// Minimal WASD + mouse-look controller for the Quick Start sample's
    /// player. Not production-grade — just enough to walk around, bump
    /// into the cube, and watch the networking work.
    ///
    /// Press Escape to release the mouse cursor.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class QuickStartPlayerController : MonoBehaviour
    {
        [Tooltip("Walk speed in metres per second.")]
        public float moveSpeed = 4f;
        [Tooltip("Mouse look sensitivity (degrees per second per Unit of mouse input).")]
        public float lookSpeed = 120f;

        private CharacterController _cc;
        private Transform _cam;
        private float _pitch;
        private Vector3 _velocity;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _cam = GetComponentInChildren<Camera>()?.transform;
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void Update()
        {
            if (_cc == null) return;

            // Yaw the body, pitch the camera.
            float yaw = Input.GetAxis("Mouse X") * lookSpeed * Time.deltaTime;
            float dp  = Input.GetAxis("Mouse Y") * lookSpeed * Time.deltaTime;
            transform.Rotate(0f, yaw, 0f);
            if (_cam != null)
            {
                _pitch = Mathf.Clamp(_pitch - dp, -80f, 80f);
                _cam.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            }

            Vector3 input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            Vector3 worldMove = transform.TransformDirection(input.normalized) * moveSpeed;

            if (_cc.isGrounded && _velocity.y < 0f) _velocity.y = -1f;
            _velocity.y += Physics.gravity.y * Time.deltaTime;

            _cc.Move((worldMove + Vector3.up * _velocity.y) * Time.deltaTime);

            if (Input.GetKeyDown(KeyCode.Escape))
                Cursor.lockState = CursorLockMode.None;
        }
    }
}
