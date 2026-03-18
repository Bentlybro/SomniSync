using UnityEngine;

namespace Bentlybro.CustomNetworking
{
    /// <summary>
    /// Core component that marks a GameObject as network-synced.
    /// Handles registration with NetworkManager and lifecycle.
    /// </summary>
    public class NetworkEntity : MonoBehaviour
    {
        [Header("Entity Settings")]
        [Tooltip("Unique ID for this entity (auto-generated if empty)")]
        public string entityId = "";
        
        [Tooltip("Entity type (cube, npc, etc.)")]
        public string entityType = "entity";
        
        [Tooltip("Spawn this entity on Start()")]
        public bool spawnOnStart = false;
        
        [Tooltip("Destroy when disconnected from server")]
        public bool destroyOnDisconnect = true;
        
        [Header("Debug")]
        public bool showDebugLogs = false;
        
        public bool IsSpawned { get; private set; }
        
        private void Start()
        {
            if (spawnOnStart)
            {
                Register();
            }
        }
        
        public void Register()
        {
            if (NetworkManager.Instance == null)
            {
                if (showDebugLogs)
                    Debug.LogError("[NetworkEntity] NetworkManager not found!");
                return;
            }
            
            if (string.IsNullOrEmpty(entityId))
            {
                entityId = System.Guid.NewGuid().ToString();
            }
            
            NetworkManager.Instance.RegisterEntity(this);
            
            // Tell server about this entity
            NetworkManager.Instance.SpawnEntityOnServer(this);
            
            IsSpawned = true;
            
            if (showDebugLogs)
                Debug.Log($"[NetworkEntity] Registered: {entityId}");
        }
        
        public void Unregister()
        {
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.UnregisterEntity(this);
            }
            
            IsSpawned = false;
            
            if (showDebugLogs)
                Debug.Log($"[NetworkEntity] Unregistered: {entityId}");
        }
        
        private void OnDestroy()
        {
            if (IsSpawned)
            {
                Unregister();
            }
        }
    }
}
