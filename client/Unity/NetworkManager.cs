using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Main network manager. Handles WebSocket connection and entity synchronization.
    /// Add this component to a GameObject in your scene.
    /// </summary>
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }
        
        [Header("Connection")]
        [Tooltip("WebSocket server URL (use localhost for same machine, or server IP for remote)")]
        public string serverUrl = "ws://localhost:9090/";
        
        [Tooltip("Auto-connect on Start()")]
        public bool autoConnect = true;
        
        [Header("Entity Management")]
        [Tooltip("Default prefab if type not found in registry")]
        public GameObject defaultPrefab;
        
        [Tooltip("Map entity types to prefabs (e.g., 'zombie' -> ZombiePrefab)")]
        public EntityPrefabMapping[] entityPrefabs;
        
        [Header("Reconnection")]
        [Tooltip("Auto-reconnect when disconnected")]
        public bool autoReconnect = true;
        
        [Tooltip("Delay between reconnect attempts (seconds)")]
        public float reconnectDelay = 3f;
        
        [Tooltip("Maximum reconnect attempts (0 = unlimited)")]
        public int maxReconnectAttempts = 0;
        
        [Header("Debug")]
        public bool showDebugLogs = false;
        
        [Tooltip("Optional TextMeshPro to show connection status")]
        public TMPro.TextMeshPro connectionStatusText;
        
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected = false;
        private bool _isReceiving = false;
        private string _clientId = null; // Our client ID from server
        private bool _isMasterClient = false; // Are we the master client (first to connect)?
        private bool _hasLoggedUpdateJson = false; // Debug flag to log first update JSON
        private int _reconnectAttempts = 0;
        private bool _isReconnecting = false;
        private bool _hasLoggedSendError = false; // Only log send errors once per disconnect
        private System.IO.MemoryStream _binaryBuffer; // For multi-frame binary messages
        
        public bool IsConnected => _isConnected;
        public int EntityCount => _spawnedEntities.Count;
        public string ClientId => _clientId;
        public bool IsMasterClient => _isMasterClient;
        
        // Entity tracking
        private Dictionary<string, NetworkEntity> _entities = new Dictionary<string, NetworkEntity>();
        private Dictionary<string, GameObject> _spawnedEntities = new Dictionary<string, GameObject>();
        private List<NetworkEntity> _pendingSpawns = new List<NetworkEntity>();
        
        // NetworkObject tracking (new unified system)
        private Dictionary<string, NetworkObject> _networkObjects = new Dictionary<string, NetworkObject>();
        private List<NetworkObject> _pendingNetworkObjects = new List<NetworkObject>();
        
        // Update batching — collects updates and sends as one message per tick
        private List<EntityUpdate> _pendingUpdates = new List<EntityUpdate>();
        private float _nextBatchTime = 0f;
        private float _batchInterval = 1f / 60f; // Match server tick rate
        
        // Spawn batching — collect all spawns and send as one message
        private List<NetworkMessage> _pendingBatchSpawns = new List<NetworkMessage>();
        private float _nextSpawnFlushTime = 0f;
        
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        
        private void Start()
        {
            if (autoConnect)
            {
                StartCoroutine(ConnectAsync());
            }
        }
        
        private void Update()
        {
            // Update status display every frame
            if (connectionStatusText != null && _isConnected)
            {
                UpdateConnectionStatus("CONNECTED", UnityEngine.Color.green);
            }
            
            // Flush batched updates
            if (_isConnected && _pendingUpdates.Count > 0 && Time.time >= _nextBatchTime)
            {
                FlushBatchedUpdates();
                _nextBatchTime = Time.time + _batchInterval;
            }
            
            // Flush batched spawns (wait one frame to collect all scene objects)
            if (_isConnected && _pendingBatchSpawns.Count > 0 && Time.time >= _nextSpawnFlushTime)
            {
                FlushBatchedSpawns();
            }
        }
        
        private void FlushBatchedSpawns()
        {
            if (_pendingBatchSpawns.Count == 0)
                return;
            
            int count = _pendingBatchSpawns.Count;
            
            // Send all queued spawns
            foreach (var spawn in _pendingBatchSpawns)
            {
                StartCoroutine(SendMessageAsync(spawn));
            }
            
            Debug.Log($"[NetworkManager] Registered {count} entities with server");
            _pendingBatchSpawns.Clear();
        }
        
        private void FlushBatchedUpdates()
        {
            if (_pendingUpdates.Count == 0)
                return;
            
            // Send all updates as ONE message with the Updates array
            var message = new NetworkMessage
            {
                Type = "client_batch_update",
                Updates = _pendingUpdates.ToArray()
            };
            
            StartCoroutine(SendMessageAsync(message));
            _pendingUpdates.Clear();
        }
        
        public void Connect()
        {
            if (!_isConnected)
            {
                StartCoroutine(ConnectAsync());
            }
        }
        
        public void Disconnect()
        {
            if (_isConnected)
            {
                StartCoroutine(DisconnectAsync());
            }
        }
        
        private IEnumerator ConnectAsync()
        {
            if (_isConnected)
            {
                if (showDebugLogs)
                    Debug.LogWarning("[NetworkManager] Already connected");
                yield break;
            }
            
            _webSocket = new ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();
            
            if (showDebugLogs)
                Debug.Log($"[NetworkManager] Connecting to {serverUrl}...");
            
            Uri serverUri = new Uri(serverUrl);
            Task connectTask = _webSocket.ConnectAsync(serverUri, _cancellationTokenSource.Token);
            
            // Wait for connection
            float timeout = 5f;
            float elapsed = 0f;
            while (!connectTask.IsCompleted && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            if (connectTask.IsCompleted && !connectTask.IsFaulted)
            {
                _isConnected = true;
                _hasLoggedSendError = false;
                if (showDebugLogs)
                    Debug.Log("[NetworkManager] Connected!");
                
                UpdateConnectionStatus("CONNECTED", UnityEngine.Color.green);
                
                // Spawn all registered entities that were waiting
                SpawnPendingEntities();
                
                // Start receiving messages
                StartCoroutine(ReceiveMessagesAsync());
            }
            else
            {
                if (showDebugLogs)
                    Debug.LogError($"[NetworkManager] Connection failed: {connectTask.Exception?.Message}");
                
                UpdateConnectionStatus("DISCONNECTED", UnityEngine.Color.red);
            }
        }
        
        private void UpdateConnectionStatus(string status, UnityEngine.Color color)
        {
            if (connectionStatusText != null)
            {
                int totalEntities = _entities.Count + _spawnedEntities.Count;
                connectionStatusText.text = $"Server: {status}\nLocal: {_entities.Count} | Remote: {_spawnedEntities.Count} | Total: {totalEntities}";
                connectionStatusText.color = color;
            }
        }
        
        private IEnumerator DisconnectAsync()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                _cancellationTokenSource?.Cancel();
                
                Task closeTask = _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client closing",
                    CancellationToken.None
                );
                
                yield return new WaitUntil(() => closeTask.IsCompleted);
            }
            
            _isConnected = false;
            _isReceiving = false;
            
            _webSocket?.Dispose();
            _cancellationTokenSource?.Dispose();
            
            if (showDebugLogs)
                Debug.Log("[NetworkManager] Disconnected");
        }
        
        private IEnumerator ReceiveMessagesAsync()
        {
            if (_isReceiving)
                yield break;
            
            _isReceiving = true;
            byte[] buffer = new byte[131072]; // 128KB buffer (supports ~3000 entities in binary)
            StringBuilder messageBuilder = new StringBuilder();
            
            while (_isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                Task<WebSocketReceiveResult> receiveTask = _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _cancellationTokenSource.Token
                );
                
                yield return new WaitUntil(() => receiveTask.IsCompleted);
                
                if (receiveTask.IsFaulted)
                {
                    if (showDebugLogs)
                        // Connection dropped — don't spam, auto-reconnect will handle it
                    break;
                }
                
                WebSocketReceiveResult result = receiveTask.Result;
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (showDebugLogs)
                        Debug.Log("[NetworkManager] Server closed connection");
                    _isConnected = false;
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Binary update — fast path, no JSON parsing
                    if (result.EndOfMessage)
                    {
                        NetworkStats.MessagesReceived++;
                        NetworkStats.BytesReceived += result.Count;
                        ProcessBinaryMessage(buffer, result.Count);
                    }
                    else
                    {
                        // Multi-frame binary (unlikely for updates but handle it)
                        if (_binaryBuffer == null)
                            _binaryBuffer = new System.IO.MemoryStream();
                        _binaryBuffer.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            byte[] fullBuffer = _binaryBuffer.ToArray();
                            _binaryBuffer.SetLength(0);
                            NetworkStats.MessagesReceived++;
                            NetworkStats.BytesReceived += fullBuffer.Length;
                            ProcessBinaryMessage(fullBuffer, fullBuffer.Length);
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    // JSON messages (spawns, control messages)
                    string chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(chunk);
                    
                    if (result.EndOfMessage)
                    {
                        string json = messageBuilder.ToString();
                        messageBuilder.Clear();
                        NetworkStats.MessagesReceived++;
                        NetworkStats.BytesReceived += Encoding.UTF8.GetByteCount(json);
                        ProcessMessage(json);
                    }
                }
            }
            
            _isReceiving = false;
            _isConnected = false;
            
            UpdateConnectionStatus("DISCONNECTED", UnityEngine.Color.red);
            
            if (showDebugLogs)
                Debug.Log("[NetworkManager] Connection lost");
            
            // Auto-reconnect
            if (autoReconnect && !_isReconnecting)
            {
                StartCoroutine(AutoReconnect());
            }
        }
        
        private IEnumerator AutoReconnect()
        {
            _isReconnecting = true;
            _reconnectAttempts = 0;
            
            while (autoReconnect && !_isConnected)
            {
                _reconnectAttempts++;
                
                if (maxReconnectAttempts > 0 && _reconnectAttempts > maxReconnectAttempts)
                {
                    Debug.LogWarning($"[NetworkManager] Max reconnect attempts ({maxReconnectAttempts}) reached");
                    break;
                }
                
                Debug.Log($"[NetworkManager] Reconnecting in {reconnectDelay}s (attempt {_reconnectAttempts})...");
                
                UpdateConnectionStatus($"RECONNECTING ({_reconnectAttempts})", UnityEngine.Color.yellow);
                
                yield return new WaitForSeconds(reconnectDelay);
                
                // Clean up old connection
                _webSocket?.Dispose();
                _cancellationTokenSource?.Dispose();
                _hasLoggedUpdateJson = false;
                
                // Try to connect
                yield return StartCoroutine(ConnectAsync());
            }
            
            _isReconnecting = false;
            
            if (_isConnected)
            {
                _reconnectAttempts = 0;
                Debug.Log("[NetworkManager] Reconnected successfully!");
            }
        }
        
        /// <summary>
        /// Process binary update message. Zero JSON parsing, zero string allocation.
        /// Format: [1 byte type][2 bytes count][per entity: 16 bytes GUID + 24 bytes floats]
        /// </summary>
        private void ProcessBinaryMessage(byte[] data, int length)
        {
            if (length < 3) return;
            
            byte msgType = data[0];
            
            if (msgType == 0x01) // Binary update
            {
                ushort count = BitConverter.ToUInt16(data, 1);
                int offset = 3;
                
                for (int i = 0; i < count; i++)
                {
                    if (offset + 40 > length) break;
                    
                    // Read GUID (16 bytes)
                    string entityId = new System.Guid(new ReadOnlySpan<byte>(data, offset, 16)).ToString();
                    offset += 16;
                    
                    // Read position (12 bytes)
                    float px = BitConverter.ToSingle(data, offset); offset += 4;
                    float py = BitConverter.ToSingle(data, offset); offset += 4;
                    float pz = BitConverter.ToSingle(data, offset); offset += 4;
                    
                    // Read rotation (12 bytes)
                    float rx = BitConverter.ToSingle(data, offset); offset += 4;
                    float ry = BitConverter.ToSingle(data, offset); offset += 4;
                    float rz = BitConverter.ToSingle(data, offset); offset += 4;
                    
                    Vector3 position = new Vector3(px, py, pz);
                    Vector3 rotation = new Vector3(rx, ry, rz);
                    
                    // Apply to entity — try NetworkObject first, then legacy
                    if (_networkObjects.TryGetValue(entityId, out NetworkObject netObj))
                    {
                        netObj.ApplyServerUpdate(position, rotation);
                    }
                    else if (_spawnedEntities.TryGetValue(entityId, out GameObject obj))
                    {
                        var netTransform = obj.GetComponent<NetworkTransform>();
                        if (netTransform != null)
                            netTransform.ApplyServerUpdate(position, rotation);
                    }
                }
            }
        }
        
        private void ProcessMessage(string json)
        {
            try
            {
                // Removed first-update debug logging
                
                // Use JsonHelper (Newtonsoft) for deserialization
                var message = JsonHelper.DeserializeNetworkMessage(json);
                
                if (message == null)
                    return;
                
                switch (message.Type)
                {
                    case "client_id":
                        // Server assigned us an ID and master status
                        _clientId = message.OwnerId;
                        _isMasterClient = message.IsMasterClient ?? false;
                        if (showDebugLogs)
                            Debug.Log($"[NetworkManager] Received client ID: {_clientId} {(_isMasterClient ? "[MASTER]" : "[SPECTATOR]")}");
                        break;
                    
                    case "spawn":
                        HandleSpawn(message);
                        break;
                    
                    case "despawn":
                        HandleDespawn(message);
                        break;
                    
                    case "update":
                        HandleUpdate(message);
                        break;
                    
                    case "master_changed":
                        HandleMasterChanged(message);
                        break;
                    
                    default:
                        if (showDebugLogs)
                            Debug.LogWarning($"[NetworkManager] Unknown message type: {message.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                if (showDebugLogs)
                    Debug.LogError($"[NetworkManager] Error processing message: {ex.Message}");
            }
        }
        
        private void HandleSpawn(NetworkMessage message)
        {
            if (string.IsNullOrEmpty(message.EntityId))
                return;
            
            // Don't spawn if we own this entity (it's already in our scene)
            if (!string.IsNullOrEmpty(message.OwnerId) && message.OwnerId == _clientId)
            {
                // Ignore — server broadcasts spawns to all clients including owner
                return;
            }
            
            // Don't spawn if already exists
            if (_spawnedEntities.ContainsKey(message.EntityId))
                return;
            
            // Spawn entity
            Vector3 position = message.Position != null 
                ? new Vector3(message.Position.X, message.Position.Y, message.Position.Z)
                : Vector3.zero;
            
            Quaternion rotation = message.Rotation != null
                ? Quaternion.Euler(message.Rotation.X, message.Rotation.Y, message.Rotation.Z)
                : Quaternion.identity;
            
            GameObject obj = null;
            
            // Check for existing scene object first — try NetworkObject (new), then NetworkEntity (legacy)
            NetworkObject[] sceneNetworkObjects = FindObjectsByType<NetworkObject>(FindObjectsSortMode.None);
            foreach (var sceneObj in sceneNetworkObjects)
            {
                if (string.IsNullOrEmpty(sceneObj.entityId) && 
                    sceneObj.entityType == message.EntityType)
                {
                    obj = sceneObj.gameObject;
                    // Link it up
                    sceneObj.entityId = message.EntityId;
                    sceneObj.ReleaseOwnership();
                    _networkObjects[message.EntityId] = sceneObj;
                    if (showDebugLogs) Debug.Log($"[NetworkManager] Linking scene NetworkObject {obj.name} to entity {message.EntityId}");
                    break;
                }
            }
            
            if (obj == null)
            {
                NetworkEntity[] sceneEntities = FindObjectsByType<NetworkEntity>(FindObjectsSortMode.None);
                foreach (var sceneEntity in sceneEntities)
                {
                    if (string.IsNullOrEmpty(sceneEntity.entityId) && 
                        sceneEntity.entityType == message.EntityType)
                    {
                        obj = sceneEntity.gameObject;
                        if (showDebugLogs) Debug.Log($"[NetworkManager] Linking scene NetworkEntity {obj.name} to entity {message.EntityId}");
                        break;
                    }
                }
            }
            
            // If no scene object found, instantiate from prefab (runtime-spawned objects)
            if (obj == null)
            {
                GameObject prefab = GetPrefabForType(message.EntityType ?? "entity");
                if (prefab == null)
                {
                    if (showDebugLogs)
                        Debug.LogWarning($"[NetworkManager] No scene object or prefab for type '{message.EntityType}', skipping spawn");
                    return;
                }
                
                obj = Instantiate(prefab, position, rotation);
                if (showDebugLogs)
                    if (showDebugLogs) Debug.Log($"[NetworkManager] Instantiated new prefab for entity {message.EntityId}");
            }
            
            var entity = obj.GetComponent<NetworkEntity>();
            if (entity == null)
            {
                entity = obj.AddComponent<NetworkEntity>();
            }
            
            entity.entityId = message.EntityId;
            entity.entityType = message.EntityType ?? "entity";
            entity.isOwned = false; // We don't own remote entities
            
            // Disable physics for remote entities - they just render server state
            var rb = obj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true; // Make kinematic so we can move it via transform
                if (showDebugLogs)
                    if (showDebugLogs) Debug.Log($"[NetworkManager] Disabled physics for remote entity: {message.EntityId}");
            }
            
            _spawnedEntities[message.EntityId] = obj;
            
            // Update UI
            UpdateConnectionStatus("CONNECTED", UnityEngine.Color.green);
            
            // Apply initial transform
            var netTransform = obj.GetComponent<NetworkTransform>();
            if (netTransform != null && message.Position != null)
            {
                netTransform.ApplyServerUpdate(position, message.Rotation != null 
                    ? new Vector3(message.Rotation.X, message.Rotation.Y, message.Rotation.Z)
                    : (Vector3?)null);
            }
            
            if (showDebugLogs)
                if (showDebugLogs) Debug.Log($"[NetworkManager] Spawned entity: {message.EntityId} at {position}");
        }
        
        private void HandleDespawn(NetworkMessage message)
        {
            if (string.IsNullOrEmpty(message.EntityId))
                return;
            
            if (_spawnedEntities.TryGetValue(message.EntityId, out GameObject obj))
            {
                _spawnedEntities.Remove(message.EntityId);
                Destroy(obj);
                
                if (showDebugLogs)
                    if (showDebugLogs) Debug.Log($"[NetworkManager] Despawned entity: {message.EntityId}");
            }
        }
        
        private void HandleMasterChanged(NetworkMessage message)
        {
            bool weAreNewMaster = (message.OwnerId == _clientId);
            
            Debug.Log($"[NetworkManager] Master changed to {message.OwnerId} {(weAreNewMaster ? "(US!)" : "")}");
            
            if (weAreNewMaster)
            {
                _isMasterClient = true;
                
                // Take ownership of all entities and enable physics
                foreach (var kvp in _spawnedEntities)
                {
                    var entity = kvp.Value.GetComponent<NetworkEntity>();
                    if (entity != null)
                    {
                        entity.isOwned = true;
                        
                        // Re-enable physics
                        var rb = kvp.Value.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.isKinematic = false;
                            // Physics re-enabled for host migration
                        }
                    }
                }
                
                // Also update local entities (legacy)
                foreach (var kvp in _entities)
                {
                    kvp.Value.isOwned = true;
                    
                    var rb = kvp.Value.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                    }
                }
                
                // Update NetworkObjects (new system)
                foreach (var kvp in _networkObjects)
                {
                    kvp.Value.TakeOwnership();
                }
                
                Debug.Log($"[NetworkManager] Host migration complete — we are now master");
            }
        }
        
        private void HandleUpdate(NetworkMessage message)
        {
            if (message.Updates == null)
            {
                Debug.LogWarning($"[NetworkManager] HandleUpdate: Updates is null");
                return;
            }
            
            foreach (var update in message.Updates)
            {
                Vector3? position = update.Position != null
                    ? new Vector3(update.Position.X, update.Position.Y, update.Position.Z)
                    : (Vector3?)null;
                
                Vector3? rotation = update.Rotation != null
                    ? new Vector3(update.Rotation.X, update.Rotation.Y, update.Rotation.Z)
                    : (Vector3?)null;
                
                // Try NetworkObject first (new system)
                if (_networkObjects.TryGetValue(update.Id, out NetworkObject netObj))
                {
                    netObj.ApplyServerUpdate(position, rotation);
                    continue;
                }
                
                // Fallback to legacy NetworkTransform
                if (_spawnedEntities.TryGetValue(update.Id, out GameObject obj))
                {
                    var netTransform = obj.GetComponent<NetworkTransform>();
                    if (netTransform != null)
                    {
                        netTransform.ApplyServerUpdate(position, rotation);
                    }
                }
            }
        }
        
        public void RegisterEntity(NetworkEntity entity)
        {
            if (!string.IsNullOrEmpty(entity.entityId))
            {
                _entities[entity.entityId] = entity;
                
                // Also add to spawned entities dictionary so HandleUpdate can find it
                if (entity.isOwned)
                {
                    _spawnedEntities[entity.entityId] = entity.gameObject;
                    if (showDebugLogs) Debug.Log($"[NetworkManager] RegisterEntity: Added owned entity {entity.entityId} to _spawnedEntities");
                }
            }
        }
        
        public void UnregisterEntity(NetworkEntity entity)
        {
            if (!string.IsNullOrEmpty(entity.entityId))
            {
                _entities.Remove(entity.entityId);
            }
        }
        
        // ===== NetworkObject API (new unified system) =====
        
        public void RegisterNetworkObject(NetworkObject obj)
        {
            if (string.IsNullOrEmpty(obj.entityId))
                return;
            
            _networkObjects[obj.entityId] = obj;
            _spawnedEntities[obj.entityId] = obj.gameObject;
        }
        
        public void UnregisterNetworkObject(NetworkObject obj)
        {
            if (string.IsNullOrEmpty(obj.entityId))
                return;
            
            _networkObjects.Remove(obj.entityId);
            _spawnedEntities.Remove(obj.entityId);
        }
        
        public void SpawnNetworkObjectOnServer(NetworkObject obj)
        {
            if (!_isConnected)
            {
                if (!_pendingNetworkObjects.Contains(obj))
                {
                    _pendingNetworkObjects.Add(obj);
                }
                return;
            }
            
            // Queue for batched sending
            _pendingBatchSpawns.Add(new NetworkMessage
            {
                Type = "client_spawn",
                EntityId = obj.entityId,
                EntityType = obj.entityType,
                Position = new Vector3Data
                {
                    X = obj.transform.position.x,
                    Y = obj.transform.position.y,
                    Z = obj.transform.position.z
                },
                Rotation = new Vector3Data
                {
                    X = obj.transform.eulerAngles.x,
                    Y = obj.transform.eulerAngles.y,
                    Z = obj.transform.eulerAngles.z
                }
            });
            
            // Delay flush to collect all spawns from this frame
            _nextSpawnFlushTime = Time.time + 0.1f;
        }
        
        public void SendPhysicsUpdate(NetworkObject obj, Vector3 position, Vector3 rotation)
        {
            if (!_isConnected || obj == null || !obj.IsOwned)
                return;
            
            var message = new NetworkMessage
            {
                Type = "client_update",
                EntityId = obj.entityId,
                Position = new Vector3Data { X = position.x, Y = position.y, Z = position.z },
                Rotation = new Vector3Data { X = rotation.x, Y = rotation.y, Z = rotation.z }
            };
            
            StartCoroutine(SendMessageAsync(message));
        }
        
        /// <summary>
        /// Queue an update for batched sending. Much more efficient than individual sends.
        /// </summary>
        public void QueueUpdate(NetworkObject obj, Vector3 position, Vector3 rotation)
        {
            if (!_isConnected || obj == null || !obj.IsOwned)
                return;
            
            _pendingUpdates.Add(new EntityUpdate
            {
                Id = obj.entityId,
                Position = new Vector3Data { X = position.x, Y = position.y, Z = position.z },
                Rotation = new Vector3Data { X = rotation.x, Y = rotation.y, Z = rotation.z }
            });
        }
        
        private GameObject GetPrefabForType(string entityType)
        {
            // Check registry
            if (entityPrefabs != null)
            {
                foreach (var mapping in entityPrefabs)
                {
                    if (mapping.entityType == entityType)
                    {
                        return mapping.prefab;
                    }
                }
            }
            
            // Fallback to default
            return defaultPrefab;
        }
        
        public void SpawnEntityOnServer(NetworkEntity entity)
        {
            if (!_isConnected)
            {
                // Queue for spawning once connected
                if (!_pendingSpawns.Contains(entity))
                {
                    _pendingSpawns.Add(entity);
                    if (showDebugLogs)
                        if (showDebugLogs)
                            Debug.Log($"[NetworkManager] Queued {entity.gameObject.name} for spawn");
                }
                return;
            }
            
            if (showDebugLogs)
                if (showDebugLogs)
                    Debug.Log($"[NetworkManager] Sending spawn for {entity.gameObject.name}");
            
            var message = new NetworkMessage
            {
                Type = "client_spawn",
                EntityId = entity.entityId,
                EntityType = entity.entityType,
                Position = new Vector3Data
                {
                    X = entity.transform.position.x,
                    Y = entity.transform.position.y,
                    Z = entity.transform.position.z
                },
                Rotation = new Vector3Data
                {
                    X = entity.transform.eulerAngles.x,
                    Y = entity.transform.eulerAngles.y,
                    Z = entity.transform.eulerAngles.z
                }
            };
            
            StartCoroutine(SendMessageAsync(message));
        }
        
        /// <summary>
        /// Send physics state update to server for owned entities
        /// </summary>
        public void SendPhysicsUpdate(NetworkEntity entity, Vector3 position, Vector3 rotation)
        {
            if (!_isConnected || entity == null || !entity.isOwned)
                return;
            
            var message = new NetworkMessage
            {
                Type = "client_update",
                EntityId = entity.entityId,
                Position = new Vector3Data
                {
                    X = position.x,
                    Y = position.y,
                    Z = position.z
                },
                Rotation = new Vector3Data
                {
                    X = rotation.x,
                    Y = rotation.y,
                    Z = rotation.z
                }
            };
            
            StartCoroutine(SendMessageAsync(message));
        }
        
        private void SpawnPendingEntities()
        {
            // Legacy entities
            if (_pendingSpawns.Count > 0)
            {
                if (showDebugLogs)
                    Debug.Log($"[NetworkManager] Spawning {_pendingSpawns.Count} queued entities");
                
                foreach (var entity in _pendingSpawns)
                {
                    if (entity != null)
                        SpawnEntityOnServer(entity);
                }
                _pendingSpawns.Clear();
            }
            
            // New NetworkObjects
            if (_pendingNetworkObjects.Count > 0)
            {
                if (showDebugLogs)
                    Debug.Log($"[NetworkManager] Spawning {_pendingNetworkObjects.Count} queued NetworkObjects");
                
                foreach (var obj in _pendingNetworkObjects)
                {
                    if (obj != null)
                        SpawnNetworkObjectOnServer(obj);
                }
                _pendingNetworkObjects.Clear();
            }
        }
        
        private IEnumerator SendMessageAsync(NetworkMessage message)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                yield break;
            
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            
            NetworkStats.MessagesSent++;
            NetworkStats.BytesSent += bytes.Length;
            
            Task sendTask = null;
            
            try
            {
                sendTask = _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                if (showDebugLogs)
                    Debug.LogError($"[NetworkManager] Send exception: {ex.Message}");
                yield break;
            }
            
            // Yield outside of try-catch
            yield return new WaitUntil(() => sendTask.IsCompleted);
            
            if (sendTask.IsFaulted)
            {
                if (!_hasLoggedSendError)
                {
                    _hasLoggedSendError = true;
                    Debug.LogWarning("[NetworkManager] Connection lost — stopping sends until reconnected");
                }
                _isConnected = false;
            }
        }
        
        private void OnDestroy()
        {
            if (_isConnected)
            {
                _cancellationTokenSource?.Cancel();
                _webSocket?.Abort();
            }
        }
    }
    
    /// <summary>
    /// Maps entity types to prefabs for spawning
    /// </summary>
    [Serializable]
    public class EntityPrefabMapping
    {
        public string entityType;
        public GameObject prefab;
    }
    
    // Message classes (must match server protocol)
    [Serializable]
    public class NetworkMessage
    {
        public string Type;
        public string EntityId;
        public string EntityType;
        public string OwnerId; // Which client owns this entity
        public bool? IsMasterClient; // Is this client the master (first to connect)?
        public Vector3Data Position;
        public Vector3Data Rotation;
        public EntityUpdate[] Updates;
    }
    
    [Serializable]
    public class Vector3Data
    {
        public float X;
        public float Y;
        public float Z;
    }
    
    [Serializable]
    public class EntityUpdate
    {
        public string Id;
        public Vector3Data Position;
        public Vector3Data Rotation;
    }
}
