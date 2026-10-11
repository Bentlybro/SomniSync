using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// One-component NPC driver — deterministic wander + optional aggro chase.
    ///
    /// Attach this to any GameObject that should behave like an NPC; it takes
    /// care of registering the NetworkObject, forcing the server's physics
    /// body to stay kinematic (so Rapier doesn't fight the AI with gravity),
    /// and running both roam and chase logic on the local client.
    ///
    /// Behaviour is controlled by two ranges:
    ///   • <see cref="wanderRadius"/>   > 0 → the NPC wanders that far from
    ///     its spawn point while idle.
    ///   • <see cref="aggroRadius"/>    > 0 → the NPC chases the local player
    ///     when they get closer than this, releasing at
    ///     <see cref="deaggroRadius"/>.
    ///
    /// Set either, both, or neither. A static sentinel is just
    /// `wanderRadius = 0, aggroRadius = 0`.
    ///
    /// Synchronisation model:
    ///   Wandering is pure deterministic — every client computes the same
    ///   position from <see cref="NetworkManager.ServerTime"/> + a per-NPC
    ///   seed. Zero bandwidth regardless of NPC count.
    ///
    ///   Chasing uses the existing claim infrastructure: first client to
    ///   notice a player in range grabs the NPC, drives it via standard
    ///   held-object updates, and releases when the target escapes. Losing
    ///   clients get CTRL_CLAIM_REJECTED and back off with a cooldown.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    [DefaultExecutionOrder(-100)] // run Awake before NetworkObject captures state
    public class NetworkNPC : MonoBehaviour
    {
        [Tooltip("Enable wandering behaviour. When off the NPC stays put unless chased or grabbed.")]
        public bool canWander = true;
        [Tooltip("Maximum distance from the spawn position to drift to while idle.")]
        public float wanderRadius = 8f;
        [Tooltip("Wander movement speed (m/s).")]
        public float wanderSpeed = 2f;
        [Tooltip("Shortest pause at each wander waypoint (seconds).")]
        public float pauseMin = 0.5f;
        [Tooltip("Longest pause at each wander waypoint (seconds).")]
        public float pauseMax = 2.5f;

        [Tooltip("Enable chasing the local player when they come near.")]
        public bool canChase = false;
        [Tooltip("Become aggro'd when the local player is closer than this (meters).")]
        public float aggroRadius = 10f;
        [Tooltip("Release when the local player gets farther than this. Must be > aggroRadius — the gap is hysteresis so the NPC doesn't flicker at the boundary.")]
        public float deaggroRadius = 14f;
        [Tooltip("Chase speed (m/s). Usually faster than wanderSpeed.")]
        public float chaseSpeed = 4f;
        [Tooltip("Stop this close to the player — prevents the NPC climbing into them.")]
        public float minContactDistance = 0.8f;
        [Tooltip("If our aggro claim is rejected (another client grabbed first), wait this long before trying again.")]
        public float reclaimCooldown = 1.5f;

        [Tooltip("Rotate to face the direction of motion (wander) or the target (chase).")]
        public bool faceDirection = true;
        [Tooltip("Turn speed in degrees/second. 0 = instant.")]
        public float turnSpeed = 540f;

        [Tooltip("Random seed for the wander path. -1 uses NetworkObject.LocalId so every NPC has a unique path that every client computes identically.")]
        public int seedOverride = -1;

        protected NetworkObject _netObj;

        // Wander state. Re-anchor happens when NetworkObject.LastReleaseServerMs
        // advances past _anchoredReleaseMs — meaning we've just received a new
        // server-authoritative release. Every client sees the same values,
        // so every client computes the same wander path after a chase.
        //
        // Before any chase has happened, _wanderEpoch derives from the object's
        // SpawnServerMs (server-authoritative birth timestamp). Do NOT fall back
        // to mgr.ServerTime here — each client's "first local tick" is at a
        // different server time (they joined at different moments), so using
        // that as the epoch puts every client on a different slice of the same
        // deterministic wander path. The resulting position disagreement is
        // exactly (client_A_join_gap_seconds * wanderSpeed) metres.
        //
        // Exposed as `protected` so subclasses can layer custom behaviour on
        // top of the wander/chase logic without forking the whole component.
        protected Vector3 _origin;
        protected double _wanderEpoch = -1;       // Server unix-seconds anchor for the current wander segment
        protected ulong _anchoredReleaseMs;       // LastReleaseServerMs we last re-anchored against

        // Remember the start of the current deterministic journey. Replaying
        // every earlier waypoint on every frame grows with room age.
        private bool _wanderCacheValid;
        private double _cachedEpoch, _cachedCursor;
        private Vector3 _cachedOrigin, _cachedCurrent;
        private int _cachedSeed, _cachedJourney;
        private float _cachedRadius, _cachedSpeed, _cachedPauseMin, _cachedPauseMax;

        // Read-only views of internal wander state — used by NetworkDebug to
        // compare what the NPC's logic is doing on each client side-by-side.
        public Vector3 DebugOrigin           => _origin;
        public double  DebugWanderEpoch      => _wanderEpoch;
        public ulong   DebugAnchoredReleaseMs => _anchoredReleaseMs;

        // Chase state
        protected float _nextClaimAttemptTime;

        protected virtual void Awake()
        {
            _netObj = GetComponent<NetworkObject>();

            // Don't let Rapier simulate physics on this object server-side —
            // we drive it from the client, so gravity / tumbling would fight
            // the script. This must run before NetworkObject.Awake (hence the
            // low DefaultExecutionOrder) so NetworkObject captures the
            // kinematic intent when it reads the Rigidbody.
            var rb = GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            _netObj.forceKinematicOnServer = true;

            _origin = transform.position;
        }

        /// <summary>
        /// Per-frame tick. Subclasses can override to add custom behaviour;
        /// call <c>base.Update()</c> to keep the built-in wander / chase logic,
        /// or ignore the base and drive the NPC entirely yourself.
        /// </summary>
        protected virtual void Update()
        {
            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsNetworkReady) return;

            if (_wanderEpoch < 0)
            {
                // Anchor the wander formula to the object's server-authoritative
                // birth timestamp. For baked-in scene objects this is the shared
                // SceneReadyServerMs; for runtime spawns it's the server time
                // stamped into CTRL_SPAWN. Either way every client lands on the
                // same value, so they compute identical wander positions.
                ulong spawnMs = _netObj.SpawnServerMs;
                if (spawnMs == 0) return; // SceneReadyServerMs not populated yet — defer one tick.
                _wanderEpoch = spawnMs / 1000.0;
            }

            bool claimedByOther = _netObj.IsClaimedByRemote;
            bool claimedByUs    = _netObj.IsClaimedLocally;

            // ── Chase (if enabled) ────────────────────────────────────────
            if (canChase && mgr.LocalPlayer != null)
            {
                Transform target = mgr.LocalPlayer;
                Vector3 toTarget = target.position - transform.position;
                float distSq = toTarget.sqrMagnitude;

                if (claimedByUs)
                {
                    // We're chasing. Release when target escapes.
                    if (distSq > deaggroRadius * deaggroRadius)
                    {
                        _netObj.Release();
                        return;
                    }

                    float dist = Mathf.Sqrt(distSq);
                    if (dist > minContactDistance)
                    {
                        Vector3 dir = toTarget / dist;
                        float step = Mathf.Min(chaseSpeed * Time.deltaTime, dist - minContactDistance);
                        transform.position += dir * step;
                        if (faceDirection)
                            ApplyFacing(new Vector3(dir.x, 0f, dir.z));
                    }
                    return;
                }

                if (!claimedByOther
                    && distSq < aggroRadius * aggroRadius
                    && Time.time >= _nextClaimAttemptTime)
                {
                    _nextClaimAttemptTime = Time.time + reclaimCooldown;
                    // Kinematic claim — we drive transform.position, don't want
                    // Unity physics fighting us mid-chase.
                    _netObj.Grab(enableLocalPhysics: false);
                    return; // next frame will run the chase branch
                }
            }

            // ── Wander ────────────────────────────────────────────────────
            if (claimedByUs || claimedByOther) return;

            // Server-authoritative re-anchor. When the most recent release
            // (shared across all clients) is newer than what we've anchored
            // against, rebind _origin and _wanderEpoch to server values — every
            // client lands on identical inputs, so the deterministic wander
            // sampler produces the same output on every screen.
            if (_netObj.LastReleaseServerMs != 0
                && _netObj.LastReleaseServerMs != _anchoredReleaseMs)
            {
                _origin = _netObj.LastReleasePos;
                _wanderEpoch = _netObj.LastReleaseServerMs / 1000.0;
                _anchoredReleaseMs = _netObj.LastReleaseServerMs;
            }

            if (!canWander) return;

            int seed = seedOverride >= 0 ? seedOverride : _netObj.LocalId;
            double t = mgr.ServerTime - _wanderEpoch;
            Vector3 pos = SampleWander(t, seed, out Vector3 forward);
            transform.position = pos;
            if (faceDirection)
                ApplyFacing(forward);
        }

        /// <summary>
        /// Smoothly rotate the transform to face a direction. Subclasses can
        /// override to use a different facing strategy (head-look, lerp
        /// rather than angular-cap, etc.).
        /// </summary>
        protected virtual void ApplyFacing(Vector3 forward)
        {
            if (forward.sqrMagnitude < 0.0001f) return;
            Quaternion want = Quaternion.LookRotation(forward);
            transform.rotation = turnSpeed <= 0f
                ? want
                : Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
        }

        // ── Deterministic wander sampler ──────────────────────────────────

        /// <summary>
        /// Compute the NPC's wander position at wander-time <paramref name="t"/>
        /// (seconds since <see cref="_wanderEpoch"/>) for the given seed.
        /// All clients call this with the same inputs, so every client
        /// computes the same position — zero bandwidth.
        ///
        /// Override to implement a different movement style (patrol routes,
        /// scripted paths, chasing a moving point, etc.). The out-parameter
        /// <paramref name="forward"/> is the facing direction the NPC should
        /// rotate toward; return zero-vector to leave facing unchanged.
        /// </summary>
        protected virtual Vector3 SampleWander(double t, int seed, out Vector3 forward)
        {
            forward = Vector3.zero;
            if (t < 0) t = 0;
            if (wanderRadius <= 0f) return _origin;

            float moveSpeed = Mathf.Max(0.1f, wanderSpeed);
            if (!_wanderCacheValid || t < _cachedCursor || _cachedEpoch != _wanderEpoch
                || _cachedOrigin != _origin || _cachedSeed != seed
                || _cachedRadius != wanderRadius || _cachedSpeed != moveSpeed
                || _cachedPauseMin != pauseMin || _cachedPauseMax != pauseMax)
            {
                _cachedEpoch = _wanderEpoch;
                _cachedOrigin = _origin;
                _cachedSeed = seed;
                _cachedRadius = wanderRadius;
                _cachedSpeed = moveSpeed;
                _cachedPauseMin = pauseMin;
                _cachedPauseMax = pauseMax;
                _cachedCursor = 0;
                _cachedCurrent = _origin;
                _cachedJourney = 0;
                _wanderCacheValid = true;
            }

            Vector3 current = _cachedCurrent;
            double cursor = _cachedCursor;
            int journey = _cachedJourney;
            const int MAX_ADVANCE_PER_FRAME = 1024;

            for (int advanced = 0; advanced < MAX_ADVANCE_PER_FRAME; advanced++)
            {
                var rng = new DeterministicRng(seed, journey);
                Vector3 next = _origin + RandomInDisc(ref rng, wanderRadius);
                float segDist = Vector3.Distance(current, next);
                float walkDur = segDist / moveSpeed;
                float pauseDur = Mathf.Max(0f, Mathf.Lerp(pauseMin, pauseMax, rng.NextFloat01()));

                if (t < cursor + walkDur)
                {
                    float w = walkDur > 0 ? (float)((t - cursor) / walkDur) : 1f;
                    forward = (next - current).normalized;
                    return Vector3.Lerp(current, next, w);
                }
                cursor += walkDur;

                if (t < cursor + pauseDur)
                    return next;

                cursor += pauseDur;
                current = next;
                journey++;
                _cachedCursor = cursor;
                _cachedCurrent = current;
                _cachedJourney = journey;
            }
            return current;
        }

        private static Vector3 RandomInDisc(ref DeterministicRng rng, float radius)
        {
            float angle = rng.NextFloat01() * Mathf.PI * 2f;
            float r = Mathf.Sqrt(rng.NextFloat01()) * radius;
            return new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
        }

        private struct DeterministicRng
        {
            private uint _state;

            public DeterministicRng(int seed, int journey)
            {
                unchecked
                {
                    uint s = (uint)seed * 2654435761u + (uint)journey * 374761393u + 0x9E3779B9u;
                    if (s == 0) s = 1;
                    _state = s;
                }
            }

            public uint Next()
            {
                unchecked
                {
                    _state ^= _state << 13;
                    _state ^= _state >> 17;
                    _state ^= _state << 5;
                    return _state;
                }
            }

            public float NextFloat01() => (Next() & 0xFFFFFF) / (float)0x1000000;
        }

        private void OnDrawGizmosSelected()
        {
            if (canWander && wanderRadius > 0f)
            {
                Gizmos.color = new Color(0.3f, 1f, 0.6f, 0.4f);
                Vector3 o = Application.isPlaying ? _origin : transform.position;
                Gizmos.DrawWireSphere(o, wanderRadius);
            }
            if (canChase && aggroRadius > 0f)
            {
                Gizmos.color = new Color(1f, 0.4f, 0.4f, 0.8f);
                Gizmos.DrawWireSphere(transform.position, aggroRadius);
                Gizmos.color = new Color(1f, 0.4f, 0.4f, 0.3f);
                Gizmos.DrawWireSphere(transform.position, deaggroRadius);
            }
        }
    }
}
