using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Attach to any GameObject that should be synced across the network.
    /// Single component — that's it. No setup needed beyond dragging it on.
    /// </summary>
    public class NetworkObject : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Type name used for deterministic sorting (e.g. 'cube', 'ball', 'enemy')")]
        public string entityType = "object";

        [Header("Smoothing")]
        public SmoothingMode smoothing = SmoothingMode.Medium;

        // Assigned by NetworkManager during deterministic sort
        [HideInInspector] public ushort LocalId;

        // Snapshot interpolation
        private Vector3 _fromPos, _toPos;
        private Vector3 _fromRot, _toRot;
        private float _interpT = 1f;
        private float _interpDuration = 0.016f; // ~60Hz

        // Delta tracking (master only)
        private Vector3 _lastSentPos;
        private Vector3 _lastSentRot;

        // Components
        private Rigidbody _rb;
        private bool _hasRb;

        public enum SmoothingMode
        {
            None,      // Snap to position
            Low,       // Fast interpolation
            Medium,    // Balanced
            High       // Smooth but more latency
        }

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _hasRb = _rb != null;

            // Freeze physics immediately — will be re-enabled for master only
            if (_hasRb)
                _rb.isKinematic = true;
        }

        private void Update()
        {
            // Spectator interpolation
            if (_interpT < 1f)
            {
                _interpT += Time.deltaTime / _interpDuration;
                if (_interpT > 1f) _interpT = 1f;

                transform.position = Vector3.Lerp(_fromPos, _toPos, _interpT);
                transform.eulerAngles = LerpAngles(_fromRot, _toRot, _interpT);
            }
        }

        /// <summary>
        /// Called by NetworkManager when role is determined.
        /// Master: enable physics. Spectator: keep kinematic.
        /// </summary>
        public void SetPhysics(bool enable)
        {
            if (!_hasRb) return;
            _rb.isKinematic = !enable;
            if (enable)
            {
                // Master — record initial position for delta tracking
                _lastSentPos = transform.position;
                _lastSentRot = transform.eulerAngles;
            }
        }

        /// <summary>
        /// Has this object moved enough to be included in the next update?
        /// </summary>
        public bool HasMoved(float posThreshold, float rotThreshold)
        {
            float posDist = (transform.position - _lastSentPos).sqrMagnitude;
            float rotDist = Quaternion.Angle(
                Quaternion.Euler(_lastSentRot),
                Quaternion.Euler(transform.eulerAngles)
            );
            return posDist > posThreshold * posThreshold || rotDist > rotThreshold;
        }

        /// <summary>
        /// Mark position as sent (reset delta tracking).
        /// </summary>
        public void MarkSent()
        {
            _lastSentPos = transform.position;
            _lastSentRot = transform.eulerAngles;
        }

        /// <summary>
        /// Spectator: apply a position update with interpolation.
        /// </summary>
        public void ApplyUpdate(Vector3 pos, Vector3 rot)
        {
            float duration = GetSmoothingDuration();
            if (duration <= 0f)
            {
                transform.position = pos;
                transform.eulerAngles = rot;
                return;
            }

            _fromPos = transform.position;
            _fromRot = transform.eulerAngles;
            _toPos = pos;
            _toRot = rot;
            _interpT = 0f;
            _interpDuration = duration;
        }

        /// <summary>
        /// Spectator: snap to position immediately (used for initial snapshot).
        /// </summary>
        public void SnapTo(Vector3 pos, Vector3 rot)
        {
            transform.position = pos;
            transform.eulerAngles = rot;
            _fromPos = _toPos = pos;
            _fromRot = _toRot = rot;
            _interpT = 1f;
        }

        private float GetSmoothingDuration()
        {
            switch (smoothing)
            {
                case SmoothingMode.None: return 0f;
                case SmoothingMode.Low: return 0.033f;     // ~30Hz
                case SmoothingMode.Medium: return 0.05f;   // 50ms
                case SmoothingMode.High: return 0.1f;      // 100ms
                default: return 0.05f;
            }
        }

        private static Vector3 LerpAngles(Vector3 from, Vector3 to, float t)
        {
            return new Vector3(
                Mathf.LerpAngle(from.x, to.x, t),
                Mathf.LerpAngle(from.y, to.y, t),
                Mathf.LerpAngle(from.z, to.z, t)
            );
        }
    }
}
