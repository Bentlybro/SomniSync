using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Attached at runtime to the local player's CharacterController (by
    /// <see cref="NetworkManager"/>) — translates player-vs-object collisions
    /// into server-side push impulses.
    ///
    /// Under server-authoritative physics the server's Rapier world handles all
    /// object-object collisions. The client can't usefully apply local physics
    /// to a pushed object because every other sphere is still kinematic on the
    /// client and would act as a wall, preventing the push from propagating.
    /// Instead: detect the hit, compute the player's velocity, send it to the
    /// server, let Rapier apply the impulse, then the resulting motion comes
    /// back via the normal broadcast loop.
    ///
    /// Zero per-frame cost — only runs when the CharacterController actually
    /// collides with something.
    /// </summary>
    public class PlayerPushDetector : MonoBehaviour
    {
        [Tooltip("Multiplier on the character controller's velocity when computing the server-side push impulse. 1 = transfer full player velocity.")]
        public float pushStrength = 1f;

        [Tooltip("Minimum time between push impulses sent for the same object (seconds). Prevents spam when continuously touching.")]
        public float minPushInterval = 0.1f;

        [Tooltip("Minimum character controller speed (m/s) to register a push. Filters out VR head-tracking micro-motion.")]
        public float minSpeed = 0.1f;

        private CharacterController _cc;
        private readonly Dictionary<ushort, float> _lastPushed = new Dictionary<ushort, float>();

        protected virtual void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            var netObj = hit.gameObject.GetComponent<NetworkObject>();
            if (netObj == null) return;
            if (!netObj.pushable) return;

            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsConnected) return;

            Vector3 velocity = _cc != null ? _cc.velocity : Vector3.zero;
            if (velocity.sqrMagnitude < minSpeed * minSpeed) return;

            float now = Time.time;
            if (_lastPushed.TryGetValue(netObj.LocalId, out float lastTime)
                && now - lastTime < minPushInterval) return;
            _lastPushed[netObj.LocalId] = now;

            mgr.SendPushImpulse(netObj.LocalId, velocity * pushStrength);
        }
    }
}
