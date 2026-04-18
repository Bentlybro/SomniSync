using System.Globalization;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Structured periodic state dump for SomniSync desync debugging.
    ///
    /// Drop this on any GameObject; it logs one "tick" block per interval
    /// covering:
    ///   • The client's current ServerTime + NTP offset + best RTT (so you
    ///     can confirm every client agrees on the clock).
    ///   • Every NetworkNPC's internal state: _origin, _wanderEpoch,
    ///     _anchoredReleaseMs, claim flags, and last-release fields from
    ///     the sibling NetworkObject.
    ///
    /// Output goes to the Unity console only — no filesystem writes. World
    /// scripts in Somnium run sandboxed and writing to disk is not
    /// something we want a networking library doing. For persistent,
    /// machine-readable diagnostic capture there are two better paths:
    ///
    ///   * The server's <c>/export</c> HTTP endpoint (see
    ///     <c>server/src/metrics_server.rs</c>) — captures the whole server
    ///     state including the full log ring and raw physics timings.
    ///   * The headless Python bot client (<c>tools/bot_client.py</c>)
    ///     mimics a Unity client at the protocol level and writes CSVs
    ///     from outside the Unity sandbox; <c>tools/netdbg_diff.py</c>
    ///     compares any pair.
    ///
    /// Tag each client's output: set <see cref="clientTag"/> to e.g. "A"
    /// and "B" so log lines are easy to identify after the fact.
    /// </summary>
    public class NetworkDebug : MonoBehaviour
    {
        [Header("Logging")]
        [Tooltip("Master on/off for this dump.")]
        public bool enableLogging = true;
        [Tooltip("Short identifier for this client (e.g. \"A\" / \"B\"). Included in every log line so you can tell clients apart when merging logs.")]
        public string clientTag = "?";
        [Tooltip("Seconds between state dumps.")]
        public float interval = 1f;

        [Header("Scope")]
        [Tooltip("Log NetworkManager / ServerTime / NTP state each tick.")]
        public bool logClock = true;
        [Tooltip("Log every NetworkNPC's wander/claim state each tick.")]
        public bool logNPCs = true;
        [Tooltip("Log arrival of CTRL_RELEASE messages (position + server_ms).")]
        public bool logReleaseEvents = true;

        private float _nextDumpTime;
        private NetworkNPC[] _npcs;
        private float _nextRefreshTime;

        protected virtual void Update()
        {
            if (!enableLogging) return;
            if (Time.time < _nextDumpTime) return;
            _nextDumpTime = Time.time + Mathf.Max(0.05f, interval);

            // Cheap to refresh NPC list — not every tick, maybe every 2s.
            if (_npcs == null || Time.time >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.time + 2f;
                _npcs = FindObjectsByType<NetworkNPC>(FindObjectsSortMode.InstanceID);
            }

            var mgr = NetworkManager.Instance;
            var sb = new StringBuilder(512);
            string tag = string.IsNullOrEmpty(clientTag) ? "?" : clientTag;

            double localT = Time.timeAsDouble;
            double serverT = mgr != null ? mgr.ServerTime : double.NaN;
            double offset = mgr != null ? mgr.ServerTimeOffsetSeconds : double.NaN;
            double rttS = (mgr != null && mgr.BestRttSeconds != double.MaxValue) ? mgr.BestRttSeconds : -1.0;
            int cid = mgr != null ? (int)mgr.ClientId : -1;
            bool ready = mgr != null && mgr.IsNetworkReady;
            bool phys = mgr != null && mgr.IsPhysicsReady;

            if (logClock)
            {
                if (mgr == null)
                {
                    sb.AppendLine($"[NetDbg {tag}] NetworkManager.Instance is null");
                }
                else
                {
                    sb.AppendFormat(
                        "[NetDbg {0} CLOCK] localT={1:F3} serverT={2:F6} offset={3:F6} bestRTT={4:F3} cid={5} ready={6} physicsReady={7}\n",
                        tag, localT, serverT, offset, rttS, cid, ready, phys);
                }
            }

            if (logNPCs && _npcs != null && mgr != null)
            {
                for (int i = 0; i < _npcs.Length; i++)
                {
                    var n = _npcs[i];
                    if (n == null) continue;
                    var netObj = n.GetComponent<NetworkObject>();
                    Vector3 pos = n.transform.position;
                    Vector3 lrp = netObj != null ? netObj.LastReleasePos : Vector3.zero;
                    ulong lrm = netObj != null ? netObj.LastReleaseServerMs : 0;
                    bool loc = netObj != null && netObj.IsClaimedLocally;
                    bool rem = netObj != null && netObj.IsClaimedByRemote;
                    double wT = (n.DebugWanderEpoch >= 0) ? (mgr.ServerTime - n.DebugWanderEpoch) : double.NaN;
                    string npcId = netObj != null ? netObj.LocalId.ToString(CultureInfo.InvariantCulture) : "";

                    sb.AppendFormat(
                        "[NetDbg {0} NPC] id={1} name={2} pos=({3:F3},{4:F3},{5:F3}) origin=({6:F3},{7:F3},{8:F3}) epoch={9:F6} wT={10:F3} anchoredMs={11} lastReleasePos=({12:F3},{13:F3},{14:F3}) lastReleaseMs={15} claim=loc:{16}/rem:{17}\n",
                        tag,
                        npcId.Length == 0 ? "?" : npcId,
                        n.gameObject.name,
                        pos.x, pos.y, pos.z,
                        n.DebugOrigin.x, n.DebugOrigin.y, n.DebugOrigin.z,
                        n.DebugWanderEpoch,
                        wT,
                        n.DebugAnchoredReleaseMs,
                        lrp.x, lrp.y, lrp.z,
                        lrm,
                        loc ? "T" : "F",
                        rem ? "T" : "F");
                }
            }

            if (sb.Length > 0)
                Debug.Log(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Log a single line when CTRL_RELEASE arrives. Called by
        /// NetworkManager.Receiver.HandleRelease when logReleaseEvents is on.
        /// </summary>
        internal void LogReleaseReceived(ushort objId, Vector3 pos, Quaternion rot, ulong serverMs)
        {
            if (!enableLogging || !logReleaseEvents) return;
            string tag = string.IsNullOrEmpty(clientTag) ? "?" : clientTag;
            Debug.Log(
                $"[NetDbg {tag} REL<=] obj={objId} serverMs={serverMs} pos=({pos.x:F3},{pos.y:F3},{pos.z:F3}) localT={Time.timeAsDouble:F3}"
            );
        }

        /// <summary>Convenience to find the current NetworkDebug (if any).</summary>
        public static NetworkDebug Instance
        {
            get
            {
                if (_instance == null)
                    _instance = UnityEngine.Object.FindFirstObjectByType<NetworkDebug>();
                return _instance;
            }
        }
        private static NetworkDebug _instance;

        protected virtual void OnEnable()  => _instance = this;
        protected virtual void OnDisable() { if (_instance == this) _instance = null; }
    }
}
