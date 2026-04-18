using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Sync custom variables (int, float, bool, string) across clients.
    /// Add alongside NetworkObject. Any client can set variables on objects
    /// they own (master owns unowned objects, spectators own claimed objects).
    /// </summary>
    public class NetworkVariables : MonoBehaviour
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

        private Dictionary<string, VarDefinition> _varMap = new Dictionary<string, VarDefinition>();
        private NetworkObject _netObj;
        private bool _initialized;

        // =====================================================================
        // Init
        // =====================================================================

        protected virtual void Start()
        {
            _netObj = GetComponent<NetworkObject>();
            if (_netObj == null)
            {
                Debug.LogError("[NetVars] NetworkVariables requires a NetworkObject on the same GameObject!");
                enabled = false;
                return;
            }

            InitializeVariables();
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

            // Collect dirty OnChange vars
            bool hasDirty = false;
            foreach (var v in variables)
            {
                if (v.dirty && v.syncMode == SyncMode.OnChange)
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
            var v = GetOrCreate(name, VarType.Int);
            v.currentValue = BitConverter.GetBytes(value);
            MarkDirty(v);
        }

        public void SetFloat(string name, float value)
        {
            var v = GetOrCreate(name, VarType.Float);
            v.currentValue = BitConverter.GetBytes(value);
            MarkDirty(v);
        }

        public void SetBool(string name, bool value)
        {
            var v = GetOrCreate(name, VarType.Bool);
            v.currentValue = new byte[] { (byte)(value ? 1 : 0) };
            MarkDirty(v);
        }

        public void SetString(string name, string value)
        {
            var v = GetOrCreate(name, VarType.String);
            v.currentValue = Encoding.UTF8.GetBytes(value ?? "");
            MarkDirty(v);
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
        private bool HasAuthority() => _netObj.HasAuthority();

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
            // Count dirty vars — use cached nameBytes to avoid double encoding
            int count = 0;
            int totalSize = 4; // 0x04 + u16 obj_id + u8 count
            foreach (var v in variables)
            {
                if (!v.dirty) continue;
                if (immediateOnly && v.syncMode != SyncMode.Immediate) continue;
                // u8 name_len + name + u8 type + u16 value_len + value
                int nameBytesLen = v.nameBytes?.Length ?? Encoding.UTF8.GetByteCount(v.name);
                totalSize += 1 + nameBytesLen + 1 + 2 + (v.currentValue?.Length ?? 0);
                count++;
            }

            if (count == 0) return;
            int sendCount = Math.Min(count, 255);

            byte[] buf = new byte[totalSize];
            buf[0] = 0x04;
            WriteU16(buf, 1, _netObj.LocalId);
            buf[3] = (byte)sendCount;

            int offset = 4;
            int written = 0;
            foreach (var v in variables)
            {
                if (written >= sendCount) break;
                if (!v.dirty) continue;
                if (immediateOnly && v.syncMode != SyncMode.Immediate) continue;

                byte[] nameBytes = v.nameBytes ?? Encoding.UTF8.GetBytes(v.name);
                buf[offset++] = (byte)nameBytes.Length;
                Array.Copy(nameBytes, 0, buf, offset, nameBytes.Length);
                offset += nameBytes.Length;

                buf[offset++] = (byte)v.type;

                int valLen = v.currentValue?.Length ?? 0;
                WriteU16(buf, offset, (ushort)valLen);
                offset += 2;

                if (valLen > 0)
                {
                    Array.Copy(v.currentValue, 0, buf, offset, valLen);
                    offset += valLen;
                }

                v.dirty = false;
                written++;
            }

            NetworkManager.Instance.SendBinaryPublic(buf);
        }

        // =====================================================================
        // Deserialization — Receive (called by NetworkManager)
        // =====================================================================

        /// <summary>
        /// Apply a received 0x04 variable update.
        /// Called by NetworkManager when it receives 0x04 for this object.
        /// </summary>
        public void ApplyRemoteUpdate(byte[] data, int startOffset, int varCount)
        {
            int offset = startOffset;
            for (int i = 0; i < varCount; i++)
            {
                if (offset >= data.Length) break;
                int nameLen = data[offset++];
                if (nameLen < 0 || offset + nameLen + 3 > data.Length) break; // name + type(1) + valLen(2)
                string name = Encoding.UTF8.GetString(data, offset, nameLen);
                offset += nameLen;

                if (offset >= data.Length) break;
                VarType type = (VarType)data[offset++];

                if (offset + 2 > data.Length) break;
                int valLen = ReadU16(data, offset);
                offset += 2;

                if (valLen < 0 || offset + valLen > data.Length) break;
                byte[] value = new byte[valLen];
                if (valLen > 0)
                    Array.Copy(data, offset, value, 0, valLen);
                offset += valLen;

                // Apply to local state
                var v = GetOrCreate(name, type);
                v.currentValue = value;
                v.dirty = false;

                OnValueChanged?.Invoke(name, type);
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

                WriteU16(buf, offset, (ushort)v.currentValue.Length);
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
                int valLen = ReadU16(data, offset);
                offset += 2;

                if (valLen < 0 || offset + valLen > data.Length) break;
                byte[] value = new byte[valLen];
                if (valLen > 0)
                    Array.Copy(data, offset, value, 0, valLen);
                offset += valLen;

                var v = GetOrCreate(name, type);
                v.currentValue = value;
                v.dirty = false;

                OnValueChanged?.Invoke(name, type);
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
            VarDefinition v;
            if (_varMap.TryGetValue(name, out v)) return v;

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

        private static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadU16(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        }
    }
}
