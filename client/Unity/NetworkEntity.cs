using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
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
        public bool isOwned = true; // By default, scene objects are owned by the client that spawned them
        
        private void Start()
        {
            if (spawnOnStart)
            {
                // Delay registration slightly to ensure NetworkManager is ready
                StartCoroutine(DelayedRegister());
            }
        }
        
        private System.Collections.IEnumerator DelayedRegister()
        {
            // Wait one frame to ensure NetworkManager.Start() has run
            yield return null;
            
            // Wait until we receive our client ID from server
            while (NetworkManager.Instance != null && string.IsNullOrEmpty(NetworkManager.Instance.ClientId))
            {
                yield return null;
            }
            
            if (NetworkManager.Instance == null)
            {
                if (showDebugLogs)
                    Debug.LogError("[NetworkEntity] NetworkManager not found!");
                yield break;
            }
            
            // Only master client spawns scene objects to server
            if (!NetworkManager.Instance.IsMasterClient)
            {
                // Spectator: Keep object but disable physics (Photon Fusion pattern)
                var rb = GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true; // Disable physics simulation
                    if (showDebugLogs)
                        Debug.Log($"[NetworkEntity] Spectator - disabled physics on {gameObject.name}");
                }
                
                // Mark as non-owned so we receive remote updates
                isOwned = false;
                
                // Don't register with server (master will handle spawning)
                if (showDebugLogs)
                    Debug.Log($"[NetworkEntity] Spectator - waiting for remote updates on {gameObject.name}");
                yield break;
            }
            
            if (showDebugLogs)
                Debug.Log($"[NetworkEntity] Master client - registering {gameObject.name}");
            
            Register();
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
