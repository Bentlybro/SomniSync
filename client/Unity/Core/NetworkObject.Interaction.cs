using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles grab/release, remote claim notifications, and push-to-claim for NetworkObject.
    ///
    /// Claim flow under server-authoritative physics:
    ///   - Grab: local rigidbody becomes non-kinematic (client simulates locally for
    ///     instant feel), server is notified and flips its Rapier body to Kinematic
    ///     and accepts client-driven position updates.
    ///   - Release: local rigidbody returns to kinematic, server switches its body
    ///     back to Dynamic with the last known linear velocity.
    /// </summary>
    public partial class NetworkObject
    {
        // =====================================================================
        // Grab / Release
        // =====================================================================

        public void Grab()
        {
            if (!grabbable) return;
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (IsClaimedLocally) return;
            if (IsClaimedByRemote) return;

            IsClaimedLocally = true;
            ClaimedByClient  = NetworkManager.Instance.ClientId;

            // Enable local physics so the grabber gets instant response.
            if (_hasRb)
                _rb.isKinematic = false;

            NetworkManager.Instance.SendClaim(LocalId);
            _lastSentPos = transform.position;
            _lastSentRot = transform.eulerAngles;
        }

        public void Release()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (!IsClaimedLocally) return;

            // Send a final position so the server's Rapier body matches our last frame
            // before switching back to Dynamic.
            NetworkManager.Instance.SendInteractionUpdate(this);

            IsClaimedLocally = false;
            ClaimedByClient  = 0;
            _lastReleaseTime = Time.time;

            if (_hasRb)
            {
                // Server is the authority again. Stop simulating locally and wait for
                // incoming server position updates.
                _rb.isKinematic = true;
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
            // A remote client is simulating this object; ours must stay kinematic
            // so server position updates aren't fought by our local rigidbody.
            if (_hasRb)
                _rb.isKinematic = true;
        }

        public void OnRemoteRelease()
        {
            ClaimedByClient = 0;
            // Server takes over again. Keep the rigidbody kinematic and let server
            // broadcasts drive the object.
            if (_hasRb)
                _rb.isKinematic = true;
        }

        /// <summary>
        /// Server refused our optimistic claim (e.g. someone else held the object
        /// when our claim arrived). Undo the local grab so server broadcasts take
        /// over again.
        /// </summary>
        public void OnClaimRejected()
        {
            if (!IsClaimedLocally) return;
            IsClaimedLocally = false;
            ClaimedByClient  = 0;
            _autoClaimedByPush = false;
            if (_hasRb)
                _rb.isKinematic = true;
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
    }
}
