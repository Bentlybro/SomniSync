using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles position/rotation interpolation and dead reckoning for NetworkObject.
    /// </summary>
    public partial class NetworkObject
    {
        // =====================================================================
        // Receive — position update (no velocity, legacy 0x01 / interaction)
        // =====================================================================

        public void ApplyUpdate(Vector3 pos, Vector3 rot, Vector3? scale = null)
        {
            float baseDuration = GetSmoothingDuration();
            if (baseDuration <= 0f)
            {
                transform.position    = pos;
                transform.eulerAngles = rot;
                if (syncScale && scale.HasValue) transform.localScale = scale.Value;
                _lastUpdateReceivedTime = Time.time;
                _deadReckoning = false;
                return;
            }

            _lastUpdateReceivedTime = Time.time;
            _fromPos = transform.position;
            _fromRot = transform.eulerAngles;
            _toPos   = pos;
            _toRot   = rot;
            if (syncScale && scale.HasValue) { _fromScale = transform.localScale; _toScale = scale.Value; }
            _interpT        = 0f;
            // Fixed duration — variable duration causes objects to visually change speed
            // whenever a packet arrives late/early, which reads as jitter.
            _interpDuration = baseDuration;
            _deadReckoning  = false;
        }

        // =====================================================================
        // Receive — position + velocity update (0x0B, dead reckoning)
        // =====================================================================

        public void ApplyUpdate(Vector3 pos, Vector3 rot, Vector3 velocity, Vector3? scale = null)
        {
            _drVelocity    = velocity;
            _drBasePos     = pos;
            _drBaseTime    = Time.time;
            _deadReckoning = velocity.sqrMagnitude > 0.0001f;

            float baseDuration = GetSmoothingDuration();
            if (baseDuration <= 0f)
            {
                transform.position    = pos;
                transform.eulerAngles = rot;
                if (syncScale && scale.HasValue) transform.localScale = scale.Value;
                _lastUpdateReceivedTime = Time.time;
                return;
            }

            _lastUpdateReceivedTime = Time.time;
            _fromPos = transform.position;
            _fromRot = transform.eulerAngles;
            // Lerp to where dead reckoning will be when the lerp finishes.
            // At t=baseDuration after receipt, DR gives: pos + vel * baseDuration.
            // Setting _toPos to that value means the lerp lands exactly where DR
            // picks up — no position jump at the lerp→DR transition.
            // (The old comment about overshoot applied when _interpDuration varied
            // per packet; now it is fixed, so the concern no longer applies.)
            _toPos   = pos + velocity * baseDuration;
            _toRot   = rot;
            if (syncScale && scale.HasValue) { _fromScale = transform.localScale; _toScale = scale.Value; }
            _interpT        = 0f;
            _interpDuration = baseDuration; // fixed, never jitter from packet timing
        }

        // =====================================================================
        // Receive — snap (initial snapshot, no interpolation)
        // =====================================================================

        public void SnapTo(Vector3 pos, Vector3 rot, Vector3? scale = null)
        {
            transform.position    = pos;
            transform.eulerAngles = rot;
            _fromPos = _toPos = pos;
            _fromRot = _toRot = rot;
            if (syncScale && scale.HasValue)
            {
                transform.localScale = scale.Value;
                _fromScale = _toScale = scale.Value;
            }
            _interpT = 1f;
            _deadReckoning = false;
            _lastUpdateReceivedTime = -1f;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private float GetSmoothingDuration()
        {
            switch (smoothing)
            {
                case SmoothingMode.None:   return 0f;
                case SmoothingMode.Low:    return 0.033f;
                case SmoothingMode.Medium: return 0.05f;
                case SmoothingMode.High:   return 0.1f;
                default:                   return 0.05f;
            }
        }

        private static Vector3 LerpAngles(Vector3 from, Vector3 to, float t) => new Vector3(
            Mathf.LerpAngle(from.x, to.x, t),
            Mathf.LerpAngle(from.y, to.y, t),
            Mathf.LerpAngle(from.z, to.z, t));
    }
}
