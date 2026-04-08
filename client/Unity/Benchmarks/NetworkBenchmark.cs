using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// ReSharper disable Unity.PerformanceCriticalCodeInvocation

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Standalone network benchmark tool.
    ///
    /// Drop this component on any GameObject in a dedicated benchmark scene.
    /// It connects to the relay as master, simulates N entities with realistic
    /// position/velocity data, and lets you spawn simulated spectator clients
    /// on demand — all using the real binary wire protocol.
    ///
    /// Live stats are rendered via OnGUI.
    ///
    /// Usage:
    ///   1. Fill in ServerUrl and RoomId in the Inspector
    ///   2. Press Play in Unity
    ///   3. Click "Start Benchmark" in the overlay
    ///   4. Click "+ Spectator" to add simulated clients one by one (or in bulk)
    ///   5. Watch latency and throughput update in real-time
    /// </summary>
    public class NetworkBenchmark : MonoBehaviour
    {
        // =====================================================================
        // Inspector
        // =====================================================================

        [Header("Server")]
        public string ServerUrl  = "ws://159.65.56.232:9090";
        public string RoomId     = "benchmark";

        [Header("Simulation")]
        [Range(1, 500)] public int EntityCount   = 100;
        [Range(1, 120)] public float SendRateHz  = 20f;

        [Header("Spectators")]
        [Range(1, 200)] public int SpectatorBatch = 5; // how many "+ Spectator" adds at once

        // =====================================================================
        // Protocol constants (mirrors protocol.rs)
        // =====================================================================

        const byte MSG_POSITION_VELOCITY = 0x0B;
        const byte MSG_EVENT             = 0x06;
        const byte CTRL_YOU_ARE_MASTER   = 0x01;
        const byte CTRL_YOU_ARE_SPECTATOR= 0x02;
        const byte CTRL_PREFIX           = 0xFF;

        // Bench probe event name (must match what spectators filter on)
        static readonly byte[] ProbeEventName = Encoding.UTF8.GetBytes("__bench");

        // =====================================================================
        // State
        // =====================================================================

        bool   _running;
        bool   _connecting;
        string _statusMsg = "Idle";

        // Benchmark connection (may be master OR spectator depending on room state)
        ClientWebSocket _masterWs;
        ushort          _myClientId;
        bool            _isMaster;
        CancellationTokenSource _masterCts;
        Task            _masterRecvTask;

        // Entity simulation (only used when we are master)
        Vector3[] _positions;
        Vector3[] _velocities;
        ushort    _sequence;

        // Spectators
        readonly List<SimSpectator> _spectators = new List<SimSpectator>();
        int _nextSpecId;

        // Stats
        long   _sentMessages;
        long   _sentBytes;
        double _sendRateSmoothed;   // msgs/sec (EMA)
        long   _prevSent;
        float  _prevStatTime;

        // Latency (rolling window, 1000 samples)
        readonly Queue<double> _latencyWindow  = new Queue<double>(1024);
        double _latencySum;
        double _latencyP50, _latencyP95, _latencyP99;
        float  _latencyUpdateTimer;

        // Recv stats across all spectators
        long _totalRecv;
        long _totalDropped;

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        void Update()
        {
            if (!_running) return;

            // Update send-rate EMA every second
            float now = Time.realtimeSinceStartup;
            float dt  = now - _prevStatTime;
            if (dt >= 1f)
            {
                long delta = _sentMessages - _prevSent;
                _sendRateSmoothed = _sendRateSmoothed * 0.5 + (delta / dt) * 0.5;
                _prevSent     = _sentMessages;
                _prevStatTime = now;
            }

            // Recompute latency percentiles every 0.5s
            _latencyUpdateTimer += Time.deltaTime;
            if (_latencyUpdateTimer >= 0.5f)
            {
                _latencyUpdateTimer = 0f;
                ComputePercentiles();
            }

            // Aggregate recv / drop stats from all spectators
            long recv = 0, dropped = 0;
            lock (_spectators)
            {
                foreach (var s in _spectators)
                {
                    recv    += s.RecvMessages;
                    dropped += s.DroppedMessages;
                }
            }
            _totalRecv    = recv;
            _totalDropped = dropped;
        }

        void OnDestroy() => StopBenchmark();

        // =====================================================================
        // OnGUI — live stats overlay
        // =====================================================================

        void OnGUI()
        {
            const float W = 400, H = 500;
            float x = 10, y = 10;

            GUI.Box(new Rect(x - 4, y - 4, W + 8, H + 8), GUIContent.none);
            GUI.BeginGroup(new Rect(x, y, W, H));
            float cy = 4;

            // Title
            GUIStyle title = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(0, cy, W, 22), "Somnium Network Benchmark", title);
            cy += 24;

            // Status
            GUI.Label(new Rect(0, cy, W, 18), $"Status: {_statusMsg}");
            cy += 20;

            if (!_running)
            {
                if (!_connecting && GUI.Button(new Rect(0, cy, 180, 26), "Start Benchmark"))
                    StartCoroutine(StartBenchmark());
                GUI.EndGroup();
                return;
            }

            // Separator
            GUI.Box(new Rect(0, cy, W, 1), GUIContent.none);
            cy += 6;

            // Send stats
            GUIStyle header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            string modeLabel = _isMaster ? "MASTER (simulating entities)" : "SPECTATOR (attached to live world)";
            GUI.Label(new Rect(0, cy, W, 18), $"[ {modeLabel} ]", header);
            cy += 20;
            if (_isMaster)
            {
                GUI.Label(new Rect(0, cy, W, 18), $"  Entities:    {EntityCount}  x  {SendRateHz:F0} Hz");
                cy += 18;
                GUI.Label(new Rect(0, cy, W, 18), $"  Probe sent:  {_sentMessages:N0}  ({_sendRateSmoothed:F0} msg/s)");
                cy += 18;
            }
            else
            {
                GUI.Label(new Rect(0, cy, W, 18), "  Real world traffic flowing — measuring relay latency");
                cy += 18;
                GUI.Label(new Rect(0, cy, W, 18), $"  Probe sent:  {_sentMessages:N0}");
                cy += 18;
            }
            GUI.Label(new Rect(0, cy, W, 18), $"  Bandwidth:   {(double)_sentBytes / 1_000_000:F2} MB sent");
            cy += 22;

            // Relay latency
            GUI.Label(new Rect(0, cy, W, 18), "[ RELAY LATENCY (probe events) ]", header);
            cy += 20;
            int specCount;
            lock (_spectators) specCount = _spectators.Count;
            if (_latencyWindow.Count == 0)
            {
                GUI.Label(new Rect(0, cy, W, 18), "  Waiting for samples...");
                cy += 18;
            }
            else
            {
                GUI.Label(new Rect(0, cy, W, 18), $"  p50:  {_latencyP50:F2} ms");   cy += 18;
                GUI.Label(new Rect(0, cy, W, 18), $"  p95:  {_latencyP95:F2} ms");   cy += 18;
                GUI.Label(new Rect(0, cy, W, 18), $"  p99:  {_latencyP99:F2} ms");   cy += 18;
                GUI.Label(new Rect(0, cy, W, 18), $"  n:    {_latencyWindow.Count} samples");
                cy += 22;
            }

            // Spectator stats
            GUI.Label(new Rect(0, cy, W, 18), "[ SPECTATORS ]", header);
            cy += 20;
            GUI.Label(new Rect(0, cy, W, 18), $"  Connected:   {specCount}");         cy += 18;
            GUI.Label(new Rect(0, cy, W, 18), $"  Total recv:  {_totalRecv:N0} msgs"); cy += 18;
            GUI.Label(new Rect(0, cy, W, 18), $"  Dropped:     {_totalDropped:N0}");   cy += 18;
            double recvPerSpec = specCount > 0 ? (double)_totalRecv / specCount : 0;
            GUI.Label(new Rect(0, cy, W, 18), $"  Recv/spec:   {recvPerSpec:N0} msgs"); cy += 22;

            // Controls
            GUI.Box(new Rect(0, cy, W, 1), GUIContent.none);
            cy += 6;
            if (GUI.Button(new Rect(0, cy, 140, 26), $"+ {SpectatorBatch} Spectator(s)"))
                StartCoroutine(AddSpectators(SpectatorBatch));
            if (specCount > 0 && GUI.Button(new Rect(150, cy, 140, 26), $"- {SpectatorBatch} Spectator(s)"))
                StartCoroutine(RemoveSpectators(SpectatorBatch));
            cy += 32;
            if (GUI.Button(new Rect(0, cy, 120, 26), "Stop Benchmark"))
                StopBenchmark();

            GUI.EndGroup();
        }

        // =====================================================================
        // Start / Stop
        // =====================================================================

        IEnumerator StartBenchmark()
        {
            if (_running || _connecting) yield break;
            _connecting = true;
            _statusMsg  = "Connecting as master...";

            // Init entity simulation
            _positions  = new Vector3[EntityCount];
            _velocities = new Vector3[EntityCount];
            var rng = new System.Random();
            for (int i = 0; i < EntityCount; i++)
            {
                _positions[i]  = new Vector3((float)(rng.NextDouble() * 200 - 100),
                                             (float)(rng.NextDouble() * 50),
                                             (float)(rng.NextDouble() * 200 - 100));
                _velocities[i] = new Vector3((float)(rng.NextDouble() * 10 - 5),
                                             (float)(rng.NextDouble() * 4  - 2),
                                             (float)(rng.NextDouble() * 10 - 5));
            }

            // Connect master
            var connectTask = ConnectMasterAsync();
            yield return new WaitUntil(() => connectTask.IsCompleted);

            if (!connectTask.IsCompletedSuccessfully || !_running)
            {
                _statusMsg  = $"Connect failed: {connectTask.Exception?.InnerException?.Message ?? "unknown"}";
                _connecting = false;
                yield break;
            }

            _connecting   = false;
            _prevStatTime = Time.realtimeSinceStartup;
            string role   = _isMaster ? "MASTER" : "SPECTATOR (attached to live world)";
            _statusMsg    = $"Running as {role}  [client_id={_myClientId}]";

            StartCoroutine(SendLoop());
        }

        async Task ConnectMasterAsync()
        {
            _masterCts = new CancellationTokenSource();
            _masterWs  = new ClientWebSocket();

            try
            {
                string uri = $"{ServerUrl}/{Uri.EscapeDataString(RoomId)}";
                await _masterWs.ConnectAsync(new Uri(uri), _masterCts.Token);

                // Read role assignment — accept either master or spectator.
                // If the room already has a player (master), we connect as spectator
                // and attach to the live session instead of simulating our own entities.
                var buf = new byte[512 * 1024]; // large enough to absorb snapshot on join
                var result = await _masterWs.ReceiveAsync(new ArraySegment<byte>(buf), _masterCts.Token);

                if (result.Count >= 4 && buf[0] == CTRL_PREFIX)
                {
                    _myClientId = (ushort)(buf[2] | (buf[3] << 8));
                    _isMaster   = buf[1] == CTRL_YOU_ARE_MASTER;
                    _running    = true;
                    _masterRecvTask = DrainMasterAsync();
                }
                else
                {
                    _statusMsg = "Unexpected handshake from server.";
                    await _masterWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                _statusMsg = $"Error: {e.Message}";
                _running = false;
            }
        }

        // Drain master's receive stream (discard everything — we only send)
        async Task DrainMasterAsync()
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (_masterWs.State == WebSocketState.Open && !_masterCts.IsCancellationRequested)
                    await _masterWs.ReceiveAsync(new ArraySegment<byte>(buf), _masterCts.Token);
            }
            catch { /* disconnected */ }
        }

        void StopBenchmark()
        {
            _running    = false;
            _connecting = false;
            _statusMsg  = "Stopped";

            _masterCts?.Cancel();
            _masterCts?.Dispose();
            _masterCts = null;

            lock (_spectators)
            {
                foreach (var s in _spectators) s.Stop();
                _spectators.Clear();
            }

            _sentMessages = 0; _sentBytes = 0;
            _prevSent = 0;
            _latencyWindow.Clear();
            _latencySum = 0;
            _sequence   = 0;
        }

        // =====================================================================
        // Send loop (coroutine on main thread, builds packet each tick)
        // =====================================================================

        IEnumerator SendLoop()
        {
            float interval   = 1f / SendRateHz;
            float probeTimer = 0f;

            while (_running && _masterWs?.State == WebSocketState.Open)
            {
                if (_isMaster)
                {
                    // Simulate physics (simple Euler integration)
                    for (int i = 0; i < EntityCount; i++)
                    {
                        _positions[i]  += _velocities[i] * interval;
                        BounceAxis(ref _positions[i].x, ref _velocities[i].x, -200f, 200f);
                        BounceAxis(ref _positions[i].y, ref _velocities[i].y,   0f, 100f);
                        BounceAxis(ref _positions[i].z, ref _velocities[i].z, -200f, 200f);
                    }
                    SendToServer(BuildPositionVelocity());
                }

                // Probe event once per second regardless of role.
                // Spectators can send events too — the server broadcasts them to everyone,
                // so all simulated spectators will receive and measure the latency.
                probeTimer += interval;
                if (probeTimer >= 1f)
                {
                    probeTimer = 0f;
                    SendToServer(BuildProbeEvent(Stopwatch.GetTimestamp()));
                }

                yield return new WaitForSeconds(interval);
            }

            if (_running)
            {
                _running   = false;
                _statusMsg = "Disconnected unexpectedly";
            }
        }

        void SendToServer(byte[] data)
        {
            if (_masterWs?.State != WebSocketState.Open) return;
            // Fire-and-forget send (don't await — coroutine can't await Tasks cleanly)
            _ = _masterWs.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _masterCts?.Token ?? CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        System.Threading.Interlocked.Increment(ref _sentMessages);
                        System.Threading.Interlocked.Add(ref _sentBytes, data.Length);
                    }
                });
        }

        static void BounceAxis(ref float pos, ref float vel, float min, float max)
        {
            if (pos < min) { pos = min; vel = Mathf.Abs(vel); }
            else if (pos > max) { pos = max; vel = -Mathf.Abs(vel); }
        }

        // =====================================================================
        // Spectator management
        // =====================================================================

        IEnumerator AddSpectators(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (!_running) yield break;
                int id   = _nextSpecId++;
                var spec = new SimSpectator(id, $"{ServerUrl}/{Uri.EscapeDataString(RoomId)}", AddLatencySample);
                lock (_spectators) _spectators.Add(spec);
                spec.Start();
                yield return new WaitForSeconds(0.05f); // slight stagger to avoid thundering herd
            }
        }

        IEnumerator RemoveSpectators(int count)
        {
            for (int i = 0; i < count; i++)
            {
                SimSpectator s = null;
                lock (_spectators)
                {
                    if (_spectators.Count == 0) yield break;
                    s = _spectators[_spectators.Count - 1];
                    _spectators.RemoveAt(_spectators.Count - 1);
                }
                s?.Stop();
                yield return null;
            }
        }

        // =====================================================================
        // Latency tracking (called from spectator threads)
        // =====================================================================

        void AddLatencySample(double ms)
        {
            lock (_latencyWindow)
            {
                if (_latencyWindow.Count >= 1000)
                    _latencySum -= _latencyWindow.Dequeue();
                _latencyWindow.Enqueue(ms);
                _latencySum += ms;
            }
        }

        void ComputePercentiles()
        {
            double[] arr;
            lock (_latencyWindow)
            {
                if (_latencyWindow.Count == 0) return;
                arr = _latencyWindow.ToArray();
            }
            Array.Sort(arr);
            int n = arr.Length;
            _latencyP50 = arr[(int)(n * 0.50)];
            _latencyP95 = arr[(int)(n * 0.95)];
            _latencyP99 = arr[Math.Min(n - 1, (int)(n * 0.99))];
        }

        // =====================================================================
        // Packet builders
        // =====================================================================

        /// <summary>
        /// MSG_POSITION_VELOCITY (0x0B):
        /// [u8 type][u16 sender_id][u16 seq][u16 count][N × 20 bytes]
        /// Each entity: [u16 id][f16 x][f16 y][f16 z][u16 rx][u16 ry][u16 rz][f16 vx][f16 vy][f16 vz]
        /// </summary>
        byte[] BuildPositionVelocity()
        {
            int count = _positions.Length;
            int size  = 7 + count * 20; // 1+2+2+2 header + N*20
            byte[] buf = new byte[size];
            int o = 0;

            buf[o++] = MSG_POSITION_VELOCITY;
            WriteU16(buf, o, _myClientId); o += 2;
            WriteU16(buf, o, _sequence++);     o += 2;
            WriteU16(buf, o, (ushort)count);   o += 2;

            for (int i = 0; i < count; i++)
            {
                WriteU16(buf, o, (ushort)i); o += 2;             // local_id
                WriteHalf(buf, o, _positions[i].x);  o += 2;     // x
                WriteHalf(buf, o, _positions[i].y);  o += 2;     // y
                WriteHalf(buf, o, _positions[i].z);  o += 2;     // z
                o += 6;                                           // rx, ry, rz (zero rotation)
                WriteHalf(buf, o, _velocities[i].x); o += 2;     // vx
                WriteHalf(buf, o, _velocities[i].y); o += 2;     // vy
                WriteHalf(buf, o, _velocities[i].z); o += 2;     // vz
            }
            return buf;
        }

        /// <summary>
        /// Probe event carrying a Stopwatch timestamp for latency measurement.
        /// MSG_EVENT [0x06][u16 obj=0][u8 target=0][u8 name_len][name][u8 params=1][u8 type=0][u16 len=8][i64 ticks]
        /// </summary>
        static byte[] BuildProbeEvent(long ticks)
        {
            int size = 1 + 2 + 1 + 1 + ProbeEventName.Length + 1 + 1 + 2 + 8;
            byte[] buf = new byte[size];
            int o = 0;
            buf[o++] = MSG_EVENT;
            WriteU16(buf, o, 0); o += 2;            // obj_id = 0
            buf[o++] = 0;                            // target = All
            buf[o++] = (byte)ProbeEventName.Length;
            Array.Copy(ProbeEventName, 0, buf, o, ProbeEventName.Length); o += ProbeEventName.Length;
            buf[o++] = 1;                            // param_count
            buf[o++] = 0;                            // type = int (we abuse it as i64)
            WriteU16(buf, o, 8); o += 2;             // val_len
            WriteI64(buf, o, ticks);                 // ticks
            return buf;
        }

        // =====================================================================
        // Binary helpers
        // =====================================================================

        static void WriteU16(byte[] buf, int o, ushort v)
        {
            buf[o]     = (byte)(v & 0xFF);
            buf[o + 1] = (byte)(v >> 8);
        }

        static void WriteI64(byte[] buf, int o, long v)
        {
            for (int i = 0; i < 8; i++) buf[o + i] = (byte)(v >> (i * 8));
        }

        /// <summary>Convert float32 to float16 (half precision), clamped to ±65504.</summary>
        static void WriteHalf(byte[] buf, int o, float v)
        {
            ushort h = FloatToHalf(v);
            buf[o]     = (byte)(h & 0xFF);
            buf[o + 1] = (byte)(h >> 8);
        }

        static ushort FloatToHalf(float v)
        {
            int  bits  = BitConverter.SingleToInt32Bits(v);
            int  sign  = (bits >> 16) & 0x8000;
            int  exp   = ((bits >> 23) & 0xFF) - 127 + 15;
            int  mant  = bits & 0x7FFFFF;

            if (exp <= 0)  return (ushort)sign;
            if (exp >= 31) return (ushort)(sign | 0x7C00);
            return (ushort)(sign | (exp << 10) | (mant >> 13));
        }
    }

    // =========================================================================
    // SimSpectator — one simulated client connection
    // =========================================================================

    internal sealed class SimSpectator
    {
        public readonly int Id;

        public long RecvMessages  => System.Threading.Interlocked.Read(ref _recvMessages);
        public long DroppedMessages => System.Threading.Interlocked.Read(ref _dropped);

        long _recvMessages;
        long _dropped;

        readonly string _uri;
        readonly Action<double> _onLatencySample;
        CancellationTokenSource _cts;
        Task _task;

        // Stopwatch frequency (ticks per second) — shared with sender
        static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        // Protocol
        const byte MSG_EVENT   = 0x06;
        const byte CTRL_PREFIX = 0xFF;
        static readonly byte[] ProbeEventName = Encoding.UTF8.GetBytes("__bench");

        public SimSpectator(int id, string uri, Action<double> onLatencySample)
        {
            Id                = id;
            _uri              = uri;
            _onLatencySample  = onLatencySample;
        }

        public void Start()
        {
            _cts  = new CancellationTokenSource();
            _task = RunAsync(_cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        async Task RunAsync(CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(_uri), ct);

                // Read role assignment (spectator) — single receive
                var roleBuf = new byte[32];
                await ws.ReceiveAsync(new ArraySegment<byte>(roleBuf), ct);

                // Receive loop
                var buf = new byte[1024 * 1024]; // 1 MB — large enough for any snapshot
                var accum = new byte[1024 * 1024];
                int accumLen = 0;

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { break; }

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    // Accumulate fragmented frames
                    if (accumLen + result.Count <= accum.Length)
                    {
                        Array.Copy(buf, 0, accum, accumLen, result.Count);
                        accumLen += result.Count;
                    }

                    if (!result.EndOfMessage) continue;

                    // Process complete message
                    ProcessMessage(accum, accumLen);
                    accumLen = 0;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* spectator died — counted in dropped */ }
        }

        void ProcessMessage(byte[] data, int length)
        {
            if (length == 0) return;

            System.Threading.Interlocked.Increment(ref _recvMessages);

            // Check for probe event so we can measure relay latency
            if (length > 4 && data[0] == MSG_EVENT)
            {
                TryExtractProbeLatency(data, length);
            }
        }

        void TryExtractProbeLatency(byte[] data, int length)
        {
            // MSG_EVENT: [0x06][u16 obj][u8 target][u8 name_len][name][u8 count][u8 type][u16 val_len][i64 ticks]
            if (length < 8) return;
            int o = 4; // skip type + obj_id + target
            if (o >= length) return;
            int nameLen = data[o++];
            if (o + nameLen > length) return;

            // Check event name matches our probe
            if (nameLen != ProbeEventName.Length) return;
            for (int i = 0; i < nameLen; i++)
                if (data[o + i] != ProbeEventName[i]) return;
            o += nameLen;

            if (o + 4 > length) return;
            int paramCount = data[o++];
            if (paramCount < 1) return;
            int type = data[o++];
            if (type != 0) return; // expect int-type slot
            int valLen = data[o] | (data[o + 1] << 8); o += 2;
            if (valLen < 8 || o + 8 > length) return;

            long sentTicks = 0;
            for (int i = 0; i < 8; i++) sentTicks |= ((long)data[o + i] << (i * 8));

            long nowTicks = Stopwatch.GetTimestamp();
            double ms = (nowTicks - sentTicks) / TicksPerMs;

            // Sanity check — ignore if clock skewed or negative (shouldn't happen, same machine)
            if (ms >= 0 && ms < 5000)
                _onLatencySample(ms);
        }
    }
}
