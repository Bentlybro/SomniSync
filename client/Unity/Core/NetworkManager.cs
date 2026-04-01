using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// v2 — Dumb relay architecture. Server just pipes bytes.
    /// Master sends position updates + snapshots. Spectators receive and apply.
    /// </summary>
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

        [Header("Connection")]
        public string serverUrl = "ws://localhost:9090";
        [Tooltip("Room ID — clients in the same room see each other. Leave empty for 'default'.")]
        public string roomId = "";
        public float reconnectDelay = 3f;

        [Header("Sync")]
        [Tooltip("How many times per second to send position updates")]
        public int sendRate = 60;
        [Tooltip("Minimum movement (meters) before an object is included in an update")]
        public float positionThreshold = 0.01f;
        [Tooltip("Minimum rotation (degrees) before an object is included in an update")]
        public float rotationThreshold = 1f;

        // State
        public bool IsConnected { get; private set; }
        public bool IsMaster { get; private set; }
        public ushort ClientId { get; private set; }
        public bool IsNetworkReady { get; private set; }

        // Events — subscribe to these for connection lifecycle
        /// <summary>Fired when networking is ready (connected + role assigned). Safe to use network features.</summary>
        public event Action OnNetworkReady;
        /// <summary>Fired when connection is lost.</summary>
        public event Action OnDisconnected;
        /// <summary>Fired when this client becomes master (initial or promotion).</summary>
        public event Action OnBecameMaster;
        /// <summary>Fired when this client becomes spectator.</summary>
        public event Action OnBecameSpectator;

        // All network objects sorted deterministically — index = local_id
        private List<NetworkObject> _objects = new List<NetworkObject>();
        private Dictionary<ushort, NetworkObject> _objectMap = new Dictionary<ushort, NetworkObject>();
        private bool _sceneReady;

        // WebSocket
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly byte[] _recvBuffer = new byte[262144]; // 256KB — supports 4000+ scaled objects
        private float _sendInterval;
        private float _nextSendTime;
        private bool _pushDetectorAttached;

        // Send queue — ensures only one SendAsync is in-flight at a time
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();

        // After snapshot, snap first N updates instead of interpolating
        private int _snapFramesRemaining;
        private const int SNAP_FRAMES_AFTER_SNAPSHOT = 3;

        // Periodic resync snapshot (master only)
        [Header("Resync")]
        [Tooltip("Seconds between full snapshots to prevent drift (0 = disabled)")]
        public float resyncInterval = 5f;
        private float _nextResyncTime;

        // Area of Interest (AOI) — master only simulates objects near players
        [Header("Area of Interest")]
        [Tooltip("Enable AOI — objects far from all players are frozen (master only)")]
        public bool enableAOI = true;
        [Tooltip("Radius around each player where objects stay active (meters)")]
        public float aoiRadius = 50f;
        [Tooltip("How often to recalculate AOI (times per second)")]
        public float aoiCheckRate = 2f;
        [Tooltip("Extra radius before deactivating (prevents flicker at boundary)")]
        public float aoiHysteresis = 5f;

        // AOI state
        private float _nextAOICheckTime;
        private Dictionary<ushort, Vector3> _playerPositions = new Dictionary<ushort, Vector3>();
        private Dictionary<ushort, float> _playerLastSeen = new Dictionary<ushort, float>();
        private Transform _localPlayerTransform;
        private float _nextPlayerPosSendTime;
        private const float PLAYER_POS_SEND_RATE = 5f; // 5Hz
        private const float PLAYER_TIMEOUT = 5f; // Remove player after 5s of no updates

        // Stats (public for NetworkStats overlay)
        public static int MessagesSent;
        public static int MessagesReceived;
        public static int BytesSent;
        public static int BytesReceived;
        public static int ActiveAOIObjects;
        public static int TotalObjects;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            _sendInterval = 1f / sendRate;
        }

        private void Start()
        {
            StartCoroutine(ConnectLoop());
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            Instance = null;
        }

        private void Update()
        {
            // One-frame delay to let all NetworkObject.Start() run first
            if (!_sceneReady)
            {
                CollectAndSortObjects();
                _sceneReady = true;
                Debug.Log($"[Net] Scene ready. {_objects.Count} objects registered.");
            }

            if (!IsConnected) return;

            // Attach push detector to local player (once)
            if (!_pushDetectorAttached && !IsMaster)
            {
                AttachPushDetector();
            }

            // Send position updates at configured rate
            if (Time.time >= _nextSendTime)
            {
                _nextSendTime = Time.time + _sendInterval;

                if (IsMaster)
                    SendPositionUpdates();

                // Any client sends interaction updates for objects they've claimed
                SendInteractionUpdates();
            }

            // Periodic full snapshot to correct drift (master only)
            // Only send if at least one active object has moved since last snapshot
            if (IsMaster && resyncInterval > 0 && Time.time >= _nextResyncTime)
            {
                _nextResyncTime = Time.time + resyncInterval;
                bool anyMoved = false;
                foreach (var obj in _objects)
                {
                    if (!obj.IsAOIActive) continue;
                    if (obj.HasMoved(positionThreshold, rotationThreshold))
                    {
                        anyMoved = true;
                        break;
                    }
                }
                if (anyMoved)
                    SendSnapshot();
            }

            // AOI: master recalculates active objects periodically
            if (IsMaster && enableAOI && Time.time >= _nextAOICheckTime)
            {
                _nextAOICheckTime = Time.time + (1f / aoiCheckRate);
                UpdateAOI();
            }

            // Spectators send their position to master for AOI
            if (!IsMaster && enableAOI && Time.time >= _nextPlayerPosSendTime)
            {
                _nextPlayerPosSendTime = Time.time + (1f / PLAYER_POS_SEND_RATE);
                SendPlayerPosition();
            }
        }

        // =====================================================================
        // Push Detector (attach to local player at runtime)
        // =====================================================================

        private void AttachPushDetector()
        {
            // Find the local player's body (NonXR.Body or XR.Body)
            foreach (var cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                string name = cc.gameObject.name;
                if (name != "NonXR.Body" && name != "XR.Body") continue;

                // Verify it's under "Original Player:" (local, not remote)
                Transform root = cc.transform;
                while (root.parent != null) root = root.parent;
                if (!root.name.StartsWith("Original Player:")) continue;

                // Attach our detector if not already there
                if (cc.GetComponent<PlayerPushDetector>() == null)
                {
                    cc.gameObject.AddComponent<PlayerPushDetector>();
                    Debug.Log($"[Net] Push detector attached to {cc.gameObject.name}");
                }
                _pushDetectorAttached = true;
                return;
            }
            // Player not spawned yet — will retry next frame
        }

        // =====================================================================
        // Object Registration
        // =====================================================================

        /// <summary>
        /// Collect all NetworkObjects, sort deterministically, assign local IDs.
        /// Both master and spectator do this identically → same IDs.
        /// </summary>
        private void CollectAndSortObjects()
        {
            _objects.Clear();
            _objectMap.Clear();

            var all = FindObjectsByType<NetworkObject>(FindObjectsSortMode.None);

            // Sort deterministically: by entityType, then by name, then by position
            Array.Sort(all, (a, b) =>
            {
                int cmp = string.Compare(a.entityType, b.entityType, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
                cmp = string.Compare(a.gameObject.name, b.gameObject.name, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
                // Tie-break by position (x, then y, then z)
                var pa = a.transform.position;
                var pb = b.transform.position;
                cmp = pa.x.CompareTo(pb.x);
                if (cmp != 0) return cmp;
                cmp = pa.y.CompareTo(pb.y);
                if (cmp != 0) return cmp;
                return pa.z.CompareTo(pb.z);
            });

            for (int i = 0; i < all.Length; i++)
            {
                ushort localId = (ushort)i;
                all[i].LocalId = localId;
                _objects.Add(all[i]);
                _objectMap[localId] = all[i];
            }
        }

        /// <summary>
        /// Register a runtime-spawned object. Assigns next available LocalId
        /// and broadcasts a spawn control message so other clients can match it.
        /// </summary>
        public void RegisterObject(NetworkObject obj)
        {
            ushort localId = (ushort)_objects.Count;
            obj.LocalId = localId;
            _objects.Add(obj);
            _objectMap[localId] = obj;

            if (IsConnected)
                SendSpawnMessage(obj);
        }

        /// <summary>
        /// Unregister a despawned object and broadcast despawn to other clients.
        /// </summary>
        public void UnregisterObject(NetworkObject obj)
        {
            if (_objectMap.ContainsKey(obj.LocalId))
            {
                _objectMap.Remove(obj.LocalId);
                _objects.Remove(obj);

                if (IsConnected)
                    SendDespawnMessage(obj);
            }
        }

        /// <summary>
        /// Broadcast spawn: [0xFF][0x08][u16 localId][u8 nameLen][entityType][half x][half y][half z]
        /// </summary>
        private void SendSpawnMessage(NetworkObject obj)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(obj.entityType ?? "object");
            byte[] buf = new byte[2 + 2 + 1 + nameBytes.Length + 6];
            buf[0] = Protocol.MSG_CONTROL;
            buf[1] = Protocol.CTRL_SPAWN;
            Protocol.WriteU16(buf, 2, obj.LocalId);
            buf[4] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, buf, 5, nameBytes.Length);
            int off = 5 + nameBytes.Length;
            Protocol.WriteHalf(buf, off, obj.transform.position.x);
            Protocol.WriteHalf(buf, off + 2, obj.transform.position.y);
            Protocol.WriteHalf(buf, off + 4, obj.transform.position.z);
            SendBinary(buf);
        }

        /// <summary>
        /// Broadcast despawn: [0xFF][0x09][u16 localId][u8 nameLen][entityType][half x][half y][half z]
        /// </summary>
        private void SendDespawnMessage(NetworkObject obj)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(obj.entityType ?? "object");
            byte[] buf = new byte[2 + 2 + 1 + nameBytes.Length + 6];
            buf[0] = Protocol.MSG_CONTROL;
            buf[1] = Protocol.CTRL_DESPAWN;
            Protocol.WriteU16(buf, 2, obj.LocalId);
            buf[4] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, buf, 5, nameBytes.Length);
            int off = 5 + nameBytes.Length;
            Protocol.WriteHalf(buf, off, obj.transform.position.x);
            Protocol.WriteHalf(buf, off + 2, obj.transform.position.y);
            Protocol.WriteHalf(buf, off + 4, obj.transform.position.z);
            SendBinary(buf);
        }

        // =====================================================================
        // Connection
        // =====================================================================

        private IEnumerator ConnectLoop()
        {
            while (true)
            {
                yield return StartCoroutine(Connect());
                if (!IsConnected)
                {
                    Debug.Log($"[Net] Reconnecting in {reconnectDelay}s...");
                    yield return new WaitForSeconds(reconnectDelay);
                }
            }
        }

        private IEnumerator Connect()
        {
            // Build URL: serverUrl/roomId (if roomId is set)
            string url = serverUrl.TrimEnd('/');
            if (!string.IsNullOrEmpty(roomId))
                url += "/" + roomId;

            Debug.Log($"[Net] Connecting to {url}...");
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            var connectTask = _ws.ConnectAsync(new Uri(url), _cts.Token);
            while (!connectTask.IsCompleted)
                yield return null;

            if (connectTask.IsFaulted || _ws.State != WebSocketState.Open)
            {
                Debug.LogWarning($"[Net] Connection failed: {connectTask.Exception?.InnerException?.Message}");
                IsConnected = false;
                yield break;
            }

            IsConnected = true;
            Debug.Log("[Net] Connected!");

            // Start receive and send loops as coroutines
            StartCoroutine(ReceiveLoop());
            StartCoroutine(SendLoop());

            // Wait until disconnected
            while (IsConnected && _ws.State == WebSocketState.Open)
                yield return null;

            IsConnected = false;
            IsMaster = false;
            IsNetworkReady = false;
            Debug.Log("[Net] Disconnected.");
            OnDisconnected?.Invoke();
        }

        // =====================================================================
        // Receive
        // =====================================================================

        private IEnumerator ReceiveLoop()
        {
            // Accumulator for fragmented WebSocket messages
            byte[] fragmentBuffer = null;
            int fragmentOffset = 0;

            while (IsConnected && _ws.State == WebSocketState.Open)
            {
                var seg = new ArraySegment<byte>(_recvBuffer);
                var recvTask = _ws.ReceiveAsync(seg, _cts.Token);
                while (!recvTask.IsCompleted)
                    yield return null;

                if (recvTask.IsFaulted || recvTask.Result.MessageType == WebSocketMessageType.Close)
                {
                    IsConnected = false;
                    yield break;
                }

                var result = recvTask.Result;
                if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
                    continue;

                if (result.EndOfMessage && fragmentBuffer == null)
                {
                    // Common fast path: single-frame message
                    MessagesReceived++;
                    BytesReceived += result.Count;
                    ProcessBinary(_recvBuffer, result.Count);
                }
                else
                {
                    // Fragmented message — accumulate until EndOfMessage
                    if (fragmentBuffer == null)
                    {
                        fragmentBuffer = new byte[_recvBuffer.Length * 2];
                        fragmentOffset = 0;
                    }
                    // Grow if needed
                    if (fragmentOffset + result.Count > fragmentBuffer.Length)
                    {
                        byte[] bigger = new byte[fragmentBuffer.Length * 2];
                        Buffer.BlockCopy(fragmentBuffer, 0, bigger, 0, fragmentOffset);
                        fragmentBuffer = bigger;
                    }
                    Buffer.BlockCopy(_recvBuffer, 0, fragmentBuffer, fragmentOffset, result.Count);
                    fragmentOffset += result.Count;

                    if (result.EndOfMessage)
                    {
                        MessagesReceived++;
                        BytesReceived += fragmentOffset;
                        ProcessBinary(fragmentBuffer, fragmentOffset);
                        fragmentBuffer = null;
                        fragmentOffset = 0;
                    }
                }
            }
        }

        private void ProcessBinary(byte[] data, int length)
        {
            try
            {
                byte msgType = data[0];

                switch (msgType)
                {
                    case 0x01: // Position updates (from master)
                        HandlePositionUpdates(data, length);
                        break;

                    case 0x02: // Snapshot
                        HandleSnapshot(data, length);
                        break;

                    case 0x03: // Interaction updates (from any client)
                        HandleInteractionUpdates(data, length);
                        break;

                    case 0x04: // Variable updates (from any client)
                        HandleVariableUpdate(data, length);
                        break;

                    case 0x05: // Scaled position updates
                        HandleScaledPositionUpdates(data, length);
                        break;

                    case 0x09: // Scaled interaction updates
                        HandleScaledInteractionUpdates(data, length);
                        break;

                    case 0x06: // Event (RPC) from any client
                        HandleEvent(data, length);
                        break;

                    case 0x07: // Animator state from any client
                        HandleAnimatorState(data, length);
                        break;

                    case 0x08: // Rigidbody velocity from any client
                        HandleRigidbodyUpdate(data, length);
                        break;

                    case 0xFF: // Control message
                        if (length >= 2)
                            HandleControl(data, length);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Net] ProcessBinary error (type=0x{data[0]:X2}, len={length}): {ex.Message}");
            }
        }

        // =====================================================================
        // Control Messages
        // =====================================================================

        private void HandleControl(byte[] data, int length)
        {
            byte ctrl = data[1];

            switch (ctrl)
            {
                case 0x01: // You are master
                    ClientId = BitConverter.ToUInt16(data, 2);
                    IsMaster = true;
                    Debug.Log($"[Net] Assigned as MASTER (client_{ClientId})");
                    EnablePhysicsOnAll(true);
                    SendSnapshot();
                    if (!IsNetworkReady) { IsNetworkReady = true; OnNetworkReady?.Invoke(); }
                    OnBecameMaster?.Invoke();
                    break;

                case 0x02: // You are spectator
                    ClientId = BitConverter.ToUInt16(data, 2);
                    IsMaster = false;
                    Debug.Log($"[Net] Assigned as SPECTATOR (client_{ClientId})");
                    EnablePhysicsOnAll(false);
                    if (!IsNetworkReady) { IsNetworkReady = true; OnNetworkReady?.Invoke(); }
                    OnBecameSpectator?.Invoke();
                    break;

                case 0x03: // Send snapshot (master only)
                    ushort targetClient = BitConverter.ToUInt16(data, 2);
                    Debug.Log($"[Net] Snapshot requested for client_{targetClient}");
                    SendSnapshot();
                    break;

                case 0x04: // Master changed (you're the new master)
                    IsMaster = true;
                    Debug.Log($"[Net] Promoted to MASTER");
                    EnablePhysicsOnAll(true);
                    SendSnapshot();
                    OnBecameMaster?.Invoke();
                    break;

                case 0x05: // Remote client claimed an object
                    if (length >= 6)
                    {
                        ushort claimObjId = BitConverter.ToUInt16(data, 2);
                        ushort claimClientId = BitConverter.ToUInt16(data, 4);
                        if (_objectMap.TryGetValue(claimObjId, out var claimObj))
                        {
                            claimObj.OnRemoteClaim(claimClientId);
                        }
                    }
                    break;

                case 0x06: // Remote client released an object
                    if (length >= 6)
                    {
                        ushort releaseObjId = BitConverter.ToUInt16(data, 2);
                        if (_objectMap.TryGetValue(releaseObjId, out var releaseObj))
                        {
                            releaseObj.OnRemoteRelease();
                        }
                    }
                    break;

                case 0x07: // Player position (for AOI)
                    if (length >= 10)
                    {
                        ushort posClientId = BitConverter.ToUInt16(data, 2);
                        float px = Protocol.ReadHalf(data, 4);
                        float py = Protocol.ReadHalf(data, 6);
                        float pz = Protocol.ReadHalf(data, 8);
                        _playerPositions[posClientId] = new Vector3(px, py, pz);
                        _playerLastSeen[posClientId] = Time.time;
                    }
                    break;

                case Protocol.CTRL_SPAWN: // Remote spawn — find matching inactive object
                    if (length >= 5)
                    {
                        ushort spawnId = BitConverter.ToUInt16(data, 2);
                        int nameLen = data[4];
                        if (length >= 5 + nameLen + 6)
                        {
                            string entityType = System.Text.Encoding.UTF8.GetString(data, 5, nameLen);
                            int posOff = 5 + nameLen;
                            float sx = Protocol.ReadHalf(data, posOff);
                            float sy = Protocol.ReadHalf(data, posOff + 2);
                            float sz = Protocol.ReadHalf(data, posOff + 4);
                            Debug.Log($"[Net] Remote spawn: id={spawnId} type={entityType} pos=({sx},{sy},{sz})");
                        }
                    }
                    break;

                case Protocol.CTRL_DESPAWN: // Remote despawn
                    if (length >= 4)
                    {
                        ushort despawnId = BitConverter.ToUInt16(data, 2);
                        if (_objectMap.TryGetValue(despawnId, out var despawnObj))
                        {
                            _objectMap.Remove(despawnId);
                            _objects.Remove(despawnObj);
                            Debug.Log($"[Net] Remote despawn: id={despawnId}");
                        }
                    }
                    break;
            }
        }

        private void EnablePhysicsOnAll(bool enable)
        {
            foreach (var obj in _objects)
            {
                if (enable)
                    obj.IsAOIActive = true; // Reset AOI on master assignment
                obj.SetPhysics(enable);
            }
        }

        // =====================================================================
        // Area of Interest (AOI) — master only
        // =====================================================================

        private void UpdateAOI()
        {
            // Clean up stale players (disconnected without notice)
            List<ushort> stale = null;
            foreach (var kvp in _playerLastSeen)
            {
                if (Time.time - kvp.Value > PLAYER_TIMEOUT)
                {
                    if (stale == null) stale = new List<ushort>();
                    stale.Add(kvp.Key);
                }
            }
            if (stale != null)
            {
                foreach (var id in stale)
                {
                    _playerPositions.Remove(id);
                    _playerLastSeen.Remove(id);
                }
            }

            // Collect all player positions: own + remote spectators
            List<Vector3> playerPositions = new List<Vector3>();

            // Master's own position
            if (_localPlayerTransform == null)
                _localPlayerTransform = FindLocalPlayerTransform();
            if (_localPlayerTransform != null)
                playerPositions.Add(_localPlayerTransform.position);

            // Remote player positions
            foreach (var kvp in _playerPositions)
                playerPositions.Add(kvp.Value);

            if (playerPositions.Count == 0) return;

            float activateRadiusSq = aoiRadius * aoiRadius;
            float deactivateRadiusSq = (aoiRadius + aoiHysteresis) * (aoiRadius + aoiHysteresis);
            int activeCount = 0;

            foreach (var obj in _objects)
            {
                // Never deactivate remotely claimed objects
                if (obj.IsClaimedByRemote)
                {
                    obj.AOIActivate();
                    activeCount++;
                    continue;
                }

                Vector3 objPos = obj.transform.position;
                float closestDistSq = float.MaxValue;

                for (int i = 0; i < playerPositions.Count; i++)
                {
                    float distSq = (objPos - playerPositions[i]).sqrMagnitude;
                    if (distSq < closestDistSq)
                        closestDistSq = distSq;
                }

                if (obj.IsAOIActive)
                {
                    // Currently active — use wider deactivate radius (hysteresis)
                    if (closestDistSq > deactivateRadiusSq)
                        obj.AOIDeactivate();
                    else
                        activeCount++;
                }
                else
                {
                    // Currently inactive — use tighter activate radius
                    if (closestDistSq <= activateRadiusSq)
                    {
                        obj.AOIActivate();
                        activeCount++;
                    }
                }
            }

            ActiveAOIObjects = activeCount;
            TotalObjects = _objects.Count;
        }

        private void SendPlayerPosition()
        {
            if (_localPlayerTransform == null)
                _localPlayerTransform = FindLocalPlayerTransform();
            if (_localPlayerTransform == null) return;

            Vector3 pos = _localPlayerTransform.position;
            // [0xFF][0x07][u16 client_id][half x][half y][half z]
            byte[] buf = new byte[10];
            buf[0] = 0xFF;
            buf[1] = Protocol.CTRL_PLAYER_POSITION;
            Protocol.WriteU16(buf, 2, ClientId);
            Protocol.WriteHalf(buf, 4, pos.x);
            Protocol.WriteHalf(buf, 6, pos.y);
            Protocol.WriteHalf(buf, 8, pos.z);
            SendBinary(buf);
        }

        private Transform FindLocalPlayerTransform()
        {
            foreach (var cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                string name = cc.gameObject.name;
                if (name != "NonXR.Body" && name != "XR.Body") continue;

                Transform root = cc.transform;
                while (root.parent != null) root = root.parent;
                if (!root.name.StartsWith("Original Player:")) continue;

                return cc.transform;
            }
            return null;
        }

        // =====================================================================
        // Position Updates (0x01) — master sends, spectators receive
        // =====================================================================
        // Compact format: [0x01][u16 count][per entity: u16 id + 6B half-pos + 6B u16-rot = 14B]

        private void SendPositionUpdates()
        {
            if (_objects.Count == 0) return;

            int movedCount = 0;
            int movedScaledCount = 0;
            foreach (var obj in _objects)
            {
                if (obj.IsClaimedByRemote) continue;
                if (!obj.IsAOIActive) continue; // AOI: skip frozen objects
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                if (obj.syncScale) movedScaledCount++;
                else movedCount++;
            }

            // Send non-scaled objects (0x01)
            if (movedCount > 0)
            {
                int size = 3 + movedCount * Protocol.BYTES_PER_ENTITY;
                byte[] buf = new byte[size];
                buf[0] = 0x01;
                WriteU16(buf, 1, (ushort)movedCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (obj.IsClaimedByRemote || obj.syncScale) continue;
                    if (!obj.IsAOIActive) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles);
                    obj.MarkSent();
                }
                SendBinary(buf);
            }

            // Send scaled objects (0x05)
            if (movedScaledCount > 0)
            {
                int size = 3 + movedScaledCount * Protocol.BYTES_PER_ENTITY_SCALED;
                byte[] buf = new byte[size];
                buf[0] = Protocol.MSG_POSITION_UPDATE_SCALED;
                WriteU16(buf, 1, (ushort)movedScaledCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (obj.IsClaimedByRemote || !obj.syncScale) continue;
                    if (!obj.IsAOIActive) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = Protocol.WriteEntityScaled(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                    obj.MarkSent();
                }
                SendBinary(buf);
            }
        }

        /// <summary>
        /// Send position updates for objects this client has claimed (interaction).
        /// Works for both master and spectator.
        /// </summary>
        private void SendInteractionUpdates()
        {
            int movedCount = 0;
            int movedScaledCount = 0;
            foreach (var obj in _objects)
            {
                if (!obj.IsClaimedLocally) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                if (obj.syncScale) movedScaledCount++;
                else movedCount++;
            }

            // Non-scaled interaction updates (0x03)
            if (movedCount > 0)
            {
                int size = 3 + movedCount * Protocol.BYTES_PER_ENTITY;
                byte[] buf = new byte[size];
                buf[0] = 0x03;
                WriteU16(buf, 1, (ushort)movedCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (!obj.IsClaimedLocally || obj.syncScale) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles);
                    obj.MarkSent();
                }
                SendBinary(buf);
            }

            // Scaled interaction updates (0x09)
            if (movedScaledCount > 0)
            {
                int size = 3 + movedScaledCount * Protocol.BYTES_PER_ENTITY_SCALED;
                byte[] buf = new byte[size];
                buf[0] = 0x09;
                WriteU16(buf, 1, (ushort)movedScaledCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (!obj.IsClaimedLocally || !obj.syncScale) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = Protocol.WriteEntityScaled(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                    obj.MarkSent();
                }
                SendBinary(buf);
            }
        }

        /// <summary>
        /// Send a single object's position as an interaction update (used on release).
        /// </summary>
        public void SendInteractionUpdate(NetworkObject obj)
        {
            if (obj.syncScale)
            {
                byte[] buf = new byte[3 + Protocol.BYTES_PER_ENTITY_SCALED];
                buf[0] = 0x09;
                WriteU16(buf, 1, 1);
                Protocol.WriteEntityScaled(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                obj.MarkSent();
                SendBinary(buf);
            }
            else
            {
                byte[] buf = new byte[3 + Protocol.BYTES_PER_ENTITY];
                buf[0] = 0x03;
                WriteU16(buf, 1, 1);
                WriteEntityCompact(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles);
                obj.MarkSent();
                SendBinary(buf);
            }
        }

        /// <summary>
        /// Send a claim message for an object (called by NetworkObject.Grab()).
        /// </summary>
        public void SendClaim(ushort objectId)
        {
            // [0xFF][0x05][u16 object_id][u16 client_id]
            byte[] buf = new byte[6];
            buf[0] = 0xFF;
            buf[1] = 0x05;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinary(buf);
        }

        /// <summary>
        /// Send a release message for an object (called by NetworkObject.Release()).
        /// </summary>
        public void SendRelease(ushort objectId)
        {
            // [0xFF][0x06][u16 object_id][u16 client_id]
            byte[] buf = new byte[6];
            buf[0] = 0xFF;
            buf[1] = 0x06;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinary(buf);
        }

        private void HandlePositionUpdates(byte[] data, int length)
        {
            if (IsMaster || !_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot;
                offset = ReadEntityCompact(data, offset, out localId, out pos, out rot);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (_snapFramesRemaining > 0)
                        obj.SnapTo(pos, rot);
                    else
                        obj.ApplyUpdate(pos, rot);
                }
            }

            if (_snapFramesRemaining > 0)
                _snapFramesRemaining--;
        }

        /// <summary>
        /// Handle interaction updates from remote clients (0x03).
        /// Any client applies these — master sees spectator interactions, spectators see others'.
        /// </summary>
        private void HandleInteractionUpdates(byte[] data, int length)
        {
            if (!_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot;
                offset = ReadEntityCompact(data, offset, out localId, out pos, out rot);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (!obj.IsClaimedLocally)
                    {
                        obj.ApplyUpdate(pos, rot);
                    }
                }
            }
        }

        // =====================================================================
        // Scaled Position Updates (0x05) — objects with syncScale enabled
        // =====================================================================

        private void HandleScaledPositionUpdates(byte[] data, int length)
        {
            if (IsMaster || !_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_SCALED <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot, scale;
                offset = Protocol.ReadEntityScaled(data, offset, out localId, out pos, out rot, out scale);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (_snapFramesRemaining > 0)
                        obj.SnapTo(pos, rot, scale);
                    else
                        obj.ApplyUpdate(pos, rot, scale);
                }
            }
        }

        // =====================================================================
        // Scaled Interaction Updates (0x09)
        // =====================================================================

        private void HandleScaledInteractionUpdates(byte[] data, int length)
        {
            if (!_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_SCALED <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot, scale;
                offset = Protocol.ReadEntityScaled(data, offset, out localId, out pos, out rot, out scale);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (!obj.IsClaimedLocally)
                        obj.ApplyUpdate(pos, rot, scale);
                }
            }
        }

        // =====================================================================
        // Variable Updates (0x04) — custom variables
        // =====================================================================

        private void HandleVariableUpdate(byte[] data, int length)
        {
            if (length < 4) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));
            int varCount = data[3];

            if (_objectMap.TryGetValue(objId, out var obj))
            {
                var netVars = obj.GetComponent<NetworkVariables>();
                if (netVars != null)
                {
                    netVars.ApplyRemoteUpdate(data, 4, varCount);
                }
            }
        }

        // =====================================================================
        // Events (0x06) — fire-and-forget RPCs
        // =====================================================================

        private void HandleEvent(byte[] data, int length)
        {
            if (length < 4) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (_objectMap.TryGetValue(objId, out var obj))
            {
                var netEvents = obj.GetComponent<NetworkEvents>();
                if (netEvents != null)
                {
                    netEvents.ApplyRemoteEvent(data, 3, length);
                }
            }
        }

        // =====================================================================
        // Animator State (0x07)
        // =====================================================================

        private void HandleAnimatorState(byte[] data, int length)
        {
            if (length < 10) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (_objectMap.TryGetValue(objId, out var obj))
            {
                var netAnim = obj.GetComponent<NetworkAnimator>();
                if (netAnim != null)
                {
                    netAnim.ApplyRemoteState(data, 3);
                }
            }
        }

        // =====================================================================
        // Rigidbody Updates (0x08) — velocity sync
        // =====================================================================

        private void HandleRigidbodyUpdate(byte[] data, int length)
        {
            if (length < 15) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (_objectMap.TryGetValue(objId, out var obj))
            {
                var netRb = obj.GetComponent<NetworkRigidbody>();
                if (netRb != null)
                {
                    netRb.HandleRigidbodyUpdate(data, 3);
                }
            }
        }

        // =====================================================================
        // Snapshot (0x02) — full state for new clients
        // =====================================================================
        // Compact format: [0x02][u16 count][per entity: u16 id + 6B half-pos + 6B u16-rot = 14B]

        private void SendSnapshot()
        {
            if (_objects.Count == 0) return;

            // Split objects into scaled and non-scaled
            // Snapshots include ALL objects (not AOI-filtered) so late joiners get full state
            int normalCount = 0;
            int scaledCount = 0;
            foreach (var obj in _objects)
            {
                if (obj.syncScale) scaledCount++;
                else normalCount++;
            }

            // Send non-scaled snapshot (0x02)
            if (normalCount > 0)
            {
                int size = 3 + normalCount * Protocol.BYTES_PER_ENTITY;
                byte[] buf = new byte[size];
                buf[0] = 0x02;
                WriteU16(buf, 1, (ushort)normalCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (obj.syncScale) continue;
                    offset = WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles);
                }
                SendBinary(buf);
            }

            // Send scaled snapshot (0x05 reused — same handler)
            if (scaledCount > 0)
            {
                int size = 3 + scaledCount * Protocol.BYTES_PER_ENTITY_SCALED;
                byte[] buf = new byte[size];
                buf[0] = Protocol.MSG_POSITION_UPDATE_SCALED;
                WriteU16(buf, 1, (ushort)scaledCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (!obj.syncScale) continue;
                    offset = Protocol.WriteEntityScaled(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                }
                SendBinary(buf);
            }

            Debug.Log($"[Net] Sending snapshot: {normalCount}+{scaledCount} scaled objects");
        }

        private void HandleSnapshot(byte[] data, int length)
        {
            if (IsMaster || !_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;
            int applied = 0;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot;
                offset = ReadEntityCompact(data, offset, out localId, out pos, out rot);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    obj.SnapTo(pos, rot);
                    applied++;
                }
            }

            // Snap next few position updates too (catch up to current state fast)
            _snapFramesRemaining = SNAP_FRAMES_AFTER_SNAPSHOT;
            Debug.Log($"[Net] Snapshot applied: {applied}/{count} objects");
        }

        // =====================================================================
        // Send Helper
        // =====================================================================

        private void SendBinary(byte[] data)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            MessagesSent++;
            BytesSent += data.Length;
            _sendQueue.Enqueue(data);
        }

        /// <summary>
        /// Coroutine that drains the send queue one message at a time,
        /// awaiting each SendAsync before starting the next. This prevents
        /// concurrent SendAsync calls which cause silent message loss.
        /// </summary>
        private IEnumerator SendLoop()
        {
            while (IsConnected && _ws.State == WebSocketState.Open)
            {
                if (_sendQueue.TryDequeue(out byte[] data))
                {
                    var seg = new ArraySegment<byte>(data);
                    var sendTask = _ws.SendAsync(seg, WebSocketMessageType.Binary, true, _cts.Token);
                    while (!sendTask.IsCompleted)
                        yield return null;

                    if (sendTask.IsFaulted)
                    {
                        Debug.LogWarning($"[Net] SendAsync failed: {sendTask.Exception?.InnerException?.Message}");
                        IsConnected = false;
                        yield break;
                    }
                }
                else
                {
                    yield return null;
                }
            }
        }

        /// <summary>Public send for components like NetworkVariables.</summary>
        public void SendBinaryPublic(byte[] data) => SendBinary(data);

        // =====================================================================
        // Binary Helpers (delegates to Protocol for shared encoding)
        // =====================================================================

        private static void WriteU16(byte[] buf, int offset, ushort value)
            => Protocol.WriteU16(buf, offset, value);

        private static void WriteFloat(byte[] buf, int offset, float value)
            => Protocol.WriteFloat(buf, offset, value);

        private static int WriteEntityCompact(byte[] buf, int offset, ushort id, Vector3 pos, Vector3 rot)
            => Protocol.WriteEntityCompact(buf, offset, id, pos, rot);

        private static int ReadEntityCompact(byte[] buf, int offset, out ushort id, out Vector3 pos, out Vector3 rot)
            => Protocol.ReadEntityCompact(buf, offset, out id, out pos, out rot);
    }
}
