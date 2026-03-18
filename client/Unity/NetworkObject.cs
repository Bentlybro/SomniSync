using System.Collections;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// NetworkObject — The only component needed to network a GameObject.
    /// 
    /// For world makers: Just add this component to any object. It will sync
    /// position, rotation, and physics across all clients automatically.
    /// 
    /// For game developers: Configure sync settings, use ownership transfer,
    /// and extend with network variables as needed.
    /// </summary>
    public enum SmoothingMode
    {
        [Tooltip("No interpolation — snaps to server position instantly")]
        None,
        [Tooltip("Minimal smoothing — fast response, may jitter")]
        Low,
        [Tooltip("Balanced smoothing — good for most objects")]
        Medium,
        [Tooltip("Heavy smoothing — very fluid but adds perceived latency")]
        High
    }
    
    public class NetworkObject : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Unique network ID (auto-generated if empty)")]
        public string entityId = "";
        
        [Tooltip("Entity type for prefab matching (e.g., 'sphere', 'cube', 'npc')")]
        public string entityType = "entity";
        
        [Header("Sync Settings")]
        [Tooltip("How many times per second to send updates (should match server tick rate)")]
        [Range(1f, 120f)]
        public float syncRate = 60f;
        
        [Tooltip("How smoothly remote objects move to their target position")]
        public SmoothingMode smoothing = SmoothingMode.Medium;
        
        [Tooltip("Minimum position change to trigger an update (meters)")]
        public float positionThreshold = 0.01f;
        
        [Tooltip("Minimum rotation change to trigger an update (degrees)")]
        public float rotationThreshold = 1f;
        
        [Header("Options")]
        [Tooltip("Sync position")]
        public bool syncPosition = true;
        
        [Tooltip("Sync rotation")]
        public bool syncRotation = true;
        
        [Tooltip("Register with server on Start()")]
        public bool spawnOnStart = true;
        
        [Tooltip("Destroy when disconnected")]
        public bool destroyOnDisconnect = true;
        
        // Runtime state (read-only in inspector)
        [Header("Runtime (Read Only)")]
        [SerializeField] private bool _isOwned = true;
        [SerializeField] private bool _isMaster = false;
        [SerializeField] private bool _isSpawned = false;
        
        public bool IsOwned => _isOwned;
        public bool IsMaster => _isMaster;
        public bool IsSpawned => _isSpawned;
        
        // Internal state
        private Rigidbody _rigidbody;
        private bool _hasRigidbody = false;
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private bool _hasReceivedUpdate = false;
        private Vector3 _lastSentPosition;
        private Quaternion _lastSentRotation;
        private float _nextSendTime = 0f;
        
        private void Awake()
        {
            // Auto-detect rigidbody
            _rigidbody = GetComponent<Rigidbody>();
            _hasRigidbody = _rigidbody != null;
        }
        
        private void Start()
        {
            _targetPosition = transform.position;
            _targetRotation = transform.rotation;
            _lastSentPosition = transform.position;
            _lastSentRotation = transform.rotation;
            
            if (spawnOnStart)
            {
                StartCoroutine(DelayedRegister());
            }
        }
        
        private IEnumerator DelayedRegister()
        {
            // Wait one frame for NetworkManager to initialize
            yield return null;
            
            // Wait for client ID from server
            while (NetworkManager.Instance != null && string.IsNullOrEmpty(NetworkManager.Instance.ClientId))
                yield return null;
            
            if (NetworkManager.Instance == null)
            {
                Debug.LogError($"[NetworkObject] NetworkManager not found! Cannot register {gameObject.name}");
                yield break;
            }
            
            _isMaster = NetworkManager.Instance.IsMasterClient;
            
            // Only master client registers scene objects with server
            if (!_isMaster)
            {
                _isOwned = false;
                SetPhysicsState(false); // Spectators don't simulate physics
                yield break;
            }
            
            Register();
        }
        
        /// <summary>
        /// Register this object with the network.
        /// </summary>
        public void Register()
        {
            if (string.IsNullOrEmpty(entityId))
                entityId = System.Guid.NewGuid().ToString();
            
            if (NetworkManager.Instance == null)
                return;
            
            NetworkManager.Instance.RegisterNetworkObject(this);
            NetworkManager.Instance.SpawnNetworkObjectOnServer(this);
            
            _isSpawned = true;
            
            Debug.Log($"[NetworkObject] Registered: {gameObject.name} (ID: {entityId}, owned: {_isOwned})");
        }
        
        private void FixedUpdate()
        {
            if (!_isSpawned && !_hasReceivedUpdate)
                return;
            
            if (_isOwned)
            {
                // We own this — send updates when moved
                SendUpdates();
            }
        }
        
        private void Update()
        {
            if (!_isOwned && _hasReceivedUpdate)
            {
                // Remote object — interpolate to target
                if (smoothing == SmoothingMode.None)
                {
                    // Instant snap
                    if (syncPosition) transform.position = _targetPosition;
                    if (syncRotation) transform.rotation = _targetRotation;
                }
                else
                {
                    float speed = smoothing switch
                    {
                        SmoothingMode.Low => 35f,
                        SmoothingMode.Medium => 20f,
                        SmoothingMode.High => 8f,
                        _ => 20f
                    };
                    
                    float t = Time.deltaTime * speed;
                    
                    if (syncPosition)
                        transform.position = Vector3.Lerp(transform.position, _targetPosition, t);
                    
                    if (syncRotation)
                        transform.rotation = Quaternion.Lerp(transform.rotation, _targetRotation, t);
                }
            }
        }
        
        private void SendUpdates()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;
            
            if (Time.time < _nextSendTime)
                return;
            
            // Delta check — only send if actually moved
            float posDelta = Vector3.Distance(transform.position, _lastSentPosition);
            float rotDelta = Quaternion.Angle(transform.rotation, _lastSentRotation);
            
            if (posDelta < positionThreshold && rotDelta < rotationThreshold)
                return;
            
            NetworkManager.Instance.SendPhysicsUpdate(this, transform.position, transform.eulerAngles);
            
            _lastSentPosition = transform.position;
            _lastSentRotation = transform.rotation;
            _nextSendTime = Time.time + (1f / syncRate);
        }
        
        /// <summary>
        /// Apply position/rotation from the server (called by NetworkManager).
        /// </summary>
        public void ApplyServerUpdate(Vector3? position, Vector3? rotation)
        {
            // Don't apply server updates to owned objects
            if (_isOwned)
                return;
            
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
        
        /// <summary>
        /// Take ownership of this object (host migration or interaction).
        /// </summary>
        public void TakeOwnership()
        {
            _isOwned = true;
            _isMaster = true;
            SetPhysicsState(true);
            Debug.Log($"[NetworkObject] Took ownership of {gameObject.name}");
        }
        
        /// <summary>
        /// Release ownership (another client is taking over).
        /// </summary>
        public void ReleaseOwnership()
        {
            _isOwned = false;
            SetPhysicsState(false);
            Debug.Log($"[NetworkObject] Released ownership of {gameObject.name}");
        }
        
        /// <summary>
        /// Enable or disable physics simulation on this object.
        /// </summary>
        private void SetPhysicsState(bool enablePhysics)
        {
            if (_hasRigidbody)
            {
                _rigidbody.isKinematic = !enablePhysics;
            }
        }
        
        private void OnDestroy()
        {
            if (_isSpawned && NetworkManager.Instance != null)
            {
                NetworkManager.Instance.UnregisterNetworkObject(this);
            }
        }
    }
}
