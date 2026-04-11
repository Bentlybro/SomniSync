using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Syncs Rigidbody velocity and angular velocity across clients.
    /// Attach to any NetworkObject with a Rigidbody.
    /// 
    /// When the authority (master or claimed owner) detects velocity changes,
    /// it sends updates. Receivers apply velocity for smooth extrapolation
    /// between position updates — objects coast naturally instead of stuttering.
    /// 
    /// Great for: thrown objects, vehicles, physics puzzles, explosions.
    /// 
    /// Protocol 0x08: [0x08][u16 obj_id][half vx][half vy][half vz][half avx][half avy][half avz]
    /// = 15 bytes per update
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkRigidbody : MonoBehaviour
    {
        [Header("Sync Settings")]
        [Tooltip("How often to check for velocity changes (Hz)")]
        public float syncRate = 10f;

        [Tooltip("Minimum velocity change to trigger sync (m/s)")]
        public float velocityThreshold = 0.05f;

        [Tooltip("Minimum angular velocity change to trigger sync (rad/s)")]
        public float angularVelocityThreshold = 0.1f;

        [Tooltip("Apply received velocity to rigidbody (enables extrapolation between position updates)")]
        public bool applyReceivedVelocity = true;

        private NetworkObject _netObj;
        private NetworkManager _manager;
        private Rigidbody _rb;

        private float _syncTimer;
        private Vector3 _lastSentVelocity;
        private Vector3 _lastSentAngularVelocity;
        private bool _sleeping; // Skip FixedUpdate entirely when at rest
        private float _sleepTimer;

        // Protocol
        private const byte MSG_RIGIDBODY = 0x08;
        private const int MSG_SIZE = 15; // 1 + 2 + 6 + 6

        private void Awake()
        {
            _netObj = GetComponent<NetworkObject>();
            _rb = GetComponent<Rigidbody>();

            if (_rb == null)
            {
                Debug.LogWarning($"[NetworkRigidbody] No Rigidbody on {name}, disabling");
                enabled = false;
            }
        }

        private void Start()
        {
            _manager = NetworkManager.Instance;
        }

        private void FixedUpdate()
        {
            if (_manager == null || !_manager.IsConnected) return;
            if (!_netObj.HasAuthority()) return;

            // Sleep optimization: skip entirely when object is at rest
            if (_sleeping)
            {
                // Only check for wake-up every 10 FixedUpdates (~5Hz)
                _sleepTimer += Time.fixedDeltaTime;
                if (_sleepTimer < 0.2f) return;
                _sleepTimer = 0f;

                if (_rb.linearVelocity.sqrMagnitude > velocityThreshold * velocityThreshold ||
                    _rb.angularVelocity.sqrMagnitude > angularVelocityThreshold * angularVelocityThreshold)
                {
                    _sleeping = false;
                }
                return;
            }

            SendCheck();
        }

        private void SendCheck()
        {
            _syncTimer += Time.fixedDeltaTime;
            if (_syncTimer < 1f / syncRate) return;
            _syncTimer = 0f;

            var vel = _rb.linearVelocity;
            var angVel = _rb.angularVelocity;

            // Use sqrMagnitude comparisons to avoid sqrt on the hot path
            float velThreshSq = velocityThreshold * velocityThreshold;
            float angThreshSq = angularVelocityThreshold * angularVelocityThreshold;
            bool velChanged = (vel - _lastSentVelocity).sqrMagnitude > velThreshSq;
            bool angChanged = (angVel - _lastSentAngularVelocity).sqrMagnitude > angThreshSq;

            bool wasStopped = _lastSentVelocity.sqrMagnitude < 0.001f && _lastSentAngularVelocity.sqrMagnitude < 0.001f;
            bool isStopped = vel.sqrMagnitude < 0.001f && angVel.sqrMagnitude < 0.001f;

            if (!velChanged && !angChanged) return;

            // Object just stopped — send final zero velocity then sleep
            if (isStopped && !wasStopped)
            {
                _lastSentVelocity = Vector3.zero;
                _lastSentAngularVelocity = Vector3.zero;
                SendVelocity(Vector3.zero, Vector3.zero);
                _sleeping = true;
                return;
            }

            if (wasStopped && isStopped) return;

            _lastSentVelocity = vel;
            _lastSentAngularVelocity = angVel;
            SendVelocity(vel, angVel);
        }

        private void SendVelocity(Vector3 vel, Vector3 angVel)
        {
            // 15 bytes — too small for pool overhead to matter; allocate directly.
            byte[] msg = new byte[MSG_SIZE];
            msg[0] = MSG_RIGIDBODY;
            Protocol.WriteU16(msg, 1, _netObj.LocalId);
            Protocol.WriteHalf(msg, 3, vel.x);
            Protocol.WriteHalf(msg, 5, vel.y);
            Protocol.WriteHalf(msg, 7, vel.z);
            Protocol.WriteHalf(msg, 9, angVel.x);
            Protocol.WriteHalf(msg, 11, angVel.y);
            Protocol.WriteHalf(msg, 13, angVel.z);
            _manager.SendBinaryPublic(msg);
        }

        /// <summary>Called by NetworkManager when a 0x08 message arrives.</summary>
        public void HandleRigidbodyUpdate(byte[] data, int offset)
        {
            if (!applyReceivedVelocity || _rb == null) return;
            if (_netObj.HasAuthority()) return; // Don't apply to our own

            float vx = Protocol.ReadHalf(data, offset); offset += 2;
            float vy = Protocol.ReadHalf(data, offset); offset += 2;
            float vz = Protocol.ReadHalf(data, offset); offset += 2;
            float avx = Protocol.ReadHalf(data, offset); offset += 2;
            float avy = Protocol.ReadHalf(data, offset); offset += 2;
            float avz = Protocol.ReadHalf(data, offset);

            // Only apply if rigidbody is kinematic (spectator mode)
            if (_rb.isKinematic) return;

            _rb.linearVelocity = new Vector3(vx, vy, vz);
            _rb.angularVelocity = new Vector3(avx, avy, avz);
        }
    }
}
