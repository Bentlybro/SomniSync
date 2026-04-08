using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Fire-and-forget networked events (RPCs).
    /// Send events from any client, all others receive them.
    /// Events are NOT persistent — late joiners won't see past events.
    /// 
    /// Usage:
    ///   var events = GetComponent&lt;NetworkEvents&gt;();
    ///   events.On("explode", () => PlayVFX());
    ///   events.On("damage", (int amount) => TakeDamage(amount));
    ///   events.Send("explode");
    ///   events.Send("damage", 25);
    /// </summary>
    /// <summary>Who should receive the event.</summary>
    public enum EventTarget : byte
    {
        All = 0,        // Everyone including sender
        Authority = 1,  // Only the client with authority over this object
        Proxies = 2     // Everyone EXCEPT the authority
    }

    public class NetworkEvents : MonoBehaviour
    {
        // Protocol: 0x06 = event message
        // [0x06][u16 object_id][u8 target][u8 name_len][name][u8 param_count][per param: u8 type, u16 len, value]

        private NetworkObject _netObj;
        private Dictionary<string, List<Action<object[]>>> _handlers = new Dictionary<string, List<Action<object[]>>>();
        private readonly List<byte[]> _paramDataCache = new List<byte[]>();

        private void Start()
        {
            _netObj = GetComponent<NetworkObject>();
            if (_netObj == null)
            {
                Debug.LogError("[NetEvents] Requires NetworkObject on the same GameObject!");
                enabled = false;
            }
        }

        // =====================================================================
        // Public API — Listen
        // =====================================================================

        /// <summary>Listen for an event with no parameters.</summary>
        public void On(string eventName, Action callback)
        {
            Register(eventName, args => callback());
        }

        /// <summary>Listen for an event with one int parameter.</summary>
        public void On(string eventName, Action<int> callback)
        {
            Register(eventName, args => callback(args.Length > 0 ? (int)args[0] : 0));
        }

        /// <summary>Listen for an event with one float parameter.</summary>
        public void On(string eventName, Action<float> callback)
        {
            Register(eventName, args => callback(args.Length > 0 ? (float)args[0] : 0f));
        }

        /// <summary>Listen for an event with one string parameter.</summary>
        public void On(string eventName, Action<string> callback)
        {
            Register(eventName, args => callback(args.Length > 0 ? (string)args[0] : ""));
        }

        /// <summary>Listen for an event with one bool parameter.</summary>
        public void On(string eventName, Action<bool> callback)
        {
            Register(eventName, args => callback(args.Length > 0 ? (bool)args[0] : false));
        }

        /// <summary>Listen for an event with raw params array.</summary>
        public void OnRaw(string eventName, Action<object[]> callback)
        {
            Register(eventName, callback);
        }

        /// <summary>Remove all listeners for an event.</summary>
        public void Off(string eventName)
        {
            _handlers.Remove(eventName);
        }

        // =====================================================================
        // Public API — Send
        // =====================================================================

        /// <summary>Send event with no parameters.</summary>
        public void Send(string eventName)
        {
            SendEvent(eventName, null);
        }

        /// <summary>Send event with one int parameter.</summary>
        public void Send(string eventName, int value)
        {
            SendEvent(eventName, new object[] { value });
        }

        /// <summary>Send event with one float parameter.</summary>
        public void Send(string eventName, float value)
        {
            SendEvent(eventName, new object[] { value });
        }

        /// <summary>Send event with one string parameter.</summary>
        public void Send(string eventName, string value)
        {
            SendEvent(eventName, new object[] { value });
        }

        /// <summary>Send event with one bool parameter.</summary>
        public void Send(string eventName, bool value)
        {
            SendEvent(eventName, new object[] { value });
        }

        /// <summary>Send event with multiple parameters.</summary>
        public void Send(string eventName, params object[] parameters)
        {
            SendEvent(eventName, parameters);
        }

        // =====================================================================
        // Targeted RPCs
        // =====================================================================

        /// <summary>Send event only to the client that has authority over this object.</summary>
        public void SendToAuthority(string eventName, params object[] parameters)
        {
            SendEvent(eventName, parameters, target: EventTarget.Authority);
        }

        /// <summary>Send event to everyone EXCEPT the authority (proxies only).</summary>
        public void SendToProxies(string eventName, params object[] parameters)
        {
            SendEvent(eventName, parameters, target: EventTarget.Proxies);
        }

        // =====================================================================
        // Serialization
        // =====================================================================

        private void SendEvent(string eventName, object[] parameters, EventTarget target = EventTarget.All)
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;

            byte[] nameBytes = Encoding.UTF8.GetBytes(eventName);
            int paramCount = parameters?.Length ?? 0;

            // Calculate size — reuse instance list, clear instead of allocate
            int size = 1 + 2 + 1 + 1 + nameBytes.Length + 1; // type + obj_id + target + name_len + name + param_count
            _paramDataCache.Clear();

            for (int i = 0; i < paramCount; i++)
            {
                byte type;
                byte[] val;
                SerializeParam(parameters[i], out type, out val);
                // u8 type + u16 len + value
                size += 1 + 2 + val.Length;
                _paramDataCache.Add(new byte[] { type });
                _paramDataCache.Add(val);
            }

            byte[] buf = new byte[size];
            int offset = 0;
            buf[offset++] = 0x06;
            buf[offset++] = (byte)(_netObj.LocalId & 0xFF);
            buf[offset++] = (byte)(_netObj.LocalId >> 8);
            buf[offset++] = (byte)target;
            buf[offset++] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, buf, offset, nameBytes.Length);
            offset += nameBytes.Length;
            buf[offset++] = (byte)paramCount;

            for (int i = 0; i < paramCount; i++)
            {
                buf[offset++] = _paramDataCache[i * 2][0]; // type byte
                byte[] val = _paramDataCache[i * 2 + 1];
                buf[offset++] = (byte)(val.Length & 0xFF);
                buf[offset++] = (byte)(val.Length >> 8);
                Array.Copy(val, 0, buf, offset, val.Length);
                offset += val.Length;
            }

            NetworkManager.Instance.SendBinaryPublic(buf);

            // Fire locally if targeting allows it
            bool isAuthority = _netObj.HasAuthority();
            bool shouldFireLocally = target == EventTarget.All ||
                                     (target == EventTarget.Authority && isAuthority) ||
                                     (target == EventTarget.Proxies && !isAuthority);
            if (shouldFireLocally)
                FireEvent(eventName, parameters ?? new object[0]);
        }

        /// <summary>Called by NetworkManager when receiving 0x06.</summary>
        public void ApplyRemoteEvent(byte[] data, int startOffset, int length)
        {
            if (startOffset + 3 > length) return; // need target(1) + nameLen(1) + paramCount(1) minimum
            int offset = startOffset;
            EventTarget target = (EventTarget)data[offset++];

            // Filter by target
            bool isAuthority = _netObj.HasAuthority();
            if (target == EventTarget.Authority && !isAuthority) return;
            if (target == EventTarget.Proxies && isAuthority) return;

            if (offset >= length) return;
            int nameLen = data[offset++];
            if (nameLen < 0 || offset + nameLen + 1 > length) return;
            string eventName = Encoding.UTF8.GetString(data, offset, nameLen);
            offset += nameLen;

            if (offset >= length) return;
            int paramCount = data[offset++];

            object[] parameters = new object[paramCount];
            for (int i = 0; i < paramCount; i++)
            {
                if (offset + 3 > length) break; // need type(1) + valLen(2)
                byte type = data[offset++];
                int valLen = data[offset] | (data[offset + 1] << 8);
                offset += 2;
                if (valLen < 0 || offset + valLen > length) break;
                parameters[i] = DeserializeParam(type, data, offset, valLen);
                offset += valLen;
            }

            FireEvent(eventName, parameters);
        }

        // =====================================================================
        // Internal
        // =====================================================================

        private void Register(string eventName, Action<object[]> handler)
        {
            if (!_handlers.ContainsKey(eventName))
                _handlers[eventName] = new List<Action<object[]>>();
            _handlers[eventName].Add(handler);
        }

        private void FireEvent(string eventName, object[] parameters)
        {
            List<Action<object[]>> handlers;
            if (_handlers.TryGetValue(eventName, out handlers))
            {
                for (int i = 0; i < handlers.Count; i++)
                {
                    try { handlers[i](parameters); }
                    catch (Exception e) { Debug.LogError($"[NetEvents] Handler error for '{eventName}': {e}"); }
                }
            }
        }

        // Type: 0=int, 1=float, 2=bool, 3=string
        private static void SerializeParam(object param, out byte type, out byte[] value)
        {
            if (param is int)
            {
                type = 0;
                value = BitConverter.GetBytes((int)param);
            }
            else if (param is float)
            {
                type = 1;
                value = BitConverter.GetBytes((float)param);
            }
            else if (param is bool)
            {
                type = 2;
                value = new byte[] { (byte)((bool)param ? 1 : 0) };
            }
            else
            {
                type = 3;
                value = Encoding.UTF8.GetBytes(param?.ToString() ?? "");
            }
        }

        private static object DeserializeParam(byte type, byte[] data, int offset, int length)
        {
            switch (type)
            {
                case 0: return length >= 4 ? BitConverter.ToInt32(data, offset) : 0;
                case 1: return length >= 4 ? BitConverter.ToSingle(data, offset) : 0f;
                case 2: return length >= 1 && data[offset] != 0;
                case 3: return Encoding.UTF8.GetString(data, offset, length);
                default: return null;
            }
        }
    }
}
