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
            _lastSentRot = transform.rotation;
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
                _fromRot = _toRot = transform.rotation;
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

        /// <summary>
        /// Called by NetworkManager on disconnect. Clears claim state so the
        /// reconnecting client doesn't think it still owns objects the server
        /// has long since returned to Dynamic.
        /// </summary>
        internal void ResetClaimStateOnDisconnect()
        {
            IsClaimedLocally   = false;
            ClaimedByClient    = 0;
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

        // Object-to-object collision cascading was removed intentionally.
        //
        // Previously, a locally-claimed object's OnCollisionEnter would fire a
        // grab on whatever it hit so the receiving object also simulated locally.
        // That made sense under relay/shared-authority physics, but under
        // server-authoritative physics it's actively harmful:
        //
        //   Player pushes A (locally claimed, dynamic) into B (kinematic, server-
        //   owned). A collides with B; Unity's solver resolves it with B still
        //   kinematic. OnCollisionEnter fires and flips B to dynamic mid-overlap.
        //   Unity's next step sees A and B penetrating and applies a massive
        //   separation impulse -> B launches at mach 1.
        //
        // Server-side Rapier already handles A-hits-B correctly: A's kinematic
        // body in Rapier follows the client's position updates, its motion
        // registers as a velocity, and B gets pushed naturally. Clients just
        // render the resulting 0x0B updates. No cascade needed.
    }
}
