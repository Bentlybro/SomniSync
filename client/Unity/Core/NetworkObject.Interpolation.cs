using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Position/rotation interpolation for NetworkObject. Rotation is a quaternion
    /// throughout — Euler encoding caused visible gimbal-lock flicker on
    /// free-rotating objects at slow speeds.
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
                return;
            }

            float duration = AdaptiveDuration(baseDuration);
            _lastUpdateReceivedTime = Time.time;
            _fromPos = transform.position;
            _fromRot = transform.rotation;
            _toPos   = pos;
            _toRot   = rot;
            if (syncScale && scale.HasValue) { _fromScale = transform.localScale; _toScale = scale.Value; }
            _interpT        = 0f;
            _interpDuration = duration;
        }

        // =====================================================================
        // Receive — position + velocity update (0x0B)
        // Velocity is still on the wire but unused; the server's Rapier sim is
        // the physics truth, and client-side extrapolation punched bouncing
        // objects through the floor before bounce packets arrived.
        // =====================================================================

        public void ApplyUpdate(Vector3 pos, Quaternion rot, Vector3 velocity, Vector3? scale = null)
        {
            _ = velocity; // intentionally ignored
            ApplyUpdate(pos, rot, scale);
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

        /// <summary>
        /// Decide how long the next lerp should take. The server's per-client AOI
        /// sends objects at different rates depending on distance (every tick,
        /// every 3rd, every 10th). A fixed 50ms lerp on a mid-tier object means
        /// we glide smoothly for 50ms then sit still for 100ms — visually a big
        /// step every ~150ms. Matching the lerp to the measured inter-packet gap
        /// turns stepping back into continuous motion.
        ///
        /// Clamped between the configured smoothing floor and 0.6s so a one-off
        /// long gap (AOI re-entry after hiding behind terrain) doesn't hang the
        /// object mid-lerp forever.
        /// </summary>
        private float AdaptiveDuration(float baseDuration)
        {
            if (_lastUpdateReceivedTime < 0f)
                return baseDuration; // first update — no gap to measure

            float gap = Time.time - _lastUpdateReceivedTime;
            return Mathf.Clamp(gap, baseDuration, 0.6f);
        }
    }
}
