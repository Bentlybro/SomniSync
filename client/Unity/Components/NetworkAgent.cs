using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Simple aggro/chase AI for NPCs. When the local player gets within
    /// <see cref="aggroRadius"/>, this client claims the NPC (via the standard
    /// grab-claim plumbing) and drives it toward the player. When the player
    /// escapes past <see cref="deaggroRadius"/> — hysteresis prevents thrash —
    /// the claim is released and other clients can aggro in turn.
    ///
    /// Per-client distributed AI: whichever player is nearest to a given NPC
    /// gets chased, and that player's client runs the logic locally. No server
    /// authority needed — builds on the existing claim infrastructure plus
    /// claim rejection (first client to grab wins; losers back off).
    ///
    /// Usage:
    ///   1. Drop on an NPC GameObject that already has a NetworkObject.
    ///   2. Set <see cref="aggroRadius"/> and <see cref="chaseSpeed"/>.
    ///   3. Done. Often paired with <see cref="NetworkRoam"/> on the same
    ///      NPC — roam runs while idle, chase takes over when aggroed.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkAgent : MonoBehaviour
    {
        [Header("Aggro")]
        [Tooltip("Become aggro'd when the local player is closer than this (meters).")]
        public float aggroRadius = 10f;
        [Tooltip("Release when the local player is farther than this. Must be > aggroRadius; the gap is hysteresis so the NPC doesn't flicker at the boundary.")]
        public float deaggroRadius = 14f;

        [Header("Motion")]
        [Tooltip("Chase speed in meters per second.")]
        public float chaseSpeed = 4f;
        [Tooltip("Stop inside this distance — prevents jittering into the player.")]
        public float minContactDistance = 0.8f;
        [Tooltip("Face the target while chasing.")]
        public bool faceTarget = true;
        [Tooltip("Turn speed (deg/s). 0 = instant.")]
        public float turnSpeed = 540f;

        [Header("Claim Retry")]
        [Tooltip("If our aggro claim is rejected (another client got there first), wait this long before trying again.")]
        public float reclaimCooldown = 1.5f;

        private NetworkObject _netObj;
        private float _nextClaimAttemptTime;

        private void Awake() => _netObj = GetComponent<NetworkObject>();

        private void Update()
        {
            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsNetworkReady) return;

            Transform target = mgr.LocalPlayer;
            if (target == null) return;

            Vector3 toTarget = target.position - transform.position;
            float distSq = toTarget.sqrMagnitude;

            if (_netObj.IsClaimedLocally)
            {
                // We're actively chasing. Release when target escapes.
                if (distSq > deaggroRadius * deaggroRadius)
                {
                    _netObj.Release();
                    return;
                }

                // Move toward target (but don't climb up their legs).
                float dist = Mathf.Sqrt(distSq);
                if (dist > minContactDistance)
                {
                    Vector3 dir = toTarget / dist;
                    float step = Mathf.Min(chaseSpeed * Time.deltaTime, dist - minContactDistance);
                    transform.position += dir * step;

                    if (faceTarget)
                    {
                        Quaternion want = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z));
                        transform.rotation = turnSpeed <= 0f
                            ? want
                            : Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
                    }
                }
                return;
            }

            if (_netObj.IsClaimedByRemote)
            {
                // Another client is chasing — stay out of it.
                _nextClaimAttemptTime = Time.time + reclaimCooldown;
                return;
            }

            // Free. Consider aggroing.
            if (distSq < aggroRadius * aggroRadius && Time.time >= _nextClaimAttemptTime)
            {
                _nextClaimAttemptTime = Time.time + reclaimCooldown; // dedupe retries
                _netObj.Grab();
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.4f, 0.4f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, aggroRadius);
            Gizmos.color = new Color(1f, 0.4f, 0.4f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, deaggroRadius);
        }
    }
}
