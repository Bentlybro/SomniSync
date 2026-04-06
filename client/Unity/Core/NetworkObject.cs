using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Attach to any GameObject that should be synced across the network.
    /// Single component — drag it on, done.
    ///
    /// Authority model: every object has a StateAuthorityId (which client simulates it).
    /// The master starts as authority for everything and redistributes to nearby clients
    /// automatically via RecalculateAuthority(). Any client can also claim objects by
    /// grabbing them regardless of StateAuthority.
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
        [Tooltip("Allow players to grab this object")]
        public bool grabbable = true;

        [Header("Smoothing")]
        public SmoothingMode smoothing = SmoothingMode.Medium;

        public enum SmoothingMode { None, Low, Medium, High }

        // Assigned by NetworkManager during deterministic sort
        [HideInInspector] public ushort LocalId;

        // =====================================================================
        // Authority
        // =====================================================================

        /// <summary>
        /// Which client is currently responsible for simulating this object.
        /// 0 = unassigned (master owns by default on first connect).
        /// </summary>
        [HideInInspector] public ushort StateAuthorityId;

        /// <summary>True when this client simulates and sends updates for this object.</summary>
        public bool HasStateAuthority
            => NetworkManager.Instance != null
            && StateAuthorityId == NetworkManager.Instance.ClientId;

        /// <summary>True if local client has control (authority or local claim).</summary>
        public bool HasAuthority()
        {
            if (IsClaimedLocally) return true;
            if (IsClaimedByRemote) return false;
            return HasStateAuthority;
        }

        // =====================================================================
        // Claim state
        // =====================================================================

        public bool IsClaimedLocally  { get; private set; }
        public ushort ClaimedByClient { get; private set; }
        public bool IsClaimedByRemote => ClaimedByClient != 0 && !IsClaimedLocally;

        // =====================================================================
        // Interpolation + dead reckoning
        // =====================================================================

        private Vector3 _fromPos, _toPos;
        private Vector3 _fromRot, _toRot;
        private Vector3 _fromScale, _toScale;
        private float _interpT        = 1f;
        private float _interpDuration = 0.016f;
        private float _lastUpdateReceivedTime = -1f;

        // Dead reckoning — extrapolate between packets using velocity
        private Vector3 _drVelocity;   // velocity from last received update
        private Vector3 _drBasePos;    // position at the moment of last update
        private float   _drBaseTime;   // Time.time at last update
        private bool    _deadReckoning;

        // =====================================================================
        // Delta tracking (for deciding when to send)
        // =====================================================================

        private Vector3 _lastSentPos;
        private Vector3 _lastSentRot;
        private Vector3 _lastSentScale;

        // =====================================================================
        // Auto-claim / push state
        // =====================================================================

        private const float AUTO_RELEASE_DELAY = 1.0f;
        private const float RECLAIM_COOLDOWN   = 0.5f;
        private float _lastContactTime;
        private float _lastReleaseTime;
        private bool  _autoClaimedByPush;

        // =====================================================================
        // AOI state (managed by NetworkManager)
        // =====================================================================

        [HideInInspector] public bool IsAOIActive = true;

        // Components
        private Rigidbody _rb;
        private bool      _hasRb;

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        private void Awake()
        {
            _rb    = GetComponent<Rigidbody>();
            _hasRb = _rb != null;
            if (_hasRb)
                _rb.isKinematic = true; // freeze until authority is assigned
        }

        private void Update()
        {
            if (!IsClaimedLocally)
            {
                if (_interpT < 1f)
                {
                    // Active lerp toward target
                    _interpT += Time.deltaTime / _interpDuration;
                    if (_interpT > 1f) _interpT = 1f;

                    transform.position    = Vector3.Lerp(_fromPos, _toPos, _interpT);
                    transform.eulerAngles = LerpAngles(_fromRot, _toRot, _interpT);
                    if (syncScale)
                        transform.localScale = Vector3.Lerp(_fromScale, _toScale, _interpT);
                }
                else if (_deadReckoning && !HasStateAuthority)
                {
                    // Lerp finished — extrapolate from last known position+velocity.
                    // Cap at 1 s to stop runaway if authority client disconnects.
                    float dt = Mathf.Min(Time.time - _drBaseTime, 1f);
                    transform.position = _drBasePos + _drVelocity * dt;
                }
            }

            // Auto-release when push-grabbed object settles
            if (_autoClaimedByPush && IsClaimedLocally && _hasRb)
            {
                bool touching = (Time.time - _lastContactTime) < 0.1f;
                bool settled  = _rb.linearVelocity.sqrMagnitude  < 0.05f
                             && _rb.angularVelocity.sqrMagnitude < 0.05f;
                if (!touching && settled)
                {
                    _autoClaimedByPush = false;
                    Release();
                }
            }
        }

        // =====================================================================
        // Authority management  (called by NetworkManager)
        // =====================================================================

        /// <summary>
        /// Assign StateAuthority to the given client.
        /// Automatically enables/disables physics on the local machine.
        /// </summary>
        public void SetStateAuthority(ushort clientId)
        {
            bool wasAuthority = HasStateAuthority;
            StateAuthorityId  = clientId;
            bool nowAuthority = HasStateAuthority;

            if (!wasAuthority && nowAuthority)
            {
                // Gained authority: take over physics from dead reckoning
                _deadReckoning = false;
                _interpT = 1f; // stop any active lerp
                if (_hasRb && !IsClaimedByRemote)
                {
                    _rb.isKinematic    = false;
                    _rb.linearVelocity = _drVelocity; // continue from last known velocity
                }
                _lastSentPos = transform.position;
                _lastSentRot = transform.eulerAngles;
            }
            else if (wasAuthority && !nowAuthority)
            {
                // Lost authority: hand off to incoming dead reckoning
                if (_hasRb)
                {
                    _drVelocity      = _rb.isKinematic ? Vector3.zero : _rb.linearVelocity;
                    _rb.isKinematic  = true;
                }
            }
        }

        // =====================================================================
        // Physics helper  (called on initial connection)
        // =====================================================================

        public void SetPhysics(bool enable)
        {
            if (!_hasRb) return;
            _rb.isKinematic = !enable;
            if (enable)
            {
                _lastSentPos = transform.position;
                _lastSentRot = transform.eulerAngles;
            }
        }

        // =====================================================================
        // Movement detection
        // =====================================================================

        public bool HasMoved(float posThreshold, float rotThreshold)
        {
            float posDist = (transform.position - _lastSentPos).sqrMagnitude;
            float rotDist = Quaternion.Angle(
                Quaternion.Euler(_lastSentRot),
                Quaternion.Euler(transform.eulerAngles));
            if (posDist > posThreshold * posThreshold || rotDist > rotThreshold)
                return true;
            if (syncScale && (transform.localScale - _lastSentScale).sqrMagnitude > 0.0001f)
                return true;
            return false;
        }

        public void MarkSent()
        {
            _lastSentPos = transform.position;
            _lastSentRot = transform.eulerAngles;
            if (syncScale) _lastSentScale = transform.localScale;
        }

        /// <summary>Returns the current rigidbody velocity, or zero for static objects.</summary>
        public Vector3 GetCurrentVelocity()
            => (_hasRb && !_rb.isKinematic) ? _rb.linearVelocity : Vector3.zero;

        // =====================================================================
        // Receive — position update (no velocity, legacy 0x01)
        // =====================================================================

        public void ApplyUpdate(Vector3 pos, Vector3 rot, Vector3? scale = null)
        {
            float baseDuration = GetSmoothingDuration();
            if (baseDuration <= 0f)
            {
                transform.position    = pos;
                transform.eulerAngles = rot;
                if (syncScale && scale.HasValue) transform.localScale = scale.Value;
                _lastUpdateReceivedTime = Time.time;
                _deadReckoning = false;
                return;
            }

            float timeSinceLastUpdate = _lastUpdateReceivedTime >= 0f
                ? Time.time - _lastUpdateReceivedTime : baseDuration;
            _lastUpdateReceivedTime = Time.time;

            _fromPos = transform.position;
            _fromRot = transform.eulerAngles;
            _toPos   = pos;
            _toRot   = rot;
            if (syncScale && scale.HasValue) { _fromScale = transform.localScale; _toScale = scale.Value; }
            _interpT        = 0f;
            _interpDuration = Mathf.Clamp(Mathf.Max(baseDuration, timeSinceLastUpdate), baseDuration, 1f);
            _deadReckoning  = false;
        }

        // =====================================================================
        // Receive — position + velocity update (0x0B, dead reckoning)
        // =====================================================================

        public void ApplyUpdate(Vector3 pos, Vector3 rot, Vector3 velocity, Vector3? scale = null)
        {
            _drVelocity    = velocity;
            _drBasePos     = pos;
            _drBaseTime    = Time.time;
            _deadReckoning = velocity.sqrMagnitude > 0.0001f;

            float baseDuration = GetSmoothingDuration();
            if (baseDuration <= 0f)
            {
                transform.position    = pos;
                transform.eulerAngles = rot;
                if (syncScale && scale.HasValue) transform.localScale = scale.Value;
                _lastUpdateReceivedTime = Time.time;
                return;
            }

            float timeSinceLastUpdate = _lastUpdateReceivedTime >= 0f
                ? Time.time - _lastUpdateReceivedTime : baseDuration;
            _lastUpdateReceivedTime = Time.time;
            _interpDuration = Mathf.Clamp(Mathf.Max(baseDuration, timeSinceLastUpdate), baseDuration, 1f);

            _fromPos = transform.position;
            _fromRot = transform.eulerAngles;
            // Predict where the object will be when the lerp finishes so we hand off
            // seamlessly to dead reckoning without a position jump.
            _toPos   = _deadReckoning ? pos + velocity * _interpDuration : pos;
            _toRot   = rot;
            if (syncScale && scale.HasValue) { _fromScale = transform.localScale; _toScale = scale.Value; }
            _interpT = 0f;
        }

        // =====================================================================
        // Receive — snap (initial snapshot, no interpolation)
        // =====================================================================

        public void SnapTo(Vector3 pos, Vector3 rot, Vector3? scale = null)
        {
            transform.position    = pos;
            transform.eulerAngles = rot;
            _fromPos = _toPos = pos;
            _fromRot = _toRot = rot;
            if (syncScale && scale.HasValue)
            {
                transform.localScale = scale.Value;
                _fromScale = _toScale = scale.Value;
            }
            _interpT = 1f;
            _deadReckoning = false;
            _lastUpdateReceivedTime = -1f;
        }

        // =====================================================================
        // Grab / Release
        // =====================================================================

        public void Grab()
        {
            if (!grabbable) return;
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (IsClaimedLocally) return;

            IsClaimedLocally = true;
            ClaimedByClient  = NetworkManager.Instance.ClientId;

            if (!HasStateAuthority && _hasRb)
                _rb.isKinematic = false; // enable physics for local movement

            NetworkManager.Instance.SendClaim(LocalId);
            _lastSentPos = transform.position;
            _lastSentRot = transform.eulerAngles;
        }

        public void Release()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (!IsClaimedLocally) return;

            NetworkManager.Instance.SendInteractionUpdate(this);

            IsClaimedLocally = false;
            ClaimedByClient  = 0;
            _lastReleaseTime = Time.time;

            if (!HasStateAuthority && _hasRb)
            {
                _rb.linearVelocity  = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic     = true;
                _fromPos = _toPos = transform.position;
                _fromRot = _toRot = transform.eulerAngles;
                _interpT = 1f;
            }

            NetworkManager.Instance.SendRelease(LocalId);
        }

        // =====================================================================
        // Remote claim / release notifications
        // =====================================================================

        public void OnRemoteClaim(ushort clientId)
        {
            ClaimedByClient = clientId;
            if (HasStateAuthority && _hasRb)
                _rb.isKinematic = true; // pause our physics while remote holds it
        }

        public void OnRemoteRelease()
        {
            ClaimedByClient = 0;
            if (HasStateAuthority && _hasRb)
            {
                _rb.isKinematic    = false;
                _rb.linearVelocity  = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
        }

        // =====================================================================
        // Push detection (called by PlayerPushDetector)
        // =====================================================================

        public void OnPlayerPush()
        {
            if (!pushable) return;
            _lastContactTime = Time.time;
            if (!IsClaimedLocally && !IsClaimedByRemote
                && (Time.time - _lastReleaseTime) > RECLAIM_COOLDOWN)
            {
                _autoClaimedByPush = true;
                Grab();
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsClaimedLocally || !_hasRb) return;
            if (_rb.linearVelocity.sqrMagnitude < 0.1f) return;
            var other = collision.gameObject.GetComponent<NetworkObject>();
            if (other == null || other.IsClaimedLocally || other.IsClaimedByRemote) return;
            other.OnPlayerPush();
        }

        // =====================================================================
        // AOI (managed by NetworkManager)
        // =====================================================================

        public void AOIDeactivate()
        {
            if (!IsAOIActive) return;
            IsAOIActive = false;
            if (_hasRb && !_rb.isKinematic)
            {
                _rb.linearVelocity  = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic     = true;
            }
        }

        public void AOIActivate()
        {
            if (IsAOIActive) return;
            IsAOIActive = true;
            if (HasStateAuthority && !IsClaimedByRemote && _hasRb)
                _rb.isKinematic = false;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private float GetSmoothingDuration()
        {
            switch (smoothing)
            {
                case SmoothingMode.None:   return 0f;
                case SmoothingMode.Low:    return 0.033f;
                case SmoothingMode.Medium: return 0.05f;
                case SmoothingMode.High:   return 0.1f;
                default:                   return 0.05f;
            }
        }

        private static Vector3 LerpAngles(Vector3 from, Vector3 to, float t) => new Vector3(
            Mathf.LerpAngle(from.x, to.x, t),
            Mathf.LerpAngle(from.y, to.y, t),
            Mathf.LerpAngle(from.z, to.z, t));
    }
}
