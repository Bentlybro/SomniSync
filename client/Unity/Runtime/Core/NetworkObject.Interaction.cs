using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Claim (grab) and remote-claim notifications for NetworkObject.
    ///
    /// There are two player-object interaction models:
    ///
    ///   1. **Grab** — explicit user-code call (VR hand grip, interact button).
    ///      Client takes local physics authority, streams held-object updates
    ///      to the server, releases when user lets go. Used for carrying.
    ///
    ///   2. **Push** — passive collision from <see cref="PlayerPushDetector"/>.
    ///      Walking into a pushable object sends an impulse to the server;
    ///      Rapier applies it and broadcasts the resulting motion. No claim
    ///      involved, no client-side physics. Used for bumping things around.
    ///
    /// This file handles the grab path. Push is fully in PlayerPushDetector.
    /// </summary>
    public partial class NetworkObject
    {
        // =====================================================================
        // Grab / Release
        // =====================================================================

        public void Grab() => Grab(true);

        /// <summary>
        /// Claim this object for local control.
        /// </summary>
        /// <param name="enableLocalPhysics">
        /// If true (default), the Rigidbody becomes non-kinematic so Unity
        /// physics drives the object while held — right for player grabs of
        /// physics props. If false, the Rigidbody stays kinematic; the caller
        /// is expected to drive transform.position directly (AI scripts
        /// chasing a target, for example). Fighting Unity physics with manual
        /// transform assignment causes visible jitter and tumbling.
        /// </param>
        public void Grab(bool enableLocalPhysics)
        {
            if (!grabbable) return;
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (IsClaimedLocally) return;
            if (IsClaimedByRemote) return;

            IsClaimedLocally = true;
            ClaimedByClient  = NetworkManager.Instance.ClientId;
            NetworkManager.Instance.TrackLocalClaim(this);

            if (_hasRb && enableLocalPhysics)
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
            NetworkManager.Instance.UntrackLocalClaim(this);

            if (_hasRb)
            {
                _rb.isKinematic = true;
                _fromPos = _toPos = transform.position;
                _fromRot = _toRot = transform.rotation;
                _interpT = 1f;
            }

            // Local approximation of the authoritative release — lets
            // NetworkNPC re-anchor instantly without waiting ~RTT/2 for the
            // server's echo. When the echo arrives, OnRemoteRelease overwrites
            // these with the truly authoritative values (tiny correction —
            // ServerTime should already be NTP-sync'd).
            LastReleasePos = transform.position;
            LastReleaseRot = transform.rotation;
            LastReleaseServerMs = (ulong)(NetworkManager.Instance.ServerTime * 1000.0);

            NetworkManager.Instance.SendRelease(LocalId);
        }

        // =====================================================================
        // Remote claim / release notifications
        // =====================================================================

        public void OnRemoteClaim(ushort clientId)
        {
            ClaimedByClient = clientId;
            if (_hasRb)
                _rb.isKinematic = true;
        }

        /// <summary>
        /// Called when the server announces a release for this object.
        /// <paramref name="pos"/>, <paramref name="rot"/>, and
        /// <paramref name="serverMs"/> are the authoritative release state —
        /// used to snap all clients (including the original claimer) to an
        /// identical post-release pose, killing the per-client lerp-phase
        /// drift that was making NetworkNPC paths diverge after each chase.
        /// </summary>
        public void OnRemoteRelease(Vector3 pos, Quaternion rot, ulong serverMs)
        {
            // Reject older releases than what we've already seen. Matters
            // for late-joiner replay: the server sends us the stored
            // last-release for each object right after welcome, and that
            // replay can race with a live release broadcast that happened
            // between add_client and the replay. Without this guard the
            // older replay would overwrite the newer live release and
            // NetworkNPC would re-anchor its wander to stale state.
            // Zero serverMs is the "no authoritative pose" sentinel
            // (empty release); always accept it so reset paths still work.
            if (serverMs != 0 && serverMs < LastReleaseServerMs)
                return;

            ClaimedByClient = 0;

            if (_hasRb)
                _rb.isKinematic = true;

            // Hard-snap to server's release pose so any in-flight interp
            // doesn't continue writing transform.position after the claim ends.
            transform.position = pos;
            transform.rotation = rot;
            _fromPos = _toPos = pos;
            _fromRot = _toRot = rot;
            _interpT = 1f;

            LastReleasePos = pos;
            LastReleaseRot = rot;
            LastReleaseServerMs = serverMs;
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
            if (NetworkManager.Instance != null) NetworkManager.Instance.UntrackLocalClaim(this);
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
            IsClaimedLocally = false;
            ClaimedByClient  = 0;
            if (NetworkManager.Instance != null) NetworkManager.Instance.UntrackLocalClaim(this);
            if (_hasRb)
                _rb.isKinematic = true;
        }
    }
}
