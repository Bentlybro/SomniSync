using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Position/rotation interpolation and dead reckoning for NetworkObject.
    /// Rotation is a quaternion throughout — Euler encoding caused visible
    /// gimbal-lock flicker on free-rotating objects at slow speeds.
    /// </summary>
    public partial class NetworkObject
    {
        // =====================================================================
        // Receive — position update (no velocity, for 0x03 interaction relay)
        // =====================================================================

        public void ApplyUpdate(Vector3 pos, Quaternion rot, Vector3? scale = null)
        {
            float baseDuration = GetSmoothingDuration();
            if (baseDuration <= 0f)
            {
                transform.position = pos;
                transform.rotation = rot;
                if (syncScale && scale.HasValue) transform.localScale = scale.Value;
                _lastUpdateReceivedTime = Time.time;
                _deadReckoning = false;
                return;
            }

            _lastUpdateReceivedTime = Time.time;
            _fromPos = transform.position;
            _fromRot = transform.rotation;
            _toPos   = pos;
            _toRot   = rot;
            if (syncScale && scale.HasValue) { _fromScale = transform.localScale; _toScale = scale.Value; }
            _interpT        = 0f;
            _interpDuration = baseDuration;
            _deadReckoning  = false;
        }

        // =====================================================================
        // Receive — position + velocity update (0x0B, dead reckoning)
        // =====================================================================

        public void ApplyUpdate(Vector3 pos, Quaternion rot, Vector3 velocity, Vector3? scale = null)
        {
            _drVelocity    = velocity;
            _drBasePos     = pos;
            _drBaseTime    = Time.time;
            // Higher threshold than before — below this, DR toggling at low
            // speeds caused sub-cm wobble on slow-moving objects.
            _deadReckoning = velocity.sqrMagnitude > 0.01f;

            float baseDuration = GetSmoothingDuration();
            if (baseDuration <= 0f)
            {
                transform.position = pos;
                transform.rotation = rot;
                if (syncScale && scale.HasValue) transform.localScale = scale.Value;
                _lastUpdateReceivedTime = Time.time;
                return;
            }

            _lastUpdateReceivedTime = Time.time;
            _fromPos = transform.position;
            _fromRot = transform.rotation;
            // Lerp to where dead reckoning will be when the lerp finishes — the
            // lerp lands exactly where DR picks up, so no jump at the handoff.
            _toPos   = pos + velocity * baseDuration;
            _toRot   = rot;
            if (syncScale && scale.HasValue) { _fromScale = transform.localScale; _toScale = scale.Value; }
            _interpT        = 0f;
            _interpDuration = baseDuration;
        }

        // =====================================================================
        // Receive — snap (initial snapshot, no interpolation)
        // =====================================================================

        public void SnapTo(Vector3 pos, Quaternion rot, Vector3? scale = null)
        {
            transform.position = pos;
            transform.rotation = rot;
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
    }
}
