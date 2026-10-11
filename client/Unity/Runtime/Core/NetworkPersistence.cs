using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// ReSharper disable Unity.PerformanceCriticalCodeInvocation

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Server-backed player persistence — save and load any typed value per player.
    ///
    /// Values are stored in a SQLite database on the relay server, keyed by the
    /// player's stable identity (Somnium account UUID or PlayerPrefs fallback UUID).
    ///
    /// Usage:
    ///   // Load (fires callback when server responds)
    ///   NetworkPersistence.Load("health", 100f, val => health = val);
    ///   NetworkPersistence.Load("coins",  0,    val => coins  = val);
    ///   NetworkPersistence.Load("name",   "",   val => playerName = val);
    ///
    ///   // Save (fire and forget)
    ///   NetworkPersistence.Save("health", health);
    ///   NetworkPersistence.Save("coins",  coins);
    ///
    /// Namespace (optional, default "global"):
    ///   NetworkPersistence.Load("score", 0, val => score = val, ns: "myrpg");
    ///   NetworkPersistence.Save("score", score, ns: "myrpg");
    ///
    /// Somnium integration:
    ///   Add SomniumBridgeIntegration.cs to your scene so the player's Somnium
    ///   account UUID is used as the key (instead of a device-local UUID).
    /// </summary>
    public static class NetworkPersistence
    {
        // =====================================================================
        // Identity provider — override this to use Somnium or custom auth.
        // SomniumBridgeIntegration.cs sets this automatically.
        // =====================================================================

        /// <summary>
        /// Override to provide a stable player identity from your platform.
        /// Set to: () => SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.Id
        /// </summary>
        public static Func<string> PlayerIdProvider;

        // =====================================================================
        // Pending callbacks — indexed by request ID
        // =====================================================================

        private static readonly Dictionary<ushort, Action<string, bool>> _pending
            = new Dictionary<ushort, Action<string, bool>>();
        private static ushort _reqId;

        // =====================================================================
        // Called by NetworkManager on disconnect — fire all pending with not-found
        // =====================================================================

        internal static void ClearPending()
        {
            foreach (var cb in _pending.Values)
                cb?.Invoke(null, false);
            _pending.Clear();
        }

        // =====================================================================
        // Called by NetworkManager when connection + role are assigned
        // =====================================================================

        internal static void OnNetworkReady(NetworkManager mgr)
        {
            // Send player identity to server so it can key persistence data
            string id = GetPlayerId();
            byte[] idBytes = Encoding.UTF8.GetBytes(id);
            if (idBytes.Length == 0 || idBytes.Length > 64)
            {
                Debug.LogError("[Persist] Player identity must be 1-64 UTF-8 bytes; persistence request skipped.");
                return;
            }
            byte[] buf = new byte[3 + idBytes.Length];
            buf[0] = 0xFF;
            buf[1] = Protocol.CTRL_PLAYER_IDENT;
            buf[2] = (byte)idBytes.Length;
            Array.Copy(idBytes, 0, buf, 3, idBytes.Length);
            mgr.SendBinaryPublic(buf);
            Debug.Log("[Persist] Player identity sent; the relay may keep persistence disabled.");
        }

        // =====================================================================
        // Called by NetworkManager.Receiver when CTRL_PERSIST_DATA arrives
        // =====================================================================

        internal static void HandlePersistData(ushort reqId, bool found, string rawValue)
        {
            if (_pending.TryGetValue(reqId, out var cb))
            {
                _pending.Remove(reqId);
                cb?.Invoke(rawValue, found);
            }
        }

        // =====================================================================
        // Public API — Load
        // =====================================================================

        /// <summary>Load a float. Callback fires with the stored value, or defaultValue if not found.</summary>
        public static void Load(string key, float defaultValue, Action<float> callback, string ns = "global")
        {
            SendGet(key, ns, (raw, found) =>
            {
                if (found && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
                    callback?.Invoke(v);
                else
                    callback?.Invoke(defaultValue);
            });
        }

        /// <summary>Load an int. Callback fires with the stored value, or defaultValue if not found.</summary>
        public static void Load(string key, int defaultValue, Action<int> callback, string ns = "global")
        {
            SendGet(key, ns, (raw, found) =>
            {
                if (found && int.TryParse(raw, out int v))
                    callback?.Invoke(v);
                else
                    callback?.Invoke(defaultValue);
            });
        }

        /// <summary>Load a bool. Callback fires with the stored value, or defaultValue if not found.</summary>
        public static void Load(string key, bool defaultValue, Action<bool> callback, string ns = "global")
        {
            SendGet(key, ns, (raw, found) =>
            {
                if (found && bool.TryParse(raw, out bool v))
                    callback?.Invoke(v);
                else
                    callback?.Invoke(defaultValue);
            });
        }

        /// <summary>Load a string. Callback fires with the stored value, or defaultValue if not found.</summary>
        public static void Load(string key, string defaultValue, Action<string> callback, string ns = "global")
        {
            SendGet(key, ns, (raw, found) => callback?.Invoke(found ? raw : defaultValue));
        }

        // =====================================================================
        // Public API — Save
        // =====================================================================

        /// <summary>Save a float value server-side (fire and forget).</summary>
        public static void Save(string key, float value, string ns = "global")
            => SendSet(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ns);

        /// <summary>Save an int value server-side (fire and forget).</summary>
        public static void Save(string key, int value, string ns = "global")
            => SendSet(key, value.ToString(), ns);

        /// <summary>Save a bool value server-side (fire and forget).</summary>
        public static void Save(string key, bool value, string ns = "global")
            => SendSet(key, value.ToString(), ns);

        /// <summary>Save a string value server-side (fire and forget).</summary>
        public static void Save(string key, string value, string ns = "global")
            => SendSet(key, value ?? "", ns);

        // =====================================================================
        // Identity
        // =====================================================================

        /// <summary>
        /// Returns the stable player ID used as the persistence key.
        /// Priority: PlayerIdProvider → PlayerPrefs UUID fallback.
        /// </summary>
        public static string GetPlayerId()
        {
            if (PlayerIdProvider != null)
            {
                string id = PlayerIdProvider();
                if (!string.IsNullOrEmpty(id)) return id;
            }

            // Fallback: device-local UUID stored in PlayerPrefs
            string uuid = PlayerPrefs.GetString("somni_persist_id", "");
            if (string.IsNullOrEmpty(uuid))
            {
                uuid = Guid.NewGuid().ToString();
                PlayerPrefs.SetString("somni_persist_id", uuid);
                PlayerPrefs.Save();
            }
            return uuid;
        }

        // =====================================================================
        // Wire protocol helpers
        // =====================================================================

        private static void SendGet(string key, string ns, Action<string, bool> callback)
        {
            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsConnected || !mgr.PersistenceAvailable)
            {
                callback?.Invoke(null, false);
                return;
            }

            byte[] nsBytes  = Encoding.UTF8.GetBytes(ns ?? "");
            byte[] keyBytes = Encoding.UTF8.GetBytes(key ?? "");
            if (nsBytes.Length > 64 || keyBytes.Length > 128)
            {
                Debug.LogError("[Persist] Namespace or key exceeds the relay limit (64/128 UTF-8 bytes).");
                callback?.Invoke(null, false);
                return;
            }

            // Skip IDs already in use (handles wraparound after 65535 requests).
            // Guard against infinite spin if somehow all 65535 slots are occupied.
            int attempts = 0;
            do
            {
                unchecked { _reqId++; }
                attempts++;
            }
            while (_pending.ContainsKey(_reqId) && attempts < 65535);

            if (attempts >= 65535)
            {
                Debug.LogError("[Persist] All request ID slots in use — too many concurrent Load calls, dropping request");
                callback?.Invoke(null, false);
                return;
            }

            ushort id = _reqId;
            _pending[id] = callback;

            // [0xFF][0x22][u16 req_id][u8 ns_len][ns][u8 key_len][key]
            int size = 6 + nsBytes.Length + keyBytes.Length;
            byte[] buf = new byte[size];
            int o = 0;
            buf[o++] = 0xFF;
            buf[o++] = Protocol.CTRL_PERSIST_GET;
            buf[o++] = (byte)(id & 0xFF);
            buf[o++] = (byte)(id >> 8);
            buf[o++] = (byte)nsBytes.Length;
            Array.Copy(nsBytes, 0, buf, o, nsBytes.Length); o += nsBytes.Length;
            buf[o++] = (byte)keyBytes.Length;
            Array.Copy(keyBytes, 0, buf, o, keyBytes.Length);

            if (!mgr.TrySendBinaryPublic(buf))
            {
                _pending.Remove(id);
                callback?.Invoke(null, false);
            }
        }

        private static void SendSet(string key, string value, string ns)
        {
            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsConnected || !mgr.PersistenceAvailable) return;

            byte[] nsBytes  = Encoding.UTF8.GetBytes(ns ?? "");
            byte[] keyBytes = Encoding.UTF8.GetBytes(key ?? "");
            byte[] valBytes = Encoding.UTF8.GetBytes(value ?? "");

            if (nsBytes.Length > 64 || keyBytes.Length > 128 || valBytes.Length > 4096)
            {
                Debug.LogError("[Persist] Save exceeds the relay limit (64-byte namespace, 128-byte key, 4096-byte value).");
                return;
            }

            // [0xFF][0x21][u8 ns_len][ns][u8 key_len][key][u16 val_len][val]
            int size = 4 + nsBytes.Length + keyBytes.Length + 2 + valBytes.Length;
            byte[] buf = new byte[size];
            int o = 0;
            buf[o++] = 0xFF;
            buf[o++] = Protocol.CTRL_PERSIST_SET;
            buf[o++] = (byte)nsBytes.Length;
            Array.Copy(nsBytes, 0, buf, o, nsBytes.Length); o += nsBytes.Length;
            buf[o++] = (byte)keyBytes.Length;
            Array.Copy(keyBytes, 0, buf, o, keyBytes.Length); o += keyBytes.Length;
            buf[o++] = (byte)(valBytes.Length & 0xFF);
            buf[o++] = (byte)(valBytes.Length >> 8);
            Array.Copy(valBytes, 0, buf, o, valBytes.Length);

            mgr.SendBinaryPublic(buf);
        }
    }
}
