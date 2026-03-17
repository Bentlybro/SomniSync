using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SomniumNetwork.Client
{
    /// <summary>
    /// Main network manager. Handles WebSocket connection and entity synchronization.
    /// Add this component to a GameObject in your scene.
    /// </summary>
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }
        
        [Header("Connection")]
        [Tooltip("WebSocket server URL")]
        public string serverUrl = "ws://localhost:8080/";
        
        [Tooltip("Auto-connect on Start()")]
        public bool autoConnect = true;
        
        [Header("Entity Management")]
        [Tooltip("Prefab to spawn for networked entities (must have NetworkEntity component)")]
        public GameObject entityPrefab;
        
        [Header("Debug")]
        public bool showDebugLogs = true;
        
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected = false;
        private bool _isReceiving = false;
        
        // Entity tracking
        private Dictionary<string, NetworkEntity> _entities = new Dictionary<string, NetworkEntity>();
        private Dictionary<string, GameObject> _spawnedEntities = new Dictionary<string, GameObject>();
        
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
                if (showDebugLogs)
                    Debug.Log("[NetworkManager] Connected!");
                
                // Start receiving messages
                StartCoroutine(ReceiveMessagesAsync());
            }
            else
            {
                if (showDebugLogs)
                    Debug.LogError($"[NetworkManager] Connection failed: {connectTask.Exception?.Message}");
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
            byte[] buffer = new byte[8192];
            
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
                        Debug.LogError($"[NetworkManager] Receive error: {receiveTask.Exception?.Message}");
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
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(json);
                }
            }
            
            _isReceiving = false;
        }
        
        private void ProcessMessage(string json)
        {
            try
            {
                var message = JsonSerializer.Deserialize<NetworkMessage>(json);
                
                if (message == null)
                    return;
                
                switch (message.Type)
                {
                    case "spawn":
                        HandleSpawn(message);
                        break;
                    
                    case "despawn":
                        HandleDespawn(message);
                        break;
                    
                    case "update":
                        HandleUpdate(message);
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
            
            GameObject obj = Instantiate(entityPrefab, position, rotation);
            
            var entity = obj.GetComponent<NetworkEntity>();
            if (entity == null)
            {
                entity = obj.AddComponent<NetworkEntity>();
            }
            
            entity.entityId = message.EntityId;
            entity.entityType = message.EntityType ?? "entity";
            
            _spawnedEntities[message.EntityId] = obj;
            
            // Apply initial transform
            var netTransform = obj.GetComponent<NetworkTransform>();
            if (netTransform != null && message.Position != null)
            {
                netTransform.ApplyServerUpdate(position, message.Rotation != null 
                    ? new Vector3(message.Rotation.X, message.Rotation.Y, message.Rotation.Z)
                    : (Vector3?)null);
            }
            
            if (showDebugLogs)
                Debug.Log($"[NetworkManager] Spawned entity: {message.EntityId} at {position}");
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
                    Debug.Log($"[NetworkManager] Despawned entity: {message.EntityId}");
            }
        }
        
        private void HandleUpdate(NetworkMessage message)
        {
            if (message.Updates == null)
                return;
            
            foreach (var update in message.Updates)
            {
                if (_spawnedEntities.TryGetValue(update.Id, out GameObject obj))
                {
                    var netTransform = obj.GetComponent<NetworkTransform>();
                    if (netTransform != null)
                    {
                        Vector3? position = update.Position != null
                            ? new Vector3(update.Position.X, update.Position.Y, update.Position.Z)
                            : (Vector3?)null;
                        
                        Vector3? rotation = update.Rotation != null
                            ? new Vector3(update.Rotation.X, update.Rotation.Y, update.Rotation.Z)
                            : (Vector3?)null;
                        
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
            }
        }
        
        public void UnregisterEntity(NetworkEntity entity)
        {
            if (!string.IsNullOrEmpty(entity.entityId))
            {
                _entities.Remove(entity.entityId);
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
    
    // Message classes (must match server protocol)
    [Serializable]
    public class NetworkMessage
    {
        public string Type { get; set; }
        public string EntityId { get; set; }
        public string EntityType { get; set; }
        public Vector3Data Position { get; set; }
        public Vector3Data Rotation { get; set; }
        public List<EntityUpdate> Updates { get; set; }
    }
    
    [Serializable]
    public class Vector3Data
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }
    
    [Serializable]
    public class EntityUpdate
    {
        public string Id { get; set; }
        public Vector3Data Position { get; set; }
        public Vector3Data Rotation { get; set; }
    }
}
