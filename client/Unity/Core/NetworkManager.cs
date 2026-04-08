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
    /// Core NetworkManager — connection lifecycle, object registration, and send queue.
    /// Split into partial classes:
    ///   NetworkManager.Receiver.cs  — incoming message handlers
    ///   NetworkManager.Sender.cs    — outgoing position/interaction/snapshot senders
    ///   NetworkManager.Authority.cs — StateAuthority assignment and redistribution
    ///   NetworkManager.AOI.cs       — Area of Interest logic and player position tracking
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
        [Tooltip("How many times per second to send position updates (transforms, grabbed objects)")]
        public int sendRate = 60;
        [Tooltip("How many times per second to send rigidbody updates (dead reckoning fills the gaps)")]
        public int rigidbodySendRate = 20;
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

        // All network objects sorted deterministically — index = local_id.
        // IDs are sequential (0…N-1), so _objects[id] is O(1) direct access — no dictionary needed.
        private List<NetworkObject> _objects = new List<NetworkObject>();
        private bool _sceneReady;

        // =====================================================================
        // Queued send message — avoids per-send heap allocation.
        // Pooled=true → send loop returns Data to ArrayPool after the send completes.
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
        // Accumulation buffer for fragmented WebSocket messages (messages > 65536 bytes
        // arrive in multiple ReceiveAsync calls with EndOfMessage=false until the last chunk).
        // Sized to the server's snapshot limit so no valid message is ever dropped.
        private readonly byte[] _recvAccumBuf = new byte[512 * 1024];
        private int _recvAccumLen = 0;
        private readonly ConcurrentQueue<QueuedMessage> _sendQueue = new ConcurrentQueue<QueuedMessage>();
        // Signals the async send loop when new data is available — avoids busy-waiting
        // and removes the 1-frame-per-send delay that coroutines introduced.
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private float _sendInterval;
        private float _rbSendInterval;
        private float _nextSendTime;
        private float _nextRbSendTime;
        private bool _pushDetectorAttached;
        private float _nextPushDetectorCheckTime;

        // After snapshot, snap first N updates instead of interpolating

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
        [Tooltip("Override the local player transform used for AOI. If unset, auto-detected (Somnium) or falls back to Camera.main.")]
        public Transform localPlayerTransformOverride;

        // AOI state (used by NetworkManager.AOI.cs)
        private float _nextAOICheckTime;
        private Dictionary<ushort, Vector3> _playerPositions = new Dictionary<ushort, Vector3>();
        private Dictionary<ushort, float> _playerLastSeen = new Dictionary<ushort, float>();
        private Transform _localPlayerTransform;
        private bool _aoiTransformWarningLogged;
        private float _nextPlayerPosSendTime;
        private const float PLAYER_POS_SEND_RATE = 5f; // 5Hz
        private const float PLAYER_TIMEOUT = 5f; // Remove player after 5s of no updates

        // Sequence numbers — track per-sender to discard stale 0x0B packets
        private Dictionary<ushort, ushort> _receivedSequences = new Dictionary<ushort, ushort>();
        private ushort _sendSequence;

        // Reusable scratch collections for AOI and authority (avoid per-tick allocations)
        private readonly List<Vector3> _aoiPlayerPosScratch          = new List<Vector3>(16);
        private readonly List<(ushort, Vector3)> _authorityPlayersScratch = new List<(ushort, Vector3)>(16);
        private readonly Dictionary<ushort, List<ushort>> _newAssignmentsScratch = new Dictionary<ushort, List<ushort>>();

        // =====================================================================
        // Integration hooks — set these in your world's Start() to plug in
        // a custom identity/transform source (e.g. SomniumBridgeIntegration.cs)
        // =====================================================================

        /// <summary>
        /// Override the local player transform used for AOI and player position.
        /// Set this to: () => SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body?.Head
        /// </summary>
        public static System.Func<Transform> LocalPlayerTransformProvider;

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
            _sendInterval   = 1f / sendRate;
            _rbSendInterval = 1f / rigidbodySendRate;
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

            // Centralized object update — replaces 1000 separate MonoBehaviour.Update()
            // dispatches. Each MonoBehaviour.Update() call crosses the native/managed
            // boundary; iterating a List<T> in a single loop is far cheaper.
            float dt = Time.deltaTime;
            float t  = Time.time;
            for (int i = 0; i < _objects.Count; i++)
                _objects[i].NetworkUpdate(dt, t);

            // Attach push detector — retry every 0.5s until found (not every frame)
            if (!_pushDetectorAttached && Time.time >= _nextPushDetectorCheckTime)
            {
                _nextPushDetectorCheckTime = Time.time + 0.5f;
                AttachPushDetector();
            }

            // Send position updates at configured rate (60Hz for transforms/grabbed)
            if (Time.time >= _nextSendTime)
            {
                _nextSendTime = Time.time + _sendInterval;
                SendAuthorityPositionUpdates(rigidbodies: false);
                SendInteractionUpdates();
            }

            // Send rigidbody updates at lower rate (20Hz — dead reckoning fills gaps)
            if (Time.time >= _nextRbSendTime)
            {
                _nextRbSendTime = Time.time + _rbSendInterval;
                SendAuthorityPositionUpdates(rigidbodies: true);
            }

            // Periodic full snapshot to correct drift (master only).
            // Always send — even when nothing has moved. Spectators accumulate dead-reckoning
            // error over time and the resync is the only mechanism to correct it. Gating on
            // anyMoved meant the resync never fired once objects settled, so drift was permanent.
            if (IsMaster && resyncInterval > 0 && Time.time >= _nextResyncTime)
            {
                _nextResyncTime = Time.time + resyncInterval;
                SendSnapshot();
            }

            // AOI + authority: master recalculates periodically
            if (IsMaster && enableAOI && Time.time >= _nextAOICheckTime)
            {
                _nextAOICheckTime = Time.time + (1f / aoiCheckRate);
                UpdateAOI();
                RecalculateAuthority();
            }

            // Spectators send their position to master for AOI
            if (!IsMaster && IsNetworkReady && enableAOI && Time.time >= _nextPlayerPosSendTime)
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
            foreach (var cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                string name = cc.gameObject.name;
                if (name != "NonXR.Body" && name != "XR.Body") continue;

                Transform root = cc.transform;
                while (root.parent != null) root = root.parent;
                if (!root.name.StartsWith("Original Player:")) continue;

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

        // O(1) object lookup — IDs are sequential so _objects[id] is a direct index.
        private NetworkObject GetObject(ushort id)
            => id < _objects.Count ? _objects[id] : null;

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
            // Send loop runs as an async Task so WebSocket sends complete as fast as the
            // network allows — no longer limited to one send per Unity frame (coroutines
            // yielded for a full 16ms frame after every SendAsync, stacking up to 50ms
            // of extra latency at 1000-object send rates).
            _ = SendLoopAsync(_ws, _cts).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Debug.LogWarning($"[Net] Send loop error: {t.Exception?.InnerException?.Message}");
            }, TaskContinuationOptions.OnlyOnFaulted);

            while (IsConnected && _ws.State == WebSocketState.Open)
                yield return null;

            IsConnected = false;
            IsMaster = false;
            IsNetworkReady = false;
            _pushDetectorAttached = false;
            NetworkPersistence.ClearPending();

            // Drain send queue and return any pooled buffers so the pool isn't leaked
            // when the connection closes and the send loop exits.
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
                        // Fast path: complete message arrived in one chunk (the common case)
                        MessagesReceived++;
                        ProcessBinary(_recvBuffer, result.Count);
                    }
                    else
                    {
                        // Fragmented message: accumulate until EndOfMessage=true
                        int space = _recvAccumBuf.Length - _recvAccumLen;
                        if (result.Count <= space)
                        {
                            System.Buffer.BlockCopy(_recvBuffer, 0, _recvAccumBuf, _recvAccumLen, result.Count);
                            _recvAccumLen += result.Count;
                        }
                        else
                        {
                            Debug.LogError($"[Net] Incoming message exceeds accumulation buffer ({_recvAccumLen + result.Count} bytes), discarding");
                            _recvAccumLen = 0; // reset so next message starts clean
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

        // SendLoop coroutine replaced by SendLoopAsync (below).

        /// <summary>
        /// Async send loop — runs on the thread pool so WebSocket sends complete at
        /// network speed rather than at Unity's frame rate. The coroutine approach
        /// yielded for a full frame (≥16ms) after every SendAsync, which meant that
        /// at 1000-object send rates (2–3 messages/tick) messages piled up 33–50ms
        /// deep before being flushed, arriving at spectators as position jumps.
        /// Captures its own ws/cts so reconnects don't corrupt a running loop.
        /// </summary>
        private async Task SendLoopAsync(ClientWebSocket ws, CancellationTokenSource cts)
        {
            while (ws.State == WebSocketState.Open)
            {
                try { await _sendSignal.WaitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                // Drain everything queued since the last signal — multiple messages can
                // arrive close together (rigidbody + transform + interaction in the same
                // frame) and we want to send them all without waiting for the next signal.
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
        // Send Helper
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
        /// Takes ownership of the buffer — the send loop returns it to the pool after the
        /// send completes.  Callers must NOT return the buffer themselves after this call.
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
