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

        // After snapshot, snap first N updates instead of interpolating
        private int _snapFramesRemaining;
        private const int SNAP_FRAMES_AFTER_SNAPSHOT = 3;

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

            if (!IsConnected || !IsMaster) return;

            // Send position updates at configured rate
            if (Time.time >= _nextSendTime)
            {
                _nextSendTime = Time.time + _sendInterval;
                SendPositionUpdates();
            }
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

            var all = FindObjectsOfType<NetworkObject>();

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
                case 0x01: // Position updates
                    HandlePositionUpdates(data, length);
                    break;

                case 0x02: // Snapshot
                    HandleSnapshot(data, length);
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
        // Format: [0x01][u16 count][per entity: u16 local_id + 3x float pos + 3x float rot]
        // = 3 + 26N bytes

        private void SendPositionUpdates()
        {
            if (_objects.Count == 0) return;

            // Count moved objects first
            int movedCount = 0;
            foreach (var obj in _objects)
            {
                if (obj.HasMoved(positionThreshold, rotationThreshold))
                    movedCount++;
            }

            if (movedCount == 0) return;

            // Pack: [0x01][u16 count][per: u16 id + 12B pos + 12B rot = 26B]
            int size = 3 + movedCount * 26;
            byte[] buf = new byte[size];
            buf[0] = 0x01;
            WriteU16(buf, 1, (ushort)movedCount);

            int offset = 3;
            foreach (var obj in _objects)
            {
                if (!obj.HasMoved(positionThreshold, rotationThreshold))
                    continue;

                WriteU16(buf, offset, obj.LocalId); offset += 2;
                var pos = obj.transform.position;
                var rot = obj.transform.eulerAngles;
                WriteFloat(buf, offset, pos.x); offset += 4;
                WriteFloat(buf, offset, pos.y); offset += 4;
                WriteFloat(buf, offset, pos.z); offset += 4;
                WriteFloat(buf, offset, rot.x); offset += 4;
                WriteFloat(buf, offset, rot.y); offset += 4;
                WriteFloat(buf, offset, rot.z); offset += 4;
                obj.MarkSent();
            }

            SendBinary(buf);
        }

        private void HandlePositionUpdates(byte[] data, int length)
        {
            if (IsMaster || !_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + 26 <= length; i++)
            {
                ushort localId = BitConverter.ToUInt16(data, offset); offset += 2;
                float px = BitConverter.ToSingle(data, offset); offset += 4;
                float py = BitConverter.ToSingle(data, offset); offset += 4;
                float pz = BitConverter.ToSingle(data, offset); offset += 4;
                float rx = BitConverter.ToSingle(data, offset); offset += 4;
                float ry = BitConverter.ToSingle(data, offset); offset += 4;
                float rz = BitConverter.ToSingle(data, offset); offset += 4;

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    var pos = new Vector3(px, py, pz);
                    var rot = new Vector3(rx, ry, rz);
                    if (_snapFramesRemaining > 0)
                        obj.SnapTo(pos, rot);
                    else
                        obj.ApplyUpdate(pos, rot);
                }
            }

            if (_snapFramesRemaining > 0)
                _snapFramesRemaining--;
        }

        // =====================================================================
        // Snapshot (0x02) — full state for new clients
        // =====================================================================
        // Format: [0x02][u16 count][per entity: u16 local_id + 12B pos + 12B rot = 26B]

        private void SendSnapshot()
        {
            if (_objects.Count == 0) return;

            int count = _objects.Count;
            int size = 3 + count * 26;
            byte[] buf = new byte[size];
            buf[0] = 0x02;
            WriteU16(buf, 1, (ushort)count);

            int offset = 3;
            foreach (var obj in _objects)
            {
                WriteU16(buf, offset, obj.LocalId); offset += 2;
                var pos = obj.transform.position;
                var rot = obj.transform.eulerAngles;
                WriteFloat(buf, offset, pos.x); offset += 4;
                WriteFloat(buf, offset, pos.y); offset += 4;
                WriteFloat(buf, offset, pos.z); offset += 4;
                WriteFloat(buf, offset, rot.x); offset += 4;
                WriteFloat(buf, offset, rot.y); offset += 4;
                WriteFloat(buf, offset, rot.z); offset += 4;
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

            for (int i = 0; i < count && offset + 26 <= length; i++)
            {
                ushort localId = BitConverter.ToUInt16(data, offset); offset += 2;
                float px = BitConverter.ToSingle(data, offset); offset += 4;
                float py = BitConverter.ToSingle(data, offset); offset += 4;
                float pz = BitConverter.ToSingle(data, offset); offset += 4;
                float rx = BitConverter.ToSingle(data, offset); offset += 4;
                float ry = BitConverter.ToSingle(data, offset); offset += 4;
                float rz = BitConverter.ToSingle(data, offset); offset += 4;

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    obj.SnapTo(new Vector3(px, py, pz), new Vector3(rx, ry, rz));
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

        // =====================================================================
        // Binary Helpers
        // =====================================================================

        private static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteFloat(byte[] buf, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            buf[offset] = bytes[0];
            buf[offset + 1] = bytes[1];
            buf[offset + 2] = bytes[2];
            buf[offset + 3] = bytes[3];
        }
    }
}
