using System;
using System.Collections;
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
        public string serverUrl = "wss://simple-sync.bentlybro.com";
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

        // All network objects sorted deterministically — index = local_id
        private List<NetworkObject> _objects = new List<NetworkObject>();
        private Dictionary<ushort, NetworkObject> _objectMap = new Dictionary<ushort, NetworkObject>();
        private bool _sceneReady;

        // WebSocket
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly byte[] _recvBuffer = new byte[65536];
        private float _sendInterval;
        private float _nextSendTime;
        private bool _pushDetectorAttached;

        // After snapshot, snap first N updates instead of interpolating
        private int _snapFramesRemaining;
        private const int SNAP_FRAMES_AFTER_SNAPSHOT = 3;

        // Periodic resync snapshot (master only)
        [Header("Resync")]
        [Tooltip("Seconds between full snapshots to prevent drift (0 = disabled)")]
        public float resyncInterval = 5f;
        private float _nextResyncTime;

        // Stats (public for NetworkStats overlay)
        public static int MessagesSent;
        public static int MessagesReceived;
        public static int BytesSent;
        public static int BytesReceived;

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
            // Only send if at least one object has moved since last snapshot
            if (IsMaster && resyncInterval > 0 && Time.time >= _nextResyncTime)
            {
                _nextResyncTime = Time.time + resyncInterval;
                bool anyMoved = false;
                foreach (var obj in _objects)
                {
                    if (obj.HasMoved(positionThreshold, rotationThreshold))
                    {
                        anyMoved = true;
                        break;
                    }
                }
                if (anyMoved)
                    SendSnapshot();
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
            Debug.Log($"[Net] Connecting to {serverUrl}...");
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            var connectTask = _ws.ConnectAsync(new Uri(serverUrl), _cts.Token);
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

            // Start receive loop as coroutine
            StartCoroutine(ReceiveLoop());

            // Wait until disconnected
            while (IsConnected && _ws.State == WebSocketState.Open)
                yield return null;

            IsConnected = false;
            IsMaster = false;
            Debug.Log("[Net] Disconnected.");
        }

        // =====================================================================
        // Receive
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
                    MessagesReceived++;
                    BytesReceived += result.Count;
                    ProcessBinary(_recvBuffer, result.Count);
                }
            }
        }

        private void ProcessBinary(byte[] data, int length)
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
                    // Send initial snapshot so server caches it
                    SendSnapshot();
                    break;

                case 0x02: // You are spectator
                    ClientId = BitConverter.ToUInt16(data, 2);
                    IsMaster = false;
                    Debug.Log($"[Net] Assigned as SPECTATOR (client_{ClientId})");
                    EnablePhysicsOnAll(false);
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
            }
        }

        private void EnablePhysicsOnAll(bool enable)
        {
            foreach (var obj in _objects)
            {
                obj.SetPhysics(enable);
            }
        }

        // =====================================================================
        // Position Updates (0x01) — master sends, spectators receive
        // =====================================================================
        // Compact format: [0x01][u16 count][per entity: u16 id + 6B half-pos + 6B u16-rot = 14B]

        private void SendPositionUpdates()
        {
            if (_objects.Count == 0) return;

            int movedCount = 0;
            foreach (var obj in _objects)
            {
                if (obj.IsClaimedByRemote) continue;
                if (obj.HasMoved(positionThreshold, rotationThreshold))
                    movedCount++;
            }

            if (movedCount == 0) return;

            int size = 3 + movedCount * Protocol.BYTES_PER_ENTITY;
            byte[] buf = new byte[size];
            buf[0] = 0x01;
            WriteU16(buf, 1, (ushort)movedCount);

            int offset = 3;
            foreach (var obj in _objects)
            {
                if (obj.IsClaimedByRemote) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold))
                    continue;

                offset = WriteEntityCompact(buf, offset, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles);
                obj.MarkSent();
            }

            SendBinary(buf);
        }

        /// <summary>
        /// Send position updates for objects this client has claimed (interaction).
        /// Works for both master and spectator.
        /// </summary>
        private void SendInteractionUpdates()
        {
            int movedCount = 0;
            foreach (var obj in _objects)
            {
                if (!obj.IsClaimedLocally) continue;
                if (obj.HasMoved(positionThreshold, rotationThreshold))
                    movedCount++;
            }

            if (movedCount == 0) return;

            int size = 3 + movedCount * Protocol.BYTES_PER_ENTITY;
            byte[] buf = new byte[size];
            buf[0] = 0x03;
            WriteU16(buf, 1, (ushort)movedCount);

            int offset = 3;
            foreach (var obj in _objects)
            {
                if (!obj.IsClaimedLocally) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold))
                    continue;

                offset = WriteEntityCompact(buf, offset, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles);
                obj.MarkSent();
            }

            SendBinary(buf);
        }

        /// <summary>
        /// Send a single object's position as an interaction update (used on release).
        /// </summary>
        public void SendInteractionUpdate(NetworkObject obj)
        {
            byte[] buf = new byte[3 + Protocol.BYTES_PER_ENTITY];
            buf[0] = 0x03;
            WriteU16(buf, 1, 1);
            WriteEntityCompact(buf, 3, obj.LocalId,
                obj.transform.position, obj.transform.eulerAngles);
            obj.MarkSent();
            SendBinary(buf);
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

            int count = _objects.Count;
            int size = 3 + count * Protocol.BYTES_PER_ENTITY;
            byte[] buf = new byte[size];
            buf[0] = 0x02;
            WriteU16(buf, 1, (ushort)count);

            int offset = 3;
            foreach (var obj in _objects)
            {
                offset = WriteEntityCompact(buf, offset, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles);
            }

            Debug.Log($"[Net] Sending snapshot: {count} objects, {size} bytes");
            SendBinary(buf);
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
            var seg = new ArraySegment<byte>(data);
            _ws.SendAsync(seg, WebSocketMessageType.Binary, true, _cts.Token);
            MessagesSent++;
            BytesSent += data.Length;
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
