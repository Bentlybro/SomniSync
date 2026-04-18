using System;
using System.Globalization;
using System.IO;
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
    /// Run two clients side-by-side with this enabled, then diff the two
    /// consoles / log files around the same wall-clock time. Any line that
    /// differs between clients (position, wander state, anchors, etc.) is
    /// a data-driven pointer at the divergence source — no more guessing.
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

        [Header("CSV File Output")]
        [Tooltip("Also write every tick to a CSV file. Each client writes its own file; tools/netdbg_diff.py can diff two files post-hoc.")]
        public bool logToFile = false;
        [Tooltip("Optional file path override. If empty, writes to Application.persistentDataPath/netdbg_{clientTag}_{UTC stamp}.csv. Printed to console on first write.")]
        public string logFilePath = "";

        private float _nextDumpTime;
        private NetworkNPC[] _npcs;
        private float _nextRefreshTime;
        private StreamWriter _csv;
        private string _csvPathResolved;

        private void Update()
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

            int npcRowsWritten = 0;
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

                    if (logToFile)
                    {
                        WriteCsvRow(tag, localT, serverT, offset, rttS, cid, ready, phys,
                            npcId, n.gameObject.name, pos,
                            n.DebugOrigin, n.DebugWanderEpoch, wT, n.DebugAnchoredReleaseMs,
                            lrp, lrm, loc, rem);
                        npcRowsWritten++;
                    }
                }
            }

            if (logToFile && npcRowsWritten == 0 && mgr != null)
            {
                WriteCsvRow(tag, localT, serverT, offset, rttS, cid, ready, phys,
                    "", "", Vector3.zero,
                    Vector3.zero, double.NaN, double.NaN, 0UL,
                    Vector3.zero, 0UL, false, false);
            }

            if (sb.Length > 0)
                Debug.Log(sb.ToString().TrimEnd());
        }

        private void WriteCsvRow(
            string tag, double localT, double serverT, double offset, double rttS,
            int cid, bool ready, bool phys,
            string npcId, string npcName, Vector3 pos,
            Vector3 origin, double wanderEpoch, double wT, ulong anchoredMs,
            Vector3 lastReleasePos, ulong lastReleaseMs, bool claimLoc, bool claimRem)
        {
            EnsureCsvOpen(tag);
            if (_csv == null) return;

            // Sanitize name for CSV: strip commas and quotes.
            string safeName = npcName.Replace(",", " ").Replace("\"", "");
            var ci = CultureInfo.InvariantCulture;
            _csv.Write(tag); _csv.Write(',');
            _csv.Write(localT.ToString("F3", ci)); _csv.Write(',');
            _csv.Write(serverT.ToString("F6", ci)); _csv.Write(',');
            _csv.Write(offset.ToString("F6", ci)); _csv.Write(',');
            _csv.Write(rttS.ToString("F6", ci)); _csv.Write(',');
            _csv.Write(cid.ToString(ci)); _csv.Write(',');
            _csv.Write(ready ? "1" : "0"); _csv.Write(',');
            _csv.Write(phys ? "1" : "0"); _csv.Write(',');
            _csv.Write(npcId); _csv.Write(',');
            _csv.Write(safeName); _csv.Write(',');
            _csv.Write(pos.x.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(pos.y.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(pos.z.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(origin.x.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(origin.y.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(origin.z.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(wanderEpoch.ToString("F6", ci)); _csv.Write(',');
            _csv.Write(wT.ToString("F6", ci)); _csv.Write(',');
            _csv.Write(anchoredMs.ToString(ci)); _csv.Write(',');
            _csv.Write(lastReleasePos.x.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(lastReleasePos.y.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(lastReleasePos.z.ToString("F4", ci)); _csv.Write(',');
            _csv.Write(lastReleaseMs.ToString(ci)); _csv.Write(',');
            _csv.Write(claimLoc ? "1" : "0"); _csv.Write(',');
            _csv.Write(claimRem ? "1" : "0");
            _csv.Write('\n');
        }

        private void EnsureCsvOpen(string tag)
        {
            if (_csv != null) return;
            try
            {
                string path = logFilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    string safeTag = string.IsNullOrEmpty(tag) ? "x" : tag;
                    path = Path.Combine(Application.persistentDataPath, $"netdbg_{safeTag}_{stamp}.csv");
                }
                _csvPathResolved = path;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _csv = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
                _csv.Write(
                    "tag,localT,serverT,offset,bestRTT,cid,ready,physicsReady," +
                    "npc_id,npc_name,pos_x,pos_y,pos_z," +
                    "origin_x,origin_y,origin_z,wanderEpoch,wT,anchoredMs," +
                    "lrp_x,lrp_y,lrp_z,lrm,claimLoc,claimRem\n");
                Debug.Log($"[NetDbg {tag}] CSV -> {_csvPathResolved}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[NetDbg {tag}] CSV open failed: {e.Message}. Disabling file logging.");
                logToFile = false;
                _csv = null;
            }
        }

        private void CloseCsv()
        {
            if (_csv == null) return;
            try { _csv.Flush(); _csv.Dispose(); } catch { }
            _csv = null;
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

        private void OnEnable() => _instance = this;
        private void OnDisable()
        {
            if (_instance == this) _instance = null;
            CloseCsv();
        }
        private void OnDestroy() => CloseCsv();
        private void OnApplicationQuit() => CloseCsv();
    }
}
