using UnityEngine;

namespace Bentlybro.CustomNetworking
{
    /// <summary>
    /// NetworkObject - Core network identity component.
    /// Mirrors Photon Fusion's NetworkObject.
    /// Required on any GameObject you want to network.
    /// </summary>
    public class NetworkObject : MonoBehaviour
    {
        [Header("Network Identity")]
        [Tooltip("Unique network ID (auto-assigned)")]
        [SerializeField] private string _networkId = "";
        
        [Tooltip("Object type for prefab spawning")]
        public string objectType = "default";
        
        [Header("Spawning")]
        [Tooltip("Automatically register this object when scene loads")]
        public bool spawnOnStart = true;
        
        [Tooltip("Destroy this object when disconnected from server")]
        public bool destroyOnDisconnect = true;
        
        [Header("State Authority")]
        [Tooltip("Which client controls this object (0 = server, 1+ = client ID)")]
        public int stateAuthority = 0;
        
        [Header("Debug")]
        public bool showDebugLogs = false;
        
        public string NetworkId => _networkId;
        public bool IsSpawned { get; private set; }
        public bool HasStateAuthority { get; private set; }
        
        private void Start()
        {
            if (spawnOnStart)
            {
                Spawn();
            }
        }
        
        /// <summary>
        /// Spawn this object on the network
        /// </summary>
        public void Spawn()
        {
            if (NetworkManager.Instance == null)
            {
                if (showDebugLogs)
                    Debug.LogError($"[NetworkObject] NetworkManager not found! Cannot spawn {gameObject.name}");
                return;
            }
            
            if (string.IsNullOrEmpty(_networkId))
            {
                _networkId = System.Guid.NewGuid().ToString();
            }
            
            NetworkManager.Instance.RegisterNetworkObject(this);
            NetworkManager.Instance.SpawnObjectOnServer(this);
            
            IsSpawned = true;
            
            if (showDebugLogs)
                Debug.Log($"[NetworkObject] Spawned: {gameObject.name} ({_networkId})");
        }
        
        /// <summary>
        /// Despawn this object from the network
        /// </summary>
        public void Despawn()
        {
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.UnregisterNetworkObject(this);
            }
            
            IsSpawned = false;
            
            if (showDebugLogs)
                Debug.Log($"[NetworkObject] Despawned: {gameObject.name} ({_networkId})");
        }
        
        /// <summary>
        /// Request state authority over this object
        /// </summary>
        public void RequestStateAuthority()
        {
            // TODO: Send request to server
            if (showDebugLogs)
                Debug.Log($"[NetworkObject] Requesting state authority for {gameObject.name}");
        }
        
        /// <summary>
        /// Check if local client has state authority
        /// </summary>
        public bool HasAuthority()
        {
            // TODO: Check with NetworkManager
            return HasStateAuthority;
        }
        
        private void OnDestroy()
        {
            if (IsSpawned)
            {
                Despawn();
            }
        }
    }
}
