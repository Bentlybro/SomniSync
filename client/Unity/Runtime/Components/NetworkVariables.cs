using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Sync custom variables (int, float, bool, string) across clients.
    /// Add alongside NetworkObject. The room's elected state writer changes
    /// unclaimed objects; a claimed object's holder changes its variables.
    /// </summary>
    public class NetworkVariables : NetworkComponent
    {
        // =====================================================================
        // Types
        // =====================================================================

        public enum VarType : byte
        {
            Int = 0,
            Float = 1,
            Bool = 2,
            String = 3
        }

        public enum SyncMode : byte
        {
            OnChange,   // Batched with tick (default, bandwidth-friendly)
            Immediate   // Sent the moment it's set
        }

        [System.Serializable]
        public class VarDefinition
        {
            public string name;
            public VarType type;
            public string defaultValue = "";
            public SyncMode syncMode = SyncMode.OnChange;
            public bool persistForLateJoiners = true;

            [System.NonSerialized] public byte[] currentValue;
            [System.NonSerialized] public byte[] nameBytes; // cached UTF-8 encoding of name
            [System.NonSerialized] public bool dirty;
        }

        // =====================================================================
        // Inspector + Runtime State
        // =====================================================================

        [Header("Variables (define in Inspector or via code)")]
        public List<VarDefinition> variables = new List<VarDefinition>();

        /// <summary>
        /// Fires when any variable changes (local or remote).
        /// Parameters: variable name, variable type.
        /// </summary>
        public event Action<string, VarType> OnValueChanged;
        /// <summary>Fires only when a value arrives from the relay.</summary>
        public event Action<string, VarType> OnRemoteValueChanged;

        private Dictionary<string, VarDefinition> _varMap = new Dictionary<string, VarDefinition>();
        private bool _initialized;

        // =====================================================================
        // Init
        // =====================================================================

        protected virtual void Start()
        {
            // NetObj already resolved + null-checked by base Awake.
            if (!_initialized) InitializeVariables();
        }

        private void InitializeVariables()
        {
            _varMap.Clear();
            foreach (var v in variables)
            {
                if (string.IsNullOrEmpty(v.name)) continue;
                v.currentValue = SerializeDefault(v.type, v.defaultValue);
                v.nameBytes    = Encoding.UTF8.GetBytes(v.name);
                v.dirty        = false;
                _varMap[v.name] = v;
            }
            _initialized = true;
        }

        // =====================================================================
        // Update — send dirty vars
        // =====================================================================

        protected virtual void Update()
        {
            if (!_initialized || NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;

            // Only send if we have authority
            if (!HasAuthority()) return;

            // Retry all dirty variables, including Immediate values that could
            // not enter the send queue at the moment they were set.
            bool hasDirty = false;
            foreach (var v in variables)
            {
                if (v.dirty)
                {
                    hasDirty = true;
                    break;
                }
            }

            if (hasDirty)
            {
                SendDirtyVars(false);
            }
        }

        // =====================================================================
        // Public API — Get
        // =====================================================================

        public int GetInt(string name)
        {
            var v = GetVar(name);
            if (v == null || v.currentValue == null || v.currentValue.Length < 4) return 0;
            return BitConverter.ToInt32(v.currentValue, 0);
        }

        public float GetFloat(string name)
        {
            var v = GetVar(name);
            if (v == null || v.currentValue == null || v.currentValue.Length < 4) return 0f;
            return BitConverter.ToSingle(v.currentValue, 0);
        }

        public bool GetBool(string name)
        {
            var v = GetVar(name);
            if (v == null || v.currentValue == null || v.currentValue.Length < 1) return false;
            return v.currentValue[0] != 0;
        }

        public string GetString(string name)
        {
            var v = GetVar(name);
            if (v == null || v.currentValue == null || v.currentValue.Length == 0) return "";
            return Encoding.UTF8.GetString(v.currentValue);
        }

        // =====================================================================
        // Public API — Set (auto-syncs)
        // =====================================================================

        public void SetInt(string name, int value)
        {
            if (!CheckAuthorityForWrite(name)) return;
            var v = GetOrCreate(name, VarType.Int);
            if (v == null) return;
            v.currentValue = BitConverter.GetBytes(value);
            MarkDirty(v);
        }

        public void SetFloat(string name, float value)
        {
            if (!CheckAuthorityForWrite(name)) return;
            if (float.IsNaN(value) || float.IsInfinity(value)) return;
            var v = GetOrCreate(name, VarType.Float);
            if (v == null) return;
            v.currentValue = BitConverter.GetBytes(value);
            MarkDirty(v);
        }

        public void SetBool(string name, bool value)
        {
            if (!CheckAuthorityForWrite(name)) return;
            var v = GetOrCreate(name, VarType.Bool);
            if (v == null) return;
            v.currentValue = new byte[] { (byte)(value ? 1 : 0) };
            MarkDirty(v);
        }

        public void SetString(string name, string value)
        {
            if (!CheckAuthorityForWrite(name)) return;
            if (Encoding.UTF8.GetByteCount(value ?? "") > 4096)
            {
                Debug.LogError($"[NetVars] '{name}' exceeds the 4096-byte string limit.");
                return;
            }
            var v = GetOrCreate(name, VarType.String);
            if (v == null) return;
            v.currentValue = Encoding.UTF8.GetBytes(value ?? "");
            MarkDirty(v);
        }

        /// <summary>
        /// Reject writes from clients that don't hold authority over this
        /// object. Previously a non-authority write silently updated the
        /// local value and then failed to sync (the Update loop skips sends
        /// for non-authorities) — looked to users like the value stuck,
        /// then flipped back on the next remote update. Now surface a clear
        /// warning and bail before corrupting local state.
        ///
        /// The check is skipped until NetworkManager has registered this object
        /// so Awake/Start initialisation can still populate defaults before any
        /// claim state is known. LocalId 0 is a real network id, not a sentinel.
        /// </summary>
        private bool CheckAuthorityForWrite(string name)
        {
            if (NetObj == null || !NetObj.IsRegistered) return true;
            if (HasAuthority()) return true;
            Debug.LogWarning($"[NetVars] '{this.name}' cannot set '{name}' — no authority (object claimed by another client).");
            return false;
        }

        // =====================================================================
        // Public API — Utility
        // =====================================================================

        /// <summary>Check if a variable exists.</summary>
        public bool HasVariable(string name) => _varMap.ContainsKey(name);

        /// <summary>Register a variable at runtime (alternative to Inspector).</summary>
        public void DefineVariable(string name, VarType type, SyncMode syncMode = SyncMode.OnChange,
                                   bool persist = true)
        {
            if (!_initialized) InitializeVariables();
            if (_varMap.ContainsKey(name)) return;
            var v = new VarDefinition
            {
                name = name,
                type = type,
                syncMode = syncMode,
                persistForLateJoiners = persist,
                currentValue = SerializeDefault(type, ""),
                dirty = false
            };
            variables.Add(v);
            _varMap[name] = v;
        }

        // =====================================================================
        // Authority
        // =====================================================================

        /// <summary>Can this client set variables on this object?</summary>

        // =====================================================================
        // Serialization — Send
        // =====================================================================

        private void MarkDirty(VarDefinition v)
        {
            v.dirty = true;
            OnValueChanged?.Invoke(v.name, v.type);

            if (v.syncMode == SyncMode.Immediate && HasAuthority())
            {
                SendDirtyVars(true);
            }
        }

        /// <summary>
        /// Send dirty variables as 0x04 message.
        /// Format: [0x04][u16 object_id][u8 var_count][per var: u8 name_len, name, u8 type, u16 value_len, value]
        /// </summary>
        private void SendDirtyVars(bool immediateOnly)
        {
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected || NetObj == null || !NetObj.IsRegistered) return;

            const int maxFrameBytes = 64 * 1024;
            var selected = new List<VarDefinition>();
            int totalSize = 4; // type + object_id + count
            foreach (var v in variables)
            {
                if (!v.dirty) continue;
                if (immediateOnly && v.syncMode != SyncMode.Immediate) continue;
                if (v.nameBytes == null) v.nameBytes = Encoding.UTF8.GetBytes(v.name ?? "");
                int valueLength = v.currentValue?.Length ?? 0;
                if (v.nameBytes.Length == 0 || v.nameBytes.Length > 64 || valueLength > 4096)
                {
                    Debug.LogError($"[NetVars] Invalid name or value length for '{v.name}'. Names are 1-64 UTF-8 bytes; values are at most 4096 bytes.");
                    v.dirty = false;
                    continue;
                }
                int recordSize = 1 + v.nameBytes.Length + 1 + 2 + valueLength;
                if (selected.Count >= 255 || totalSize + recordSize > maxFrameBytes) break;
                totalSize += recordSize;
                selected.Add(v);
            }

            if (selected.Count == 0) return;

            byte[] buf = new byte[totalSize];
            buf[0] = Protocol.MSG_VARIABLE_UPDATE;
            Protocol.WriteU16(buf, 1, NetObj.LocalId);
            buf[3] = (byte)selected.Count;

            int offset = 4;
            foreach (var v in selected)
            {
                buf[offset++] = (byte)v.nameBytes.Length;
                Array.Copy(v.nameBytes, 0, buf, offset, v.nameBytes.Length);
                offset += v.nameBytes.Length;

                // High bit tells the relay to cache this value for late joiners.
                buf[offset++] = (byte)((byte)v.type | (v.persistForLateJoiners ? 0x80 : 0));

                int valLen = v.currentValue?.Length ?? 0;
                Protocol.WriteU16(buf, offset, (ushort)valLen);
                offset += 2;

                if (valLen > 0)
                {
                    Array.Copy(v.currentValue, 0, buf, offset, valLen);
                    offset += valLen;
                }

            }

            if (manager.TrySendBinaryPublic(buf))
                foreach (var v in selected) v.dirty = false;
        }

        // =====================================================================
        // Deserialization — Receive (called by NetworkManager)
        // =====================================================================

        /// <summary>
        /// Apply a received 0x04 variable update.
        /// Called by NetworkManager when it receives 0x04 for this object.
        /// </summary>
        public void ApplyRemoteUpdate(byte[] data, int startOffset, int varCount, int length)
        {
            if (length > data.Length || startOffset < 0 || startOffset > length) return;
            if (!_initialized) InitializeVariables();
            int offset = startOffset;
            for (int i = 0; i < varCount; i++)
            {
                if (offset >= length) break;
                int nameLen = data[offset++];
                if (offset + nameLen + 3 > length) break; // name + type(1) + valLen(2)
                string name = Encoding.UTF8.GetString(data, offset, nameLen);
                offset += nameLen;

                if (offset >= length) break;
                VarType type = (VarType)(data[offset++] & 0x7F);
                if (type > VarType.String) break;

                if (offset + 2 > length) break;
                int valLen = Protocol.ReadU16(data, offset);
                offset += 2;

                if (offset + valLen > length) break;
                byte[] value = new byte[valLen];
                if (valLen > 0)
                    Array.Copy(data, offset, value, 0, valLen);
                offset += valLen;

                // Apply to local state
                var v = GetOrCreate(name, type);
                if (v == null) continue;
                v.currentValue = value;
                v.dirty = false;

                OnValueChanged?.Invoke(name, type);
                OnRemoteValueChanged?.Invoke(name, type);
            }
        }

        /// <summary>
        /// Serialize all persistent variables for inclusion in snapshot.
        /// Returns null if no persistent variables or no data.
        /// Format: [u8 count][per var: u8 name_len, name, u8 type, u16 value_len, value]
        /// </summary>
        public byte[] SerializeForSnapshot()
        {
            int count = 0;
            int totalSize = 1; // u8 count
            foreach (var v in variables)
            {
                if (!v.persistForLateJoiners) continue;
                if (v.currentValue == null || v.currentValue.Length == 0) continue;
                byte[] nameBytes = Encoding.UTF8.GetBytes(v.name);
                totalSize += 1 + nameBytes.Length + 1 + 2 + v.currentValue.Length;
                count++;
            }

            if (count == 0) return null;

            byte[] buf = new byte[totalSize];
            buf[0] = (byte)count;
            int offset = 1;

            foreach (var v in variables)
            {
                if (!v.persistForLateJoiners) continue;
                if (v.currentValue == null || v.currentValue.Length == 0) continue;

                byte[] nameBytes = Encoding.UTF8.GetBytes(v.name);
                buf[offset++] = (byte)nameBytes.Length;
                Array.Copy(nameBytes, 0, buf, offset, nameBytes.Length);
                offset += nameBytes.Length;

                buf[offset++] = (byte)v.type;

                Protocol.WriteU16(buf, offset, (ushort)v.currentValue.Length);
                offset += 2;

                Array.Copy(v.currentValue, 0, buf, offset, v.currentValue.Length);
                offset += v.currentValue.Length;
            }

            return buf;
        }

        /// <summary>
        /// Deserialize snapshot data (for late joiners).
        /// </summary>
        public void ApplySnapshot(byte[] data, int startOffset)
        {
            if (startOffset >= data.Length) return;
            if (!_initialized) InitializeVariables();
            int count = data[startOffset];
            int offset = startOffset + 1;

            for (int i = 0; i < count; i++)
            {
                if (offset >= data.Length) break;
                int nameLen = data[offset++];
                if (nameLen < 0 || offset + nameLen + 3 > data.Length) break; // name + type(1) + valLen(2)
                string name = Encoding.UTF8.GetString(data, offset, nameLen);
                offset += nameLen;

                if (offset >= data.Length) break;
                VarType type = (VarType)data[offset++];

                if (offset + 2 > data.Length) break;
                int valLen = Protocol.ReadU16(data, offset);
                offset += 2;

                if (valLen < 0 || offset + valLen > data.Length) break;
                byte[] value = new byte[valLen];
                if (valLen > 0)
                    Array.Copy(data, offset, value, 0, valLen);
                offset += valLen;

                var v = GetOrCreate(name, type);
                if (v == null) continue;
                v.currentValue = value;
                v.dirty = false;

                OnValueChanged?.Invoke(name, type);
                OnRemoteValueChanged?.Invoke(name, type);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private VarDefinition GetVar(string name)
        {
            if (!_initialized) InitializeVariables();
            VarDefinition v;
            _varMap.TryGetValue(name, out v);
            return v;
        }

        private VarDefinition GetOrCreate(string name, VarType type)
        {
            if (!_initialized) InitializeVariables();
            VarDefinition v;
            if (_varMap.TryGetValue(name, out v))
            {
                if (v.type == type) return v;
                Debug.LogError($"[NetVars] '{name}' has type {v.type}, not {type}.");
                return null;
            }

            v = new VarDefinition
            {
                name         = name,
                type         = type,
                currentValue = SerializeDefault(type, ""),
                nameBytes    = Encoding.UTF8.GetBytes(name),
                dirty        = false
            };
            variables.Add(v);
            _varMap[name] = v;
            return v;
        }

        private static byte[] SerializeDefault(VarType type, string defaultStr)
        {
            if (string.IsNullOrEmpty(defaultStr))
            {
                switch (type)
                {
                    case VarType.Int: return BitConverter.GetBytes(0);
                    case VarType.Float: return BitConverter.GetBytes(0f);
                    case VarType.Bool: return new byte[] { 0 };
                    case VarType.String: return new byte[0];
                    default: return new byte[0];
                }
            }

            switch (type)
            {
                case VarType.Int:
                    int iv; int.TryParse(defaultStr, out iv);
                    return BitConverter.GetBytes(iv);
                case VarType.Float:
                    float fv; float.TryParse(defaultStr, out fv);
                    return BitConverter.GetBytes(fv);
                case VarType.Bool:
                    return new byte[] { (byte)(defaultStr.ToLower() == "true" ? 1 : 0) };
                case VarType.String:
                    return Encoding.UTF8.GetBytes(defaultStr);
                default:
                    return new byte[0];
            }
        }

    }
}
