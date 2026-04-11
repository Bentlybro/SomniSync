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
    ///
    /// Split into partial classes:
    ///   NetworkObject.Interpolation.cs — ApplyUpdate, SnapTo, smoothing helpers
    ///   NetworkObject.Interaction.cs   — Grab, Release, claim/release notifications, push
    /// </summary>
    public partial class NetworkObject : MonoBehaviour
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
        // Interpolation + dead reckoning (used by NetworkObject.Interpolation.cs)
        // =====================================================================

        private Vector3 _fromPos, _toPos;
        private Vector3 _fromRot, _toRot;
        private Vector3 _fromScale, _toScale;
        private float _interpT        = 1f;
        private float _interpDuration = 0.016f;
        private float _lastUpdateReceivedTime = -1f;

        private Vector3 _drVelocity;
        private Vector3 _drBasePos;
        private float   _drBaseTime;
        private bool    _deadReckoning;

        // =====================================================================
        // Delta tracking (for deciding when to send)
        // =====================================================================

        private Vector3 _lastSentPos;
        private Vector3 _lastSentRot;
        private Vector3 _lastSentScale;

        // =====================================================================
        // Auto-claim / push state (used by NetworkObject.Interaction.cs)
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

        // Core component
        private Rigidbody _rb;
        private bool      _hasRb;

        // Optional sibling components — cached at Awake to avoid per-message GetComponent calls
        internal NetworkVariables CachedVariables;
        internal NetworkEvents    CachedEvents;
        internal NetworkAnimator  CachedAnimator;
        internal NetworkRigidbody CachedRigidbody;

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        private void Awake()
        {
            _rb    = GetComponent<Rigidbody>();
            _hasRb = _rb != null;
            if (_hasRb)
                _rb.isKinematic = true; // freeze until authority is assigned

            // Cache optional sibling components so receivers avoid per-message GetComponent calls
            CachedVariables = GetComponent<NetworkVariables>();
            CachedEvents    = GetComponent<NetworkEvents>();
            CachedAnimator  = GetComponent<NetworkAnimator>();
            CachedRigidbody = GetComponent<NetworkRigidbody>();
        }

        // Update() intentionally removed — NetworkManager calls NetworkUpdate() for all
        // objects in a single loop, eliminating 1000 separate MonoBehaviour.Update()
        // dispatch calls per frame (each of which crosses the native/managed boundary).

        /// <summary>
        /// Called once per frame by NetworkManager for every registered object.
        /// Handles interpolation, dead reckoning, and auto-release in one pass.
        /// </summary>
        internal void NetworkUpdate(float deltaTime, float currentTime)
        {
            if (!IsClaimedLocally)
            {
                if (_interpT < 1f)
                {
                    _interpT += deltaTime / _interpDuration;
                    if (_interpT > 1f) _interpT = 1f;

                    transform.position    = Vector3.Lerp(_fromPos, _toPos, _interpT);
                    transform.eulerAngles = LerpAngles(_fromRot, _toRot, _interpT);
                    if (syncScale)
                        transform.localScale = Vector3.Lerp(_fromScale, _toScale, _interpT);
                }
                else if (_deadReckoning && !HasStateAuthority)
                {
                    // Lerp finished — extrapolate using last known velocity (capped at 1s)
                    float dt = Mathf.Min(currentTime - _drBaseTime, 1f);
                    transform.position = _drBasePos + _drVelocity * dt;
                }
            }

            // Auto-release when push-grabbed object settles
            if (_autoClaimedByPush && IsClaimedLocally && _hasRb)
            {
                bool touching = (currentTime - _lastContactTime) < 0.1f;
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
        // Authority management (called by NetworkManager)
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
                _interpT = 1f;
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
                    _drVelocity     = _rb.isKinematic ? Vector3.zero : _rb.linearVelocity;
                    _rb.isKinematic = true;
                }
            }
        }

        // =====================================================================
        // Physics helper (called on initial connection)
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

        /// <summary>True if this object has a Rigidbody (used to split send rates).</summary>
        public bool IsRigidbody => _hasRb;

        /// <summary>
        /// True if the rigidbody has come to rest and needs no update.
        /// Non-rigidbody objects are never considered sleeping.
        /// </summary>
        public bool IsSleeping => _hasRb && !_rb.isKinematic && _rb.IsSleeping();

        // =====================================================================
        // AOI (managed by NetworkManager)
        // =====================================================================

        public void AOIDeactivate()
        {
            if (!IsAOIActive) return;
            // Don't freeze objects that are still in motion (e.g. thrown) — let them land first
            if (_hasRb && !_rb.isKinematic && _rb.linearVelocity.sqrMagnitude > 0.5f) return;
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
    }
}
