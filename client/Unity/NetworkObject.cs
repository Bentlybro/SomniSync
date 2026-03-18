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
        private bool _hasReceivedUpdate = false;
        private Vector3 _lastSentPosition;
        private Quaternion _lastSentRotation;
        private float _nextSendTime = 0f;
        
        // Snapshot interpolation — buffer positions for smooth playback
        private Vector3 _fromPosition;
        private Vector3 _toPosition;
        private Quaternion _fromRotation;
        private Quaternion _toRotation;
        private float _snapshotTime = 0f;    // Time when we received the latest snapshot
        private float _prevSnapshotTime = 0f; // Time when we received the previous snapshot
        private float _interpolationT = 0f;   // Current interpolation progress (0-1)
        
        private void Awake()
        {
            // Auto-detect rigidbody
            _rigidbody = GetComponent<Rigidbody>();
            _hasRigidbody = _rigidbody != null;
        }
        
        private void Start()
        {
            _fromPosition = transform.position;
            _toPosition = transform.position;
            _fromRotation = transform.rotation;
            _toRotation = transform.rotation;
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
                
                // Register as available for linking by batch_sync
                NetworkManager.Instance.RegisterUnlinkedObject(this);
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
            
            // Registration complete — no per-entity log to avoid spam with 200+ objects
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
                if (smoothing == SmoothingMode.None)
                {
                    if (syncPosition) transform.position = _toPosition;
                    if (syncRotation) transform.rotation = _toRotation;
                }
                else
                {
                    // Snapshot interpolation — smoothly move between last two known positions
                    float snapshotDuration = _snapshotTime - _prevSnapshotTime;
                    
                    if (snapshotDuration <= 0f)
                        snapshotDuration = 1f / 60f; // Fallback to 60Hz assumption
                    
                    // Add a small buffer multiplier to prevent overshooting
                    float bufferMultiplier = smoothing switch
                    {
                        SmoothingMode.Low => 1.0f,
                        SmoothingMode.Medium => 1.1f,
                        SmoothingMode.High => 1.3f,
                        _ => 1.1f
                    };
                    
                    _interpolationT += Time.deltaTime / (snapshotDuration * bufferMultiplier);
                    _interpolationT = Mathf.Clamp01(_interpolationT);
                    
                    if (syncPosition)
                        transform.position = Vector3.Lerp(_fromPosition, _toPosition, _interpolationT);
                    
                    if (syncRotation)
                        transform.rotation = Quaternion.Slerp(_fromRotation, _toRotation, _interpolationT);
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
            
            NetworkManager.Instance.QueueUpdate(this, transform.position, transform.eulerAngles);
            
            _lastSentPosition = transform.position;
            _lastSentRotation = transform.rotation;
            _nextSendTime = Time.time + (1f / syncRate);
        }
        
        /// <summary>
        /// Apply position/rotation from the server (called by NetworkManager).
        /// </summary>
        public void ApplyServerUpdate(Vector3? position, Vector3? rotation)
        {
            if (_isOwned)
                return;
            
            // Shift current target to "from" (what we're interpolating FROM)
            _fromPosition = _hasReceivedUpdate ? _toPosition : transform.position;
            _fromRotation = _hasReceivedUpdate ? _toRotation : transform.rotation;
            _prevSnapshotTime = _snapshotTime;
            _snapshotTime = Time.time;
            _interpolationT = 0f;
            
            if (position.HasValue && syncPosition)
                _toPosition = position.Value;
            
            if (rotation.HasValue && syncRotation)
                _toRotation = Quaternion.Euler(rotation.Value);
            
            _hasReceivedUpdate = true;
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
