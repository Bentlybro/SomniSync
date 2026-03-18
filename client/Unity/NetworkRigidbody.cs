using UnityEngine;

namespace Bentlybro.CustomNetworking
{
    /// <summary>
    /// Syncs Rigidbody physics across network.
    /// For interactive objects (balls, throwables, etc.)
    /// Client simulates physics and sends updates to server.
    /// </summary>
    [RequireComponent(typeof(NetworkEntity))]
    [RequireComponent(typeof(Rigidbody))]
    public class NetworkRigidbody : MonoBehaviour
    {
        [Header("Sync Settings")]
        [Tooltip("Send position updates to server")]
        public bool syncPosition = true;
        
        [Tooltip("Send rotation updates to server")]
        public bool syncRotation = true;
        
        [Tooltip("Send velocity to other clients")]
        public bool syncVelocity = true;
        
        [Header("Update Rate")]
        [Tooltip("How often to send updates to server (seconds)")]
        [Range(0.05f, 1f)]
        public float updateInterval = 0.1f; // 10 Hz
        
        [Header("Ownership")]
        [Tooltip("Only the owner can move this object")]
        public bool requireOwnership = true;
        
        [Tooltip("This client owns this object (can modify physics)")]
        public bool isOwner = true; // Default: objects in scene are owned by whoever placed them
        
        [Header("Interpolation (for non-owners)")]
        [Tooltip("Smooth movement for non-owners")]
        public bool useInterpolation = true;
        
        [Tooltip("Interpolation speed")]
        [Range(1f, 30f)]
        public float interpolationSpeed = 10f;
        
        [Header("Debug")]
        public bool showDebugLogs = false;
        
        private Rigidbody _rigidbody;
        private NetworkEntity _networkEntity;
        private float _lastUpdateTime = 0f;
        
        // For non-owners: interpolation targets
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private Vector3 _targetVelocity;
        private bool _hasReceivedUpdate = false;
        
        private void Start()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _networkEntity = GetComponent<NetworkEntity>();
            
            _targetPosition = transform.position;
            _targetRotation = transform.rotation;
            
            if (!isOwner)
            {
                // Non-owners: make kinematic, we'll move it via interpolation
                _rigidbody.isKinematic = true;
            }
        }
        
        private void FixedUpdate()
        {
            if (isOwner)
            {
                // Owner: send updates to server
                if (Time.time >= _lastUpdateTime + updateInterval)
                {
                    _lastUpdateTime = Time.time;
                    SendPhysicsUpdate();
                }
            }
            else if (_hasReceivedUpdate && useInterpolation)
            {
                // Non-owner: interpolate to target state
                if (syncPosition)
                {
                    _rigidbody.MovePosition(Vector3.Lerp(
                        _rigidbody.position,
                        _targetPosition,
                        Time.fixedDeltaTime * interpolationSpeed
                    ));
                }
                
                if (syncRotation)
                {
                    _rigidbody.MoveRotation(Quaternion.Lerp(
                        _rigidbody.rotation,
                        _targetRotation,
                        Time.fixedDeltaTime * interpolationSpeed
                    ));
                }
            }
        }
        
        private void SendPhysicsUpdate()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;
            
            // TODO: Send physics update to server
            // Server will broadcast to other clients
            // For now, NetworkTransform will handle basic position sync
            
            if (showDebugLogs)
            {
                Debug.Log($"[NetworkRigidbody] Sending update: pos={transform.position}, vel={_rigidbody.velocity}");
            }
        }
        
        /// <summary>
        /// Called by NetworkManager when server update arrives
        /// </summary>
        public void ApplyServerUpdate(Vector3? position, Quaternion? rotation, Vector3? velocity)
        {
            if (isOwner)
                return; // Owners ignore server updates (they're the authority for their objects)
            
            _hasReceivedUpdate = true;
            
            if (position.HasValue && syncPosition)
            {
                _targetPosition = position.Value;
                
                if (!useInterpolation)
                {
                    _rigidbody.position = position.Value;
                }
            }
            
            if (rotation.HasValue && syncRotation)
            {
                _targetRotation = rotation.Value;
                
                if (!useInterpolation)
                {
                    _rigidbody.rotation = rotation.Value;
                }
            }
            
            if (velocity.HasValue && syncVelocity)
            {
                _targetVelocity = velocity.Value;
                // Velocity doesn't interpolate, just apply it
                if (!_rigidbody.isKinematic)
                {
                    _rigidbody.velocity = velocity.Value;
                }
            }
            
            if (showDebugLogs)
            {
                Debug.Log($"[NetworkRigidbody] Received update: pos={position}, vel={velocity}");
            }
        }
        
        /// <summary>
        /// Request ownership of this object (for multiplayer pickup/throw)
        /// </summary>
        public void RequestOwnership()
        {
            if (isOwner)
                return;
            
            // TODO: Send ownership request to server
            // Server decides if ownership transfer is allowed
            
            if (showDebugLogs)
            {
                Debug.Log($"[NetworkRigidbody] Requesting ownership of {_networkEntity.entityId}");
            }
        }
        
        /// <summary>
        /// Grant ownership (called by NetworkManager when server approves)
        /// </summary>
        public void GrantOwnership()
        {
            isOwner = true;
            _rigidbody.isKinematic = false;
            
            if (showDebugLogs)
            {
                Debug.Log($"[NetworkRigidbody] Ownership granted for {_networkEntity.entityId}");
            }
        }
        
        /// <summary>
        /// Revoke ownership (someone else took it)
        /// </summary>
        public void RevokeOwnership()
        {
            isOwner = false;
            _rigidbody.isKinematic = true;
            
            if (showDebugLogs)
            {
                Debug.Log($"[NetworkRigidbody] Ownership revoked for {_networkEntity.entityId}");
            }
        }
    }
}
