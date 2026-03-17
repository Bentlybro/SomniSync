using UnityEngine;

namespace SomniumNetwork.Client
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
        public float interpolationSpeed = 10f;
        
        [Tooltip("Use smooth interpolation (Lerp) vs instant updates")]
        public bool useInterpolation = true;
        
        [Header("Debug")]
        public bool showDebugLogs = false;
        
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private Vector3 _targetScale;
        private bool _hasReceivedUpdate = false;
        
        private void Start()
        {
            _targetPosition = transform.position;
            _targetRotation = transform.rotation;
            _targetScale = transform.localScale;
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
                
                if (showDebugLogs)
                    Debug.Log($"[NetworkTransform] Position update: {position.Value}");
            }
            
            if (rotation.HasValue && syncRotation)
            {
                _targetRotation = Quaternion.Euler(rotation.Value);
                _hasReceivedUpdate = true;
                
                if (showDebugLogs)
                    Debug.Log($"[NetworkTransform] Rotation update: {rotation.Value}");
            }
        }
    }
}
