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
        [Header("Wander (0 = don't wander)")]
        [Tooltip("Maximum distance from the spawn position to drift to while idle. Set to 0 for a stationary NPC.")]
        public float wanderRadius = 8f;
        [Tooltip("Wander movement speed (m/s).")]
        public float wanderSpeed = 2f;
        [Tooltip("Shortest pause at each wander waypoint (seconds).")]
        public float pauseMin = 0.5f;
        [Tooltip("Longest pause at each wander waypoint (seconds).")]
        public float pauseMax = 2.5f;

        [Header("Chase (0 = don't chase)")]
        [Tooltip("Become aggro'd when the local player is closer than this (meters). 0 disables chasing entirely.")]
        public float aggroRadius = 0f;
        [Tooltip("Release when the local player gets farther than this. Must be > aggroRadius — the gap is hysteresis so the NPC doesn't flicker at the boundary.")]
        public float deaggroRadius = 14f;
        [Tooltip("Chase speed (m/s). Usually faster than wanderSpeed.")]
        public float chaseSpeed = 4f;
        [Tooltip("Stop this close to the player — prevents the NPC climbing into them.")]
        public float minContactDistance = 0.8f;

        [Header("Facing")]
        [Tooltip("Rotate to face the direction of motion (wander) or the target (chase).")]
        public bool faceDirection = true;
        [Tooltip("Turn speed in degrees/second. 0 = instant.")]
        public float turnSpeed = 540f;

        [Header("Determinism")]
        [Tooltip("Random seed for the wander path. -1 uses NetworkObject.LocalId so every NPC has a unique path that every client computes identically.")]
        public int seedOverride = -1;

        [Header("Claim Retry")]
        [Tooltip("If our aggro claim is rejected (another client grabbed first), wait this long before trying again.")]
        public float reclaimCooldown = 1.5f;

        private NetworkObject _netObj;

        // Wander state
        private Vector3 _origin;
        private double _wanderEpoch = -1; // ServerTime when current wander anchor was set
        private bool _wasClaimed;

        // Chase state
        private float _nextClaimAttemptTime;

        private void Awake()
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

        private void Update()
        {
            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsNetworkReady) return;

            if (_wanderEpoch < 0)
                _wanderEpoch = mgr.ServerTime;

            bool claimedByOther = _netObj.IsClaimedByRemote;
            bool claimedByUs    = _netObj.IsClaimedLocally;

            // ── Chase (if enabled) ────────────────────────────────────────
            if (aggroRadius > 0f && mgr.LocalPlayer != null)
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
            if (claimedByUs || claimedByOther)
            {
                _wasClaimed = true;
                return;
            }

            if (_wasClaimed)
            {
                // Just came free — re-anchor at current pos so wander picks up
                // from here rather than teleporting back to its deterministic path.
                _origin = transform.position;
                _wanderEpoch = mgr.ServerTime;
                _wasClaimed = false;
            }

            if (wanderRadius <= 0f) return; // static sentinel

            int seed = seedOverride >= 0 ? seedOverride : _netObj.LocalId;
            double t = mgr.ServerTime - _wanderEpoch;
            Vector3 pos = SampleWander(t, seed, out Vector3 forward);
            transform.position = pos;
            if (faceDirection)
                ApplyFacing(forward);
        }

        private void ApplyFacing(Vector3 forward)
        {
            if (forward.sqrMagnitude < 0.0001f) return;
            Quaternion want = Quaternion.LookRotation(forward);
            transform.rotation = turnSpeed <= 0f
                ? want
                : Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
        }

        // ── Deterministic wander sampler ──────────────────────────────────

        private Vector3 SampleWander(double t, int seed, out Vector3 forward)
        {
            forward = Vector3.zero;
            if (t < 0) t = 0;

            Vector3 current = _origin;
            double cursor = 0;
            int journey = 0;
            float moveSpeed = Mathf.Max(0.1f, wanderSpeed);
            const int SAFETY_CAP = 100000;

            while (journey < SAFETY_CAP)
            {
                var rng = new DeterministicRng(seed, journey);
                Vector3 next = _origin + RandomInDisc(ref rng, wanderRadius);
                float segDist = Vector3.Distance(current, next);
                float walkDur = segDist / moveSpeed;
                float pauseDur = Mathf.Lerp(pauseMin, pauseMax, rng.NextFloat01());

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
            if (wanderRadius > 0f)
            {
                Gizmos.color = new Color(0.3f, 1f, 0.6f, 0.4f);
                Vector3 o = Application.isPlaying ? _origin : transform.position;
                Gizmos.DrawWireSphere(o, wanderRadius);
            }
            if (aggroRadius > 0f)
            {
                Gizmos.color = new Color(1f, 0.4f, 0.4f, 0.8f);
                Gizmos.DrawWireSphere(transform.position, aggroRadius);
                Gizmos.color = new Color(1f, 0.4f, 0.4f, 0.3f);
                Gizmos.DrawWireSphere(transform.position, deaggroRadius);
            }
        }
    }
}
