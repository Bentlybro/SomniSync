using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Syncs position and rotation from server.
    /// Smoothly interpolates to target values.
    /// </summary>
    [RequireComponent(typeof(NetworkEntity))]
    public class NetworkTransform : MonoBehaviour
    {
        [Header("Sync Settings")]
        [Tooltip("Synchronize position")]
        public bool syncPosition = true;
        
        [Tooltip("Synchronize rotation")]
        public bool syncRotation = true;
        
        [Tooltip("Synchronize scale")]
        public bool syncScale = false;
        
        [Header("Interpolation")]
        [Tooltip("Interpolation speed (higher = snappier, lower = smoother)")]
        [Range(1f, 50f)]
        public float interpolationSpeed = 30f;
        
        [Tooltip("Use smooth interpolation (Lerp) vs instant updates")]
        public bool useInterpolation = true;
        
        [Header("Debug")]
        public bool showDebugLogs = false;
        
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private Vector3 _targetScale;
        private bool _hasReceivedUpdate = false;
        
        private NetworkEntity _networkEntity;
        private Vector3 _lastSentPosition;
        private Quaternion _lastSentRotation;
        private float _updateInterval = 0.05f; // 20 Hz update rate
        private float _nextUpdateTime = 0f;
        
        private void Start()
        {
            _targetPosition = transform.position;
            _targetRotation = transform.rotation;
            _targetScale = transform.localScale;
            
            _networkEntity = GetComponent<NetworkEntity>();
            if (_networkEntity == null)
            {
                Debug.LogError($"[NetworkTransform] NetworkEntity component required on {gameObject.name}");
                enabled = false;
                return;
            }
            
            _lastSentPosition = transform.position;
            _lastSentRotation = transform.rotation;
        }
        
        private void FixedUpdate()
        {
            // If we own this entity, send physics updates to server
            if (_networkEntity != null && _networkEntity.isOwned && NetworkManager.Instance != null)
            {
                if (Time.time >= _nextUpdateTime)
                {
                    // Check if moved significantly (delta compression)
                    float positionDelta = Vector3.Distance(transform.position, _lastSentPosition);
                    float rotationDelta = Quaternion.Angle(transform.rotation, _lastSentRotation);
                    
                    if (positionDelta > 0.01f || rotationDelta > 1f) // 1cm or 1 degree threshold
                    {
                        NetworkManager.Instance.SendPhysicsUpdate(
                            _networkEntity,
                            transform.position,
                            transform.eulerAngles
                        );
                        
                        _lastSentPosition = transform.position;
                        _lastSentRotation = transform.rotation;
                    }
                    
                    _nextUpdateTime = Time.time + _updateInterval;
                }
            }
            // Removed "Not owned" spam - we know spectators don't send updates
        }
        
        private void Update()
        {
            if (!_hasReceivedUpdate)
                return;
            
            if (useInterpolation)
            {
                float t = Time.deltaTime * interpolationSpeed;
                
                if (syncPosition)
                {
                    transform.position = Vector3.Lerp(transform.position, _targetPosition, t);
                }
                
                if (syncRotation)
                {
                    transform.rotation = Quaternion.Lerp(transform.rotation, _targetRotation, t);
                }
                
                if (syncScale)
                {
                    transform.localScale = Vector3.Lerp(transform.localScale, _targetScale, t);
                }
            }
            else
            {
                // Instant update (no interpolation)
                if (syncPosition)
                    transform.position = _targetPosition;
                
                if (syncRotation)
                    transform.rotation = _targetRotation;
                
                if (syncScale)
                    transform.localScale = _targetScale;
            }
        }
        
        /// <summary>
        /// Called by NetworkManager when server update arrives
        /// </summary>
        public void ApplyServerUpdate(Vector3? position, Vector3? rotation)
        {
            if (position.HasValue && syncPosition)
            {
                _targetPosition = position.Value;
                _hasReceivedUpdate = true;
            }
            
            if (rotation.HasValue && syncRotation)
            {
                _targetRotation = Quaternion.Euler(rotation.Value);
                _hasReceivedUpdate = true;
            }
        }
    }
}
