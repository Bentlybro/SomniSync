using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Attach to any GameObject that should be synced across the network.
    /// Single component — drag it on, done.
    ///
    /// Architecture: the server owns physics. The local client only simulates
    /// an object while it is grabbed locally (IsClaimedLocally). At all other
    /// times the object is kinematic and driven by server position broadcasts.
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
        // Claim state
        // =====================================================================

        public bool IsClaimedLocally  { get; private set; }
        public ushort ClaimedByClient { get; private set; }
        public bool IsClaimedByRemote => ClaimedByClient != 0 && !IsClaimedLocally;

        /// <summary>
        /// True if this client is allowed to write sibling-component state
        /// (NetworkVariables, NetworkAnimator, NetworkTimer, NetworkEvents).
        ///
        /// Rule: yes if we hold this object, or nobody does. That way grabbed
        /// items are written by the grabber, and free-floating objects (NPCs,
        /// ambient props) accept writes from any client — the server just relays.
        /// </summary>
        public bool HasAuthority() => IsClaimedLocally || !IsClaimedByRemote;

        // =====================================================================
        // Interpolation + dead reckoning (used by NetworkObject.Interpolation.cs)
        // =====================================================================

        private Vector3 _fromPos, _toPos;
        private Quaternion _fromRot, _toRot;
        private Vector3 _fromScale, _toScale;
        private float _interpT        = 1f;
        private float _interpDuration = 0.016f;
        private float _lastUpdateReceivedTime = -1f;

        // =====================================================================
        // Delta tracking for held-object sends
        // =====================================================================

        private Vector3 _lastSentPos;
        private Quaternion _lastSentRot;
        private Vector3 _lastSentScale;

        // Core component
        private Rigidbody _rb;
        private bool      _hasRb;
        private bool      _wasKinematicAtAwake;

        /// <summary>
        /// Set to true by AI/controller components (e.g. <c>NetworkNPC</c>)
        /// that drive the transform themselves and don't want the server's
        /// Rapier simulation fighting them with gravity, tumbling, etc.
        /// When true, SendSceneInit sends this object as KinematicPosition
        /// regardless of the prefab's Rigidbody configuration.
        /// </summary>
        [HideInInspector] public bool forceKinematicOnServer;

        /// <summary>
        /// True when the user configured this object as a Dynamic rigidbody in
        /// the inspector. We force isKinematic = true at Awake (so local physics
        /// doesn't diverge before the server is ready), so SendSceneInit can't
        /// rely on the current isKinematic flag — it must use this instead.
        /// Honoured only when <see cref="forceKinematicOnServer"/> is false.
        /// </summary>
        public bool WantsDynamicPhysics => _hasRb && !_wasKinematicAtAwake && !forceKinematicOnServer;

        // Optional sibling components — cached at Awake to avoid per-message GetComponent calls
        internal NetworkVariables CachedVariables;
        internal NetworkEvents    CachedEvents;
        internal NetworkAnimator  CachedAnimator;

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        private void Awake()
        {
            _rb    = GetComponent<Rigidbody>();
            _hasRb = _rb != null;
            if (_hasRb)
            {
                // Capture the inspector-set kinematic flag as the user's intent, then
                // force kinematic so local physics doesn't run before connect (which
                // would desync every client to a different position). The server is
                // the physics authority once SCENE_INIT goes up.
                _wasKinematicAtAwake = _rb.isKinematic;
                _rb.isKinematic = true;
            }

            CachedVariables = GetComponent<NetworkVariables>();
            CachedEvents    = GetComponent<NetworkEvents>();
            CachedAnimator  = GetComponent<NetworkAnimator>();
        }

        /// <summary>
        /// Called once per frame by NetworkManager for every registered object.
        /// Advances interpolation toward the most recent server state.
        /// </summary>
        internal void NetworkUpdate(float deltaTime, float currentTime)
        {
            if (!IsClaimedLocally && _interpT < 1f)
            {
                _interpT += deltaTime / _interpDuration;
                if (_interpT > 1f) _interpT = 1f;

                transform.position = Vector3.Lerp(_fromPos, _toPos, _interpT);
                transform.rotation = Quaternion.Slerp(_fromRot, _toRot, _interpT);
                if (syncScale)
                    transform.localScale = Vector3.Lerp(_fromScale, _toScale, _interpT);
            }
        }

        // =====================================================================
        // Movement detection (used by held-object send path)
        // =====================================================================

        public bool HasMoved(float posThreshold, float rotThreshold)
        {
            float posDist = (transform.position - _lastSentPos).sqrMagnitude;
            float rotDist = Quaternion.Angle(_lastSentRot, transform.rotation);
            if (posDist > posThreshold * posThreshold || rotDist > rotThreshold)
                return true;
            if (syncScale && (transform.localScale - _lastSentScale).sqrMagnitude > 0.0001f)
                return true;
            return false;
        }

        public void MarkSent()
        {
            _lastSentPos = transform.position;
            _lastSentRot = transform.rotation;
            if (syncScale) _lastSentScale = transform.localScale;
        }

        /// <summary>True if this object has a Rigidbody.</summary>
        public bool IsRigidbody => _hasRb;
    }
}
