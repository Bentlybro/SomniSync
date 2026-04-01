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

        [Header("Sync")]
        [Tooltip("Also sync this object's scale (adds 6 bytes per update)")]
        public bool syncScale = false;

        [Header("Interaction")]
        [Tooltip("Allow players to push/move this object by walking into it")]
        public bool pushable = true;
        [Tooltip("Allow players to grab this object (for custom grab systems)")]
        public bool grabbable = true;

        [Header("Smoothing")]
        public SmoothingMode smoothing = SmoothingMode.Medium;

        // Assigned by NetworkManager during deterministic sort
        [HideInInspector] public ushort LocalId;

        /// <summary>
        /// True if this object is currently claimed by the local client for interaction.
        /// </summary>
        public bool IsClaimedLocally { get; private set; }

        /// <summary>True if this client has authority (master for unowned, or local claimer).</summary>
        public bool HasAuthority()
        {
            if (IsClaimedLocally) return true;
            if (IsClaimedByRemote) return false;
            return NetworkManager.Instance != null && NetworkManager.Instance.IsMaster;
        }

        /// <summary>
        /// Client ID that currently owns this object (0 = master/default).
        /// </summary>
        public ushort ClaimedByClient { get; private set; }

        // Snapshot interpolation
        private Vector3 _fromPos, _toPos;
        private Vector3 _fromRot, _toRot;
        private Vector3 _fromScale, _toScale;
        private float _interpT = 1f;
        private float _interpDuration = 0.016f; // ~60Hz

        // Delta tracking (for sending updates)
        private Vector3 _lastSentPos;
        private Vector3 _lastSentRot;
        private Vector3 _lastSentScale;

        // HasMoved frame cache — prevents redundant Quaternion.Angle() calls when
        // SendPositionUpdates() iterates objects twice per tick (count pass + write pass).
        private int _hasMovedFrame = -1;
        private bool _hasMovedResult;

        // Auto-claim state (managed by PlayerPushDetector)
        private const float AUTO_RELEASE_DELAY = 1.0f;
        private const float RECLAIM_COOLDOWN = 0.5f;
        private float _lastContactTime;
        private float _lastReleaseTime;
        private bool _autoClaimedByPush;

        // AOI state (managed by NetworkManager)
        [HideInInspector] public bool IsAOIActive = true;

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
                if (syncScale)
                    transform.localScale = Vector3.Lerp(_fromScale, _toScale, _interpT);
            }

            // Auto-release: keep claimed while moving, release once settled
            if (_autoClaimedByPush && IsClaimedLocally && _hasRb)
            {
                bool touching = (Time.time - _lastContactTime) < 0.1f;
                bool settled = _rb.linearVelocity.sqrMagnitude < 0.05f &&
                               _rb.angularVelocity.sqrMagnitude < 0.05f;

                // Release when: not touching AND object has stopped moving
                if (!touching && settled)
                {
                    _autoClaimedByPush = false;
                    Release();
                }
            }
        }

        // =================================================================
        // Push detection (called by PlayerPushDetector on the player body)
        // =================================================================

        /// <summary>
        /// Called by PlayerPushDetector when the local player's CharacterController
        /// hits this object. Zero per-frame cost — only fires on actual collision.
        /// </summary>
        public void OnPlayerPush()
        {
            if (!pushable) return;

            _lastContactTime = Time.time;

            if (!IsClaimedLocally && !IsClaimedByRemote &&
                (Time.time - _lastReleaseTime) > RECLAIM_COOLDOWN)
            {
                _autoClaimedByPush = true;
                Grab();
            }
        }

        // =================================================================
        // Chain claiming: claimed objects that hit other objects claim them too
        // =================================================================

        private void OnCollisionEnter(Collision collision)
        {
            // Only chain-claim if WE are claimed locally and have velocity
            if (!IsClaimedLocally || !_hasRb) return;
            if (_rb.linearVelocity.sqrMagnitude < 0.1f) return;

            var other = collision.gameObject.GetComponent<NetworkObject>();
            if (other == null) return;
            if (other.IsClaimedLocally || other.IsClaimedByRemote) return;

            // Chain claim: this moving object hit a static one, claim it too
            other.OnPlayerPush();
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
        /// Result is cached per-frame: SendPositionUpdates iterates objects twice
        /// (count pass then write pass) so we avoid redundant Quaternion.Angle calls.
        /// Cache is invalidated by MarkSent() when the tracking baseline changes.
        /// </summary>
        public bool HasMoved(float posThreshold, float rotThreshold)
        {
            if (_hasMovedFrame == Time.frameCount)
                return _hasMovedResult;

            _hasMovedFrame = Time.frameCount;
            _hasMovedResult = ComputeHasMoved(posThreshold, rotThreshold);
            return _hasMovedResult;
        }

        private bool ComputeHasMoved(float posThreshold, float rotThreshold)
        {
            float posDist = (transform.position - _lastSentPos).sqrMagnitude;
            float rotDist = Quaternion.Angle(
                Quaternion.Euler(_lastSentRot),
                Quaternion.Euler(transform.eulerAngles)
            );
            if (posDist > posThreshold * posThreshold || rotDist > rotThreshold)
                return true;
            if (syncScale && (transform.localScale - _lastSentScale).sqrMagnitude > 0.0001f)
                return true;
            return false;
        }

        /// <summary>
        /// Mark position as sent (reset delta tracking). Also invalidates the HasMoved
        /// frame cache so the write pass doesn't re-include this object in the same tick.
        /// </summary>
        public void MarkSent()
        {
            _lastSentPos = transform.position;
            _lastSentRot = transform.eulerAngles;
            if (syncScale) _lastSentScale = transform.localScale;
            _hasMovedFrame = -1; // Invalidate: tracking baseline just changed
        }

        /// <summary>
        /// Spectator: apply a position update with interpolation.
        /// </summary>
        public void ApplyUpdate(Vector3 pos, Vector3 rot, Vector3? scale = null)
        {
            float duration = GetSmoothingDuration();
            if (duration <= 0f)
            {
                transform.position = pos;
                transform.eulerAngles = rot;
                if (syncScale && scale.HasValue)
                    transform.localScale = scale.Value;
                return;
            }

            _fromPos = transform.position;
            _fromRot = transform.eulerAngles;
            _toPos = pos;
            _toRot = rot;
            if (syncScale && scale.HasValue)
            {
                _fromScale = transform.localScale;
                _toScale = scale.Value;
            }
            _interpT = 0f;
            _interpDuration = duration;
        }

        /// <summary>
        /// Spectator: snap to position immediately (used for initial snapshot).
        /// </summary>
        public void SnapTo(Vector3 pos, Vector3 rot, Vector3? scale = null)
        {
            transform.position = pos;
            transform.eulerAngles = rot;
            _fromPos = _toPos = pos;
            _fromRot = _toRot = rot;
            if (syncScale && scale.HasValue)
            {
                transform.localScale = scale.Value;
                _fromScale = _toScale = scale.Value;
            }
            _interpT = 1f;
        }

        /// <summary>
        /// Call when the local player grabs/interacts with this object.
        /// Works for both master and spectator clients.
        /// </summary>
        public void Grab()
        {
            if (!grabbable) return;
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

            // Send final position BEFORE releasing so master adopts it
            NetworkManager.Instance.SendInteractionUpdate(this);

            IsClaimedLocally = false;
            ClaimedByClient = 0;
            _lastReleaseTime = Time.time;

            if (!NetworkManager.Instance.IsMaster)
            {
                // Spectator: return to kinematic, lock in final position
                if (_hasRb)
                {
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                    _rb.isKinematic = true;
                }
                // Set interpolation target to current pos so we don't snap away
                _fromPos = _toPos = transform.position;
                _fromRot = _toRot = transform.eulerAngles;
                _interpT = 1f;
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
                // Master: resume simulating from where the spectator left it
                if (_hasRb)
                {
                    _rb.isKinematic = false;
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                }
            }
        }

        /// <summary>
        /// Called by NetworkManager AOI system. Freezes physics and excludes from updates.
        /// </summary>
        public void AOIDeactivate()
        {
            if (!IsAOIActive) return;
            IsAOIActive = false;
            if (_hasRb && !_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic = true;
            }
        }

        /// <summary>
        /// Called by NetworkManager AOI system. Re-enables physics simulation.
        /// </summary>
        public void AOIActivate()
        {
            if (IsAOIActive) return;
            IsAOIActive = true;
            // Only re-enable physics if we're master and object isn't remotely claimed
            if (NetworkManager.Instance != null && NetworkManager.Instance.IsMaster && !IsClaimedByRemote)
            {
                if (_hasRb)
                    _rb.isKinematic = false;
            }
        }

        /// <summary>
        /// True while this object is interpolating toward a received network position.
        /// NetworkRigidbody uses this to avoid adding extrapolation on top of an active lerp.
        /// </summary>
        public bool IsInterpolating => _interpT < 1f;

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
            // Base unit = one send interval. Durations relative to tick rate so the lerp
            // always finishes close to when the NEXT packet arrives regardless of sendRate.
            // Fixed values (50ms, 100ms) were longer than the 16ms send interval at 60Hz,
            // causing perpetual-chase where objects never reached their target position.
            float tick = NetworkManager.Instance != null
                ? 1f / NetworkManager.Instance.sendRate
                : 0.016f; // fallback if called before NetworkManager is ready

            switch (smoothing)
            {
                case SmoothingMode.None:   return 0f;
                case SmoothingMode.Low:    return tick * 0.5f;  // finishes mid-tick (minimal latency)
                case SmoothingMode.Medium: return tick;          // exactly one send interval
                case SmoothingMode.High:   return tick * 1.5f;  // 1.5 ticks (smoothest, most latency)
                default:                   return tick;
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
