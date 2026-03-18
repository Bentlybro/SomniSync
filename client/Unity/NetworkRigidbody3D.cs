using UnityEngine;

namespace Bentlybro.CustomNetworking
{
    /// <summary>
    /// NetworkRigidbody3D - Syncs Unity Rigidbody physics across network.
    /// Mirrors Photon Fusion's NetworkRigidbody3D behavior.
    /// Add this component alongside Unity's Rigidbody component.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class NetworkRigidbody3D : MonoBehaviour
    {
        [Header("Network Settings")]
        [Tooltip("Which client has authority to modify this object (0 = server, 1+ = client ID)")]
        public int stateAuthority = 0; // 0 = server authoritative
        
        [Header("Sync Options")]
        [Tooltip("Synchronize scale changes")]
        public bool syncScale = false;
        
        [Tooltip("Synchronize parent changes")]
        public bool syncParent = false;
        
        [Header("Interpolation")]
        [Tooltip("What to interpolate towards")]
        public InterpolationTarget interpolationTarget = InterpolationTarget.Transform;
        
        [Tooltip("Use precise rotation (quaternion) instead of euler angles")]
        public bool usePreciseRotation = false;
        
        [Header("Performance")]
        [Tooltip("Use render sleep threshold (don't send updates when barely moving)")]
        public bool useRenderSleepThreshold = true;
        
        [Tooltip("Velocity threshold for sleep (magnitude)")]
        public float renderSleepThreshold = 0.01f;
        
        private Rigidbody _rigidbody;
        private NetworkObject _networkObject;
        private bool _hasAuthority = false;
        
        // Interpolation state for non-authority clients
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private Vector3 _targetVelocity;
        private Vector3 _targetAngularVelocity;
        
        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _networkObject = GetComponent<NetworkObject>();
            
            if (_networkObject == null)
            {
                Debug.LogError($"[NetworkRigidbody3D] NetworkObject component required on {gameObject.name}");
                enabled = false;
                return;
            }
        }
        
        private void Start()
        {
            // Determine if we have authority
            UpdateAuthority();
            
            // Set up rigidbody based on authority
            if (!_hasAuthority)
            {
                // Non-authority: kinematic, we'll move it manually
                _rigidbody.isKinematic = true;
            }
        }
        
        private void FixedUpdate()
        {
            if (_hasAuthority)
            {
                // Authority: normal physics, send state to network
                SendPhysicsState();
            }
            else
            {
                // Non-authority: interpolate to received state
                InterpolateToTarget();
            }
        }
        
        private void UpdateAuthority()
        {
            // TODO: Get actual authority from NetworkManager
            // For now: stateAuthority == 0 means server controls (we don't have authority)
            // stateAuthority > 0 means a client controls it
            _hasAuthority = (stateAuthority > 0); // Simplified for now
        }
        
        private void SendPhysicsState()
        {
            // Check sleep threshold
            if (useRenderSleepThreshold)
            {
                float velocity = _rigidbody.velocity.magnitude + _rigidbody.angularVelocity.magnitude;
                if (velocity < renderSleepThreshold && _rigidbody.IsSleeping())
                {
                    return; // Don't send updates when sleeping
                }
            }
            
            // TODO: Send rigidbody state to NetworkManager
            // Position, rotation, velocity, angularVelocity, sleeping state
        }
        
        private void InterpolateToTarget()
        {
            if (interpolationTarget == InterpolationTarget.Transform)
            {
                // Interpolate transform
                _rigidbody.MovePosition(Vector3.Lerp(
                    _rigidbody.position,
                    _targetPosition,
                    Time.fixedDeltaTime * 10f
                ));
                
                _rigidbody.MoveRotation(Quaternion.Lerp(
                    _rigidbody.rotation,
                    _targetRotation,
                    Time.fixedDeltaTime * 10f
                ));
            }
            else
            {
                // Interpolate rigidbody (includes velocity)
                _rigidbody.velocity = Vector3.Lerp(
                    _rigidbody.velocity,
                    _targetVelocity,
                    Time.fixedDeltaTime * 10f
                );
                
                _rigidbody.angularVelocity = Vector3.Lerp(
                    _rigidbody.angularVelocity,
                    _targetAngularVelocity,
                    Time.fixedDeltaTime * 10f
                );
            }
        }
        
        /// <summary>
        /// Apply network state (called by NetworkManager when update received)
        /// </summary>
        public void ApplyNetworkState(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            if (_hasAuthority)
                return; // Ignore updates if we have authority
            
            _targetPosition = position;
            _targetRotation = rotation;
            _targetVelocity = velocity;
            _targetAngularVelocity = angularVelocity;
        }
        
        /// <summary>
        /// Request state authority (for grabbing/interacting with objects)
        /// </summary>
        public void RequestStateAuthority()
        {
            // TODO: Send authority request to server
            Debug.Log($"[NetworkRigidbody3D] Requesting state authority for {gameObject.name}");
        }
        
        /// <summary>
        /// Grant state authority (called by NetworkManager when approved)
        /// </summary>
        public void GrantStateAuthority()
        {
            _hasAuthority = true;
            _rigidbody.isKinematic = false;
            Debug.Log($"[NetworkRigidbody3D] Authority granted for {gameObject.name}");
        }
        
        /// <summary>
        /// Revoke state authority (someone else took it)
        /// </summary>
        public void RevokeStateAuthority()
        {
            _hasAuthority = false;
            _rigidbody.isKinematic = true;
            Debug.Log($"[NetworkRigidbody3D] Authority revoked for {gameObject.name}");
        }
    }
    
    public enum InterpolationTarget
    {
        Transform,  // Interpolate position/rotation only
        Rigidbody   // Interpolate velocity (more accurate for physics)
    }
}
