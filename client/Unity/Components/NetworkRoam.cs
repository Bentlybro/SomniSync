using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Deterministic seeded random-walk roaming for NPCs — zero bandwidth.
    ///
    /// Every client computes the NPC's position from the shared
    /// <see cref="NetworkManager.ServerTime"/> clock and a stable per-object
    /// seed. Same seed + same time = same position on every client, so no
    /// broadcasts are needed; the NPC wanders identically everywhere.
    ///
    /// Pairs cleanly with <see cref="NetworkAgent"/>: when that component
    /// claims the NPC (aggro), roaming pauses automatically; when the claim
    /// is released, roaming re-anchors at the current position and continues.
    ///
    /// Usage:
    ///   1. Drop on an NPC GameObject that already has a NetworkObject.
    ///   2. Set <see cref="wanderRadius"/> (how far it drifts from its start)
    ///      and <see cref="speed"/>.
    ///   3. Done. No extra scripting required.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkRoam : MonoBehaviour
    {
        [Header("Roam")]
        [Tooltip("Maximum distance from the spawn position to drift to.")]
        public float wanderRadius = 10f;
        [Tooltip("Movement speed in meters per second.")]
        public float speed = 2f;
        [Tooltip("Shortest pause at each waypoint (seconds).")]
        public float pauseMin = 0.5f;
        [Tooltip("Longest pause at each waypoint (seconds).")]
        public float pauseMax = 2.5f;
        [Tooltip("Face direction of travel.")]
        public bool faceDirection = true;
        [Tooltip("Turn speed (deg/s) when facing direction. 0 = instant.")]
        public float turnSpeed = 360f;

        [Header("Seed")]
        [Tooltip("Deterministic seed for the random walk. -1 uses the NetworkObject's LocalId so each NPC has its own path.")]
        public int seedOverride = -1;

        private NetworkObject _netObj;

        // Anchor for the current roam pattern. Reset to current position whenever
        // we come out of a chase / claim so roam continues from wherever we were.
        private Vector3 _origin;
        private double _epoch;   // ServerTime when current anchor was set
        private bool _wasClaimed;

        private void Awake()
        {
            _netObj = GetComponent<NetworkObject>();
            _origin = transform.position;
        }

        private void Start()
        {
            // NetworkManager may not be ready yet on first frame — defer epoch init.
            _epoch = -1;
        }

        private void Update()
        {
            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsNetworkReady) return;

            if (_epoch < 0)
                _epoch = mgr.ServerTime;

            bool claimed = _netObj.IsClaimedLocally || _netObj.IsClaimedByRemote;

            if (claimed)
            {
                _wasClaimed = true;
                return; // Someone is driving us (chase, grab, etc.)
            }

            if (_wasClaimed)
            {
                // Just released — re-anchor at current position so the rest of
                // the journey continues naturally from here instead of teleporting
                // back to where the deterministic path would have put us.
                _origin = transform.position;
                _epoch = mgr.ServerTime;
                _wasClaimed = false;
            }

            int seed = seedOverride >= 0 ? seedOverride : _netObj.LocalId;
            double t = mgr.ServerTime - _epoch;
            Vector3 pos = SampleAt(t, seed, out Vector3 forward);
            transform.position = pos;

            if (faceDirection && forward.sqrMagnitude > 0.0001f)
            {
                Quaternion target = Quaternion.LookRotation(forward);
                transform.rotation = turnSpeed <= 0f
                    ? target
                    : Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
            }
        }

        /// <summary>
        /// Deterministically compute the roam position at time t after the
        /// current anchor. Walks through journeys (move + pause) in sequence
        /// until it finds the one that contains t. Cost is O(journey_index);
        /// typically 0-1 per frame since journeys are short.
        /// </summary>
        private Vector3 SampleAt(double t, int seed, out Vector3 forward)
        {
            forward = Vector3.zero;
            if (t < 0) t = 0;

            Vector3 current = _origin;
            double cursor = 0;
            int journey = 0;
            float moveSpeed = Mathf.Max(0.1f, speed);
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

        /// <summary>
        /// Tiny xorshift PRNG seeded deterministically from (seed, journey).
        /// Identical output on every client for the same inputs.
        /// </summary>
        private struct DeterministicRng
        {
            private uint _state;

            public DeterministicRng(int seed, int journey)
            {
                unchecked
                {
                    uint s = (uint)seed * 2654435761u + (uint)journey * 374761393u + 0x9E3779B9u;
                    if (s == 0) s = 1; // xorshift degenerates at zero
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
            Gizmos.color = new Color(0.3f, 1f, 0.6f, 0.4f);
            Vector3 o = Application.isPlaying ? _origin : transform.position;
            Gizmos.DrawWireSphere(o, wanderRadius);
        }
    }
}
