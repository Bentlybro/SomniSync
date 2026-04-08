using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles grab/release, remote claim notifications, and push-to-claim for NetworkObject.
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

            IsClaimedLocally = true;
            ClaimedByClient  = NetworkManager.Instance.ClientId;

            if (!HasStateAuthority && _hasRb)
                _rb.isKinematic = false; // enable physics for local movement

            NetworkManager.Instance.SendClaim(LocalId);
            _lastSentPos = transform.position;
            _lastSentRot = transform.eulerAngles;
        }

        public void Release()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (!IsClaimedLocally) return;

            NetworkManager.Instance.SendInteractionUpdate(this);

            IsClaimedLocally = false;
            ClaimedByClient  = 0;
            _lastReleaseTime = Time.time;

            if (!HasStateAuthority && _hasRb)
            {
                // Don't zero velocity here — the authority client will resume physics
                // from the position sent by SendInteractionUpdate and apply dead-reckoning.
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
            if (HasStateAuthority && _hasRb)
                _rb.isKinematic = true; // pause our physics while remote holds it
        }

        public void OnRemoteRelease()
        {
            ClaimedByClient = 0;
            if (HasStateAuthority && _hasRb)
            {
                _rb.isKinematic     = false;
                _rb.linearVelocity  = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
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
