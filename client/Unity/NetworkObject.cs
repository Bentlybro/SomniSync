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

        /// <summary>
        /// True if this object is currently claimed by the local client for interaction.
        /// </summary>
        public bool IsClaimedLocally { get; private set; }

        /// <summary>
        /// Client ID that currently owns this object (0 = master/default).
        /// </summary>
        public ushort ClaimedByClient { get; private set; }

        // Snapshot interpolation
        private Vector3 _fromPos, _toPos;
        private Vector3 _fromRot, _toRot;
        private float _interpT = 1f;
        private float _interpDuration = 0.016f; // ~60Hz

        // Delta tracking (for sending updates)
        private Vector3 _lastSentPos;
        private Vector3 _lastSentRot;

        // Auto-claim: release after no proximity for this duration
        private const float AUTO_RELEASE_DELAY = 1.0f;
        private const float CLAIM_RADIUS = 1.5f;   // How close player needs to be to push
        private const float RELEASE_RADIUS = 2.5f;  // Release when player is this far
        private float _lastProximityTime;
        private bool _autoClaimedByProximity;
        private static Transform _localPlayer;

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
            // Spectator interpolation (only when not claimed locally)
            if (_interpT < 1f && !IsClaimedLocally)
            {
                _interpT += Time.deltaTime / _interpDuration;
                if (_interpT > 1f) _interpT = 1f;

                transform.position = Vector3.Lerp(_fromPos, _toPos, _interpT);
                transform.eulerAngles = LerpAngles(_fromRot, _toRot, _interpT);
            }

            // Proximity-based auto-claim/release for spectators
            if (NetworkManager.Instance != null && !NetworkManager.Instance.IsMaster &&
                NetworkManager.Instance.IsConnected && _hasRb)
            {
                CheckPlayerProximity();
            }
        }

        // =================================================================
        // Auto-claim via proximity (CharacterController doesn't fire OnCollisionEnter)
        // =================================================================

        private void CheckPlayerProximity()
        {
            // Find local player once and cache
            if (_localPlayer == null)
            {
                _localPlayer = FindLocalPlayerBody();
                if (_localPlayer == null) return;
            }

            float dist = Vector3.Distance(transform.position, _localPlayer.position);

            if (!IsClaimedLocally)
            {
                // Claim when player is close enough and object isn't owned by someone else
                if (dist < CLAIM_RADIUS && !IsClaimedByRemote)
                {
                    _autoClaimedByProximity = true;
                    _lastProximityTime = Time.time;
                    Grab();
                }
            }
            else if (_autoClaimedByProximity)
            {
                // Track proximity while claimed
                if (dist < RELEASE_RADIUS)
                {
                    _lastProximityTime = Time.time;
                }
                else
                {
                    float elapsed = Time.time - _lastProximityTime;
                    bool settled = _rb.velocity.sqrMagnitude < 0.01f &&
                                   _rb.angularVelocity.sqrMagnitude < 0.01f;
                    if (elapsed > AUTO_RELEASE_DELAY || (settled && elapsed > 0.2f))
                    {
                        _autoClaimedByProximity = false;
                        Release();
                    }
                }
            }
        }

        /// <summary>
        /// Find the local player's body transform (NonXR.Body or XR.Body).
        /// Searches for Somnium's player hierarchy once, caches result.
        /// </summary>
        private static Transform FindLocalPlayerBody()
        {
            // Look for the Original Player root (local player)
            foreach (var go in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                Transform t = go.transform;
                // Check if this is NonXR.Body or XR.Body
                if (t.name == "NonXR.Body" || t.name == "XR.Body")
                {
                    // Verify it's the local "Original Player" (not a Simulator/remote)
                    Transform root = t.parent;
                    while (root != null && root.parent != null)
                        root = root.parent;
                    if (root != null && root.name.StartsWith("Original Player:"))
                        return t;
                }
            }
            return null;
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

        /// <summary>
        /// Call when the local player grabs/interacts with this object.
        /// Works for both master and spectator clients.
        /// </summary>
        public void Grab()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (IsClaimedLocally) return; // Already claimed

            IsClaimedLocally = true;
            ClaimedByClient = NetworkManager.Instance.ClientId;

            if (!NetworkManager.Instance.IsMaster)
            {
                // Spectator: enable physics so the player can move it
                if (_hasRb) _rb.isKinematic = false;
            }

            // Tell everyone we own this now
            NetworkManager.Instance.SendClaim(LocalId);
            _lastSentPos = transform.position;
            _lastSentRot = transform.eulerAngles;
        }

        /// <summary>
        /// Call when the local player releases this object.
        /// </summary>
        public void Release()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (!IsClaimedLocally) return;

            IsClaimedLocally = false;
            ClaimedByClient = 0;

            if (!NetworkManager.Instance.IsMaster)
            {
                // Spectator: return to kinematic
                if (_hasRb) _rb.isKinematic = true;
            }

            // Tell everyone we released it
            NetworkManager.Instance.SendRelease(LocalId);
        }

        /// <summary>
        /// Called by NetworkManager when a remote client claims this object.
        /// </summary>
        public void OnRemoteClaim(ushort clientId)
        {
            ClaimedByClient = clientId;

            if (NetworkManager.Instance != null && NetworkManager.Instance.IsMaster)
            {
                // Master: stop simulating this object (remote client controls it now)
                if (_hasRb) _rb.isKinematic = true;
            }
        }

        /// <summary>
        /// Called by NetworkManager when a remote client releases this object.
        /// </summary>
        public void OnRemoteRelease()
        {
            ClaimedByClient = 0;

            if (NetworkManager.Instance != null && NetworkManager.Instance.IsMaster)
            {
                // Master: resume simulating this object
                if (_hasRb) _rb.isKinematic = false;
            }
        }

        /// <summary>
        /// Is this object currently claimed by a non-master client?
        /// Master uses this to skip it in position updates.
        /// </summary>
        public bool IsClaimedByRemote
        {
            get { return ClaimedByClient != 0 && !IsClaimedLocally; }
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
