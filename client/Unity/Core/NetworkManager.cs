using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Core NetworkManager — connection lifecycle, object registration, send queue.
    ///
    /// Architecture: server-authoritative. The Rust relay server runs Rapier3D
    /// physics and broadcasts authoritative positions (0x0B). Clients only send:
    ///   - SCENE_INIT (one-time scene description, sent by first connecting client)
    ///   - Held-object updates (when the local player is grabbing something)
    ///   - Player position (for server-side AOI filtering)
    ///   - Events, variables, animator state
    ///
    /// Split into partial classes:
    ///   NetworkManager.Receiver.cs  — incoming message handlers
    ///   NetworkManager.Sender.cs    — outgoing held-object/scene-init/player-pos senders
    /// </summary>
    public partial class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

        [Header("Connection")]
        public string serverUrl = "ws://localhost:9090";
        [Tooltip("Room ID — clients in the same room see each other. Leave empty for 'default'.")]
        public string roomId = "";
        public float reconnectDelay = 3f;

        [Header("Sync")]
        [Tooltip("How many times per second to send held-object position updates")]
        public int sendRate = 60;
        [Tooltip("How many times per second to broadcast the local player's position to the server (AOI)")]
        public int playerPosSendRate = 10;
        [Tooltip("Minimum movement (meters) before a held object is included in an update")]
        public float positionThreshold = 0.01f;
        [Tooltip("Minimum rotation (degrees) before a held object is included in an update")]
        public float rotationThreshold = 1f;

        [Header("Local Player")]
        [Tooltip("Your player transform. If set, this is used for server-side AOI and push detection. Leave empty to auto-detect Somnium Space rigs.")]
        public Transform localPlayer;
        [Tooltip("Auto-attach PlayerPushDetector for one-shot MSG_PUSH_IMPULSE on contact. Off by default — the server's Rapier player capsule already handles continuous push physics from your movement. Only enable if you specifically want an extra instantaneous impulse on first contact.")]
        public bool autoAttachPushDetector = false;

        [Header("Runtime Spawning")]
        [Tooltip("Prefabs that can be spawned at runtime. List order = prefab index — MUST match on every client.")]
        public List<GameObject> networkedPrefabs = new List<GameObject>();

        /// <summary>
        /// Fires whenever a runtime NetworkObject is spawned (local or remote).
        /// Callback receives the spawned object and its prefab index.
        /// </summary>
        public event Action<NetworkObject, int> OnObjectSpawned;

        /// <summary>Fires whenever a runtime object is despawned.</summary>
        public event Action<NetworkObject> OnObjectDespawned;

        // State
        public bool IsConnected { get; private set; }
        public ushort ClientId { get; private set; }
        public bool IsNetworkReady { get; private set; }
        /// <summary>True once the server has broadcast CTRL_PHYSICS_READY. Always true in the steady state.</summary>
        public bool IsPhysicsReady { get; private set; }

        /// <summary>
        /// Monotonic clock shared by all clients in this room, in seconds.
        /// Initialised from the server's unix_millis at welcome (accurate to
        /// ±RTT/2), then continually refined by NTP-style ping/pong exchanges —
        /// whenever we see a smaller RTT we use that sample's offset, which has
        /// less one-way-delay error baked in. All clients converge on the same
        /// ServerTime so deterministic components like NetworkNPC and
        /// NetworkWaypoint produce matching positions. Zero until welcome.
        /// </summary>
        public double ServerTime => IsNetworkReady ? Time.timeAsDouble - _serverTimeOffset : 0.0;
        private double _serverTimeOffset;
        internal void SetServerTimeOffsetSeconds(double offset) => _serverTimeOffset = offset;
        internal double ServerTimeOffsetSeconds => _serverTimeOffset;
        /// <summary>Smallest RTT seen since connect, in seconds. Used to prefer the cleanest time-sync sample.</summary>
        internal double BestRttSeconds = double.MaxValue;
        private float _nextTimePingTime;

        // Events
        /// <summary>Fired when networking is ready (connected + client ID assigned). Safe to use network features.</summary>
        public event Action OnNetworkReady;
        /// <summary>Fired when connection is lost.</summary>
        public event Action OnDisconnected;

        // All network objects sorted deterministically — index = local_id.
        private List<NetworkObject> _objects = new List<NetworkObject>();
        private bool _sceneReady;
        private bool _sceneInitSent;

        // =====================================================================
        // Queued send message
        // =====================================================================

        private readonly struct QueuedMessage
        {
            public readonly byte[] Data;
            public readonly int    Length;
            public readonly bool   Pooled;
            public QueuedMessage(byte[] data, int length, bool pooled = false)
            { Data = data; Length = length; Pooled = pooled; }
        }

        // WebSocket
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly byte[] _recvBuffer = new byte[65536];
        private readonly byte[] _recvAccumBuf = new byte[512 * 1024];
        private int _recvAccumLen = 0;
        private readonly ConcurrentQueue<QueuedMessage> _sendQueue = new ConcurrentQueue<QueuedMessage>();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private float _sendInterval;
        private float _playerPosInterval;
        private float _nextSendTime;
        private float _nextPlayerPosTime;
        private float _nextPushDetectorCheckTime;

        // Resolved local player transform. Priority: `localPlayer` inspector
        // override > SetLocalPlayer() call > auto-detected Somnium rig.
        private Transform _localPlayerTransform;

        // Sequence numbers — track per-sender to discard stale 0x0B packets
        private Dictionary<ushort, ushort> _receivedSequences = new Dictionary<ushort, ushort>();

        // Stats
        public static int MessagesSent;
        public static int MessagesReceived;
        public static int BytesSent;
        public static int BytesReceived;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            _sendInterval      = 1f / sendRate;
            _playerPosInterval = 1f / Mathf.Max(1, playerPosSendRate);
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
            if (!_sceneReady)
            {
                CollectAndSortObjects();
                _sceneReady = true;
                Debug.Log($"[Net] Scene ready. {_objects.Count} objects registered.");
                // If we're already connected and waiting to bootstrap, send SCENE_INIT now.
                if (IsConnected && !_sceneInitSent && _needsSceneInit)
                {
                    SendSceneInit();
                    _sceneInitSent = true;
                }
            }

            if (!IsConnected) return;

            float dt = Time.deltaTime;
            float t  = Time.time;
            for (int i = 0; i < _objects.Count; i++)
                _objects[i].NetworkUpdate(dt, t);

            // Resolve the local player transform once per half-second until we find
            // something — handles async-spawned Somnium rigs without scanning every frame.
            if (_localPlayerTransform == null && Time.time >= _nextPushDetectorCheckTime)
            {
                _nextPushDetectorCheckTime = Time.time + 0.5f;
                ResolveLocalPlayer();
            }

            // Send held-object updates (locally claimed objects only — the server owns everything else).
            if (Time.time >= _nextSendTime)
            {
                _nextSendTime = Time.time + _sendInterval;
                SendInteractionUpdates();
            }

            // Send local player position for server-side AOI.
            if (Time.time >= _nextPlayerPosTime)
            {
                _nextPlayerPosTime = Time.time + _playerPosInterval;
                SendPlayerPosition();
            }

            // Periodic NTP-style ping to tighten the ServerTime offset.
            // Ping every 1 s so clock drift / NTP adjustments on the server
            // converge quickly across clients. An EMA in HandleTimePong smooths
            // out per-sample RTT noise, so 1 Hz is stable, not jittery.
            if (IsNetworkReady && Time.time >= _nextTimePingTime)
            {
                _nextTimePingTime = Time.time + 1f;
                SendTimePing();
            }
        }

        private void SendTimePing()
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            ulong clientMs = (ulong)(Time.timeAsDouble * 1000.0);
            byte[] buf = new byte[9];
            buf[0] = Protocol.MSG_TIME_PING;
            buf[1] = (byte)(clientMs       & 0xFF);
            buf[2] = (byte)((clientMs >> 8)  & 0xFF);
            buf[3] = (byte)((clientMs >> 16) & 0xFF);
            buf[4] = (byte)((clientMs >> 24) & 0xFF);
            buf[5] = (byte)((clientMs >> 32) & 0xFF);
            buf[6] = (byte)((clientMs >> 40) & 0xFF);
            buf[7] = (byte)((clientMs >> 48) & 0xFF);
            buf[8] = (byte)((clientMs >> 56) & 0xFF);
            SendBinary(buf);
        }

        // =====================================================================
        // Local player resolution + push detector auto-attach
        // =====================================================================

        /// <summary>
        /// Resolve the local player transform. Priority:
        ///   1. `localPlayer` inspector field set by the user
        ///   2. A transform handed in via <see cref="SetLocalPlayer"/>
        ///      (<see cref="SomniumBridgeIntegration"/> does this)
        ///   3. Somnium-Space-specific auto-detection: look for the body under an
        ///      "Original Player:" root GameObject.
        /// </summary>
        private void ResolveLocalPlayer()
        {
            if (localPlayer != null)
            {
                _localPlayerTransform = localPlayer;
                AutoAttachPushDetector(localPlayer.gameObject);
                return;
            }

            // Somnium-specific fallback — look for SomniumBridge's player rig.
            foreach (var cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                string n = cc.gameObject.name;
                if (n != "NonXR.Body" && n != "XR.Body") continue;

                Transform root = cc.transform;
                while (root.parent != null) root = root.parent;
                if (!root.name.StartsWith("Original Player:")) continue;

                _localPlayerTransform = cc.transform;
                AutoAttachPushDetector(cc.gameObject);
                return;
            }
        }

        private void AutoAttachPushDetector(GameObject target)
        {
            if (!autoAttachPushDetector) return;
            if (target == null) return;
            if (target.GetComponent<PlayerPushDetector>() != null) return;
            // PlayerPushDetector uses OnCollisionEnter, which requires a Collider.
            if (target.GetComponent<Collider>() == null) return;
            target.AddComponent<PlayerPushDetector>();
            Debug.Log($"[Net] Push detector attached to {target.name}");
        }

        // =====================================================================
        // Object Registration
        // =====================================================================

        /// <summary>
        /// Collect all NetworkObjects, sort deterministically, assign local IDs.
        /// Every client runs this the same way so IDs match on all clients and the server.
        /// </summary>
        private void CollectAndSortObjects()
        {
            _objects.Clear();

            var all = FindObjectsByType<NetworkObject>(FindObjectsSortMode.None);

            Array.Sort(all, (a, b) =>
            {
                int cmp = string.Compare(a.entityType, b.entityType, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
                cmp = string.Compare(a.gameObject.name, b.gameObject.name, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
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
                all[i].LocalId = (ushort)i;
                _objects.Add(all[i]);
            }
        }

        private NetworkObject GetObject(ushort id)
            => id < _objects.Count ? _objects[id] : null;

        // =====================================================================
        // Public API: allow other scripts to tell us about the local player.
        // =====================================================================

        /// <summary>
        /// Register the local player transform so the network layer can broadcast
        /// player position for server-side AOI. Called automatically by the
        /// push detector, or manually by SomniumBridgeIntegration.
        /// </summary>
        public void SetLocalPlayer(Transform t) => _localPlayerTransform = t;

        /// <summary>
        /// The currently-resolved local player transform (inspector override,
        /// SetLocalPlayer call, or auto-detected Somnium rig). Null until
        /// one of those paths has succeeded. Useful for NPC AI that wants
        /// to chase/aggro against the local player.
        /// </summary>
        public Transform LocalPlayer => _localPlayerTransform;

        // =====================================================================
        // Connection
        // =====================================================================

        private bool _needsSceneInit;

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

            StartCoroutine(ReceiveLoop());
            _ = SendLoopAsync(_ws, _cts).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Debug.LogWarning($"[Net] Send loop error: {t.Exception?.InnerException?.Message}");
            }, TaskContinuationOptions.OnlyOnFaulted);

            while (IsConnected && _ws.State == WebSocketState.Open)
                yield return null;

            IsConnected = false;
            IsNetworkReady = false;
            IsPhysicsReady = false;
            _sceneInitSent = false;
            _needsSceneInit = false;
            // Keep _localPlayerTransform — it's a scene reference, still valid on
            // reconnect. No need to re-resolve.
            // Clear per-sender sequence dedup — on reconnect the server starts at
            // seq=1, which would otherwise be rejected as "older" than the cached
            // high value from the previous session and silently drop updates.
            _receivedSequences.Clear();
            // Discard the RTT sample history — new session, new network path.
            BestRttSeconds = double.MaxValue;
            _nextTimePingTime = 0;
            _timeSyncInitialised = false;
            // Release any objects we were holding so reconnect doesn't leave the
            // client stuck believing it still owns objects the server has long
            // since returned to Dynamic.
            for (int i = 0; i < _objects.Count; i++)
                _objects[i].ResetClaimStateOnDisconnect();
            NetworkPersistence.ClearPending();

            while (_sendQueue.TryDequeue(out QueuedMessage stale))
                if (stale.Pooled) ArrayPool<byte>.Shared.Return(stale.Data);

            Debug.Log("[Net] Disconnected.");
            OnDisconnected?.Invoke();
        }

        // =====================================================================
        // Receive / Send loops
        // =====================================================================

        private IEnumerator ReceiveLoop()
        {
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
                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                {
                    BytesReceived += result.Count;

                    if (result.EndOfMessage && _recvAccumLen == 0)
                    {
                        MessagesReceived++;
                        ProcessBinary(_recvBuffer, result.Count);
                    }
                    else
                    {
                        int space = _recvAccumBuf.Length - _recvAccumLen;
                        if (result.Count <= space)
                        {
                            System.Buffer.BlockCopy(_recvBuffer, 0, _recvAccumBuf, _recvAccumLen, result.Count);
                            _recvAccumLen += result.Count;
                        }
                        else
                        {
                            Debug.LogError($"[Net] Incoming message exceeds accumulation buffer ({_recvAccumLen + result.Count} bytes), discarding");
                            _recvAccumLen = 0;
                        }

                        if (result.EndOfMessage)
                        {
                            if (_recvAccumLen > 0)
                            {
                                MessagesReceived++;
                                ProcessBinary(_recvAccumBuf, _recvAccumLen);
                            }
                            _recvAccumLen = 0;
                        }
                    }
                }
            }
        }

        private async Task SendLoopAsync(ClientWebSocket ws, CancellationTokenSource cts)
        {
            while (ws.State == WebSocketState.Open)
            {
                try { await _sendSignal.WaitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                while (_sendQueue.TryDequeue(out QueuedMessage msg))
                {
                    bool breakLoop = false;
                    if (ws.State == WebSocketState.Open)
                    {
                        try
                        {
                            await ws.SendAsync(
                                new ArraySegment<byte>(msg.Data, 0, msg.Length),
                                WebSocketMessageType.Binary, true, cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { breakLoop = true; }
                        catch (Exception)                  { breakLoop = true; }
                    }
                    if (msg.Pooled) ArrayPool<byte>.Shared.Return(msg.Data);
                    if (breakLoop) break;
                }
            }
        }

        // =====================================================================
        // Send Helpers
        // =====================================================================

        private void SendBinary(byte[] data)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            _sendQueue.Enqueue(new QueuedMessage(data, data.Length, pooled: false));
            _sendSignal.Release();
            MessagesSent++;
            BytesSent += data.Length;
        }

        /// <summary>
        /// Enqueues a slice of a pooled ArrayPool buffer for sending.
        /// Ownership transfers to the send loop, which returns it after the send.
        /// </summary>
        private void SendBinaryPooled(byte[] pooledBuf, int length)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                ArrayPool<byte>.Shared.Return(pooledBuf);
                return;
            }
            _sendQueue.Enqueue(new QueuedMessage(pooledBuf, length, pooled: true));
            _sendSignal.Release();
            MessagesSent++;
            BytesSent += length;
        }

        public void SendBinaryPublic(byte[] data) => SendBinary(data);

        // =====================================================================
        // Binary Helpers (delegates to Protocol)
        // =====================================================================

        private static void WriteU16(byte[] buf, int offset, ushort value)
            => Protocol.WriteU16(buf, offset, value);
    }
}
