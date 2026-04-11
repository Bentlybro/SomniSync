using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Syncs Animator state and triggers across clients.
    /// Add alongside NetworkObject + an Animator component.
    /// 
    /// Syncs:
    /// - Current state hash + normalized time
    /// - Bool/Int/Float parameters (change detection)
    /// - Triggers (fire-and-forget via events)
    /// 
    /// Usage:
    ///   var netAnim = GetComponent&lt;NetworkAnimator&gt;();
    ///   netAnim.SetTrigger("attack");  // Syncs to all clients
    ///   netAnim.SetBool("walking", true);  // Syncs to all clients
    /// </summary>
    public class NetworkAnimator : MonoBehaviour
    {
        // Protocol: uses 0x07 for animator state sync
        // [0x07][u16 object_id][i32 state_hash][f32 normalized_time][u8 param_count][per param: u8 name_len, name, u8 type, 4B value]

        [Header("Sync Settings")]
        [Tooltip("How often to check for state changes (per second)")]
        public int syncRate = 10;

        private NetworkObject _netObj;
        private Animator _animator;
        private NetworkEvents _events;
        private float _syncInterval;
        private float _nextSyncTime;

        // Cached parameter state for change detection
        private struct ParamCache
        {
            public string name;
            public AnimatorControllerParameterType type;
            public int intValue;
            public float floatValue;
            public bool boolValue;
        }

        private ParamCache[] _paramCache;
        private byte[][] _paramNameBytes; // cached UTF-8 name encodings to avoid per-frame allocation
        private int[] _dirtyIndices;      // reused dirty-param scratch buffer
        private int _lastStateHash;
        private float _lastNormalizedTime;

        private void Start()
        {
            _netObj = GetComponent<NetworkObject>();
            _animator = GetComponent<Animator>();

            if (_netObj == null || _animator == null)
            {
                Debug.LogError("[NetAnim] Requires NetworkObject + Animator on the same GameObject!");
                enabled = false;
                return;
            }

            _syncInterval = 1f / syncRate;
            _events = GetComponent<NetworkEvents>();

            // Cache initial parameter state + pre-encode names to avoid per-frame allocs
            var parameters = _animator.parameters;
            _paramCache = new ParamCache[parameters.Length];
            _paramNameBytes = new byte[parameters.Length][];
            _dirtyIndices = new int[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                _paramCache[i] = new ParamCache
                {
                    name = p.name,
                    type = p.type,
                    intValue = p.type == AnimatorControllerParameterType.Int ? _animator.GetInteger(p.name) : 0,
                    floatValue = p.type == AnimatorControllerParameterType.Float ? _animator.GetFloat(p.name) : 0f,
                    boolValue = p.type == AnimatorControllerParameterType.Bool ? _animator.GetBool(p.name) : false
                };
                _paramNameBytes[i] = Encoding.UTF8.GetBytes(p.name);
            }
        }

        private void Update()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            if (!HasAuthority()) return;

            if (Time.time < _nextSyncTime) return;
            _nextSyncTime = Time.time + _syncInterval;

            CheckAndSync();
        }

        // =====================================================================
        // Public API — set animator params with network sync
        // =====================================================================

        /// <summary>Set a trigger on all clients.</summary>
        public void SetTrigger(string name)
        {
            if (_animator != null) _animator.SetTrigger(name);
            SendTrigger(name);
        }

        /// <summary>Set a bool parameter on all clients.</summary>
        public void SetBool(string name, bool value)
        {
            if (_animator != null) _animator.SetBool(name, value);
            // Will be picked up by change detection on next sync
        }

        /// <summary>Set an int parameter on all clients.</summary>
        public void SetInteger(string name, int value)
        {
            if (_animator != null) _animator.SetInteger(name, value);
        }

        /// <summary>Set a float parameter on all clients.</summary>
        public void SetFloat(string name, float value)
        {
            if (_animator != null) _animator.SetFloat(name, value);
        }

        // =====================================================================
        // Change Detection & Sync
        // =====================================================================

        private void CheckAndSync()
        {
            if (_animator == null || _paramCache == null) return;

            var stateInfo = _animator.GetCurrentAnimatorStateInfo(0);
            bool stateChanged = stateInfo.fullPathHash != _lastStateHash;
            int dirtyCount = 0;

            // Check parameters for changes — write into pre-allocated scratch array, no List alloc
            for (int i = 0; i < _paramCache.Length; i++)
            {
                var cache = _paramCache[i];
                bool dirty = false;

                switch (cache.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        bool bv = _animator.GetBool(cache.name);
                        if (bv != cache.boolValue) { _paramCache[i].boolValue = bv; dirty = true; }
                        break;
                    case AnimatorControllerParameterType.Int:
                        int iv = _animator.GetInteger(cache.name);
                        if (iv != cache.intValue) { _paramCache[i].intValue = iv; dirty = true; }
                        break;
                    case AnimatorControllerParameterType.Float:
                        float fv = _animator.GetFloat(cache.name);
                        if (Mathf.Abs(fv - cache.floatValue) > 0.01f) { _paramCache[i].floatValue = fv; dirty = true; }
                        break;
                }

                if (dirty)
                    _dirtyIndices[dirtyCount++] = i;
            }

            if (!stateChanged && dirtyCount == 0) return;

            _lastStateHash = stateInfo.fullPathHash;
            _lastNormalizedTime = stateInfo.normalizedTime;

            // Build message using pre-cached name byte arrays — no per-frame allocs
            // [0x07][u16 obj_id][i32 state_hash][f32 norm_time][u8 dirty_count][per param: u8 name_len, name, u8 type, 4B value]
            int size = 1 + 2 + 4 + 4 + 1; // header
            for (int i = 0; i < dirtyCount; i++)
                size += 1 + _paramNameBytes[_dirtyIndices[i]].Length + 1 + 4;

            byte[] buf = new byte[size];
            int offset = 0;
            buf[offset++] = 0x07;
            buf[offset++] = (byte)(_netObj.LocalId & 0xFF);
            buf[offset++] = (byte)(_netObj.LocalId >> 8);

            byte[] hashBytes = BitConverter.GetBytes(_lastStateHash);
            Array.Copy(hashBytes, 0, buf, offset, 4); offset += 4;
            byte[] timeBytes = BitConverter.GetBytes(_lastNormalizedTime);
            Array.Copy(timeBytes, 0, buf, offset, 4); offset += 4;

            buf[offset++] = (byte)dirtyCount;

            for (int i = 0; i < dirtyCount; i++)
            {
                int idx = _dirtyIndices[i];
                var cache = _paramCache[idx];
                byte[] nameBytes = _paramNameBytes[idx]; // use cached encoding
                buf[offset++] = (byte)nameBytes.Length;
                Array.Copy(nameBytes, 0, buf, offset, nameBytes.Length);
                offset += nameBytes.Length;

                byte[] valBytes;
                switch (cache.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        buf[offset++] = 2; // bool type
                        valBytes = BitConverter.GetBytes(cache.boolValue ? 1 : 0);
                        break;
                    case AnimatorControllerParameterType.Int:
                        buf[offset++] = 0; // int type
                        valBytes = BitConverter.GetBytes(cache.intValue);
                        break;
                    case AnimatorControllerParameterType.Float:
                        buf[offset++] = 1; // float type
                        valBytes = BitConverter.GetBytes(cache.floatValue);
                        break;
                    default:
                        buf[offset++] = 0;
                        valBytes = new byte[4];
                        break;
                }
                Array.Copy(valBytes, 0, buf, offset, 4); offset += 4;
            }

            NetworkManager.Instance.SendBinaryPublic(buf);
        }

        private void SendTrigger(string triggerName)
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;

            // Use cached NetworkEvents if available (GetComponent already called in Start)
            if (_events != null)
            {
                _events.Send("_anim_trigger", triggerName);
                return;
            }

            // Fallback: send as 0x07 with special marker
            byte[] nameBytes = Encoding.UTF8.GetBytes(triggerName);
            byte[] buf = new byte[1 + 2 + 4 + 4 + 1 + 1 + nameBytes.Length + 1 + 4];
            int offset = 0;
            buf[offset++] = 0x07;
            buf[offset++] = (byte)(_netObj.LocalId & 0xFF);
            buf[offset++] = (byte)(_netObj.LocalId >> 8);
            // state hash = 0 (means "trigger only")
            offset += 4;
            offset += 4;
            buf[offset++] = 1; // 1 param
            buf[offset++] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, buf, offset, nameBytes.Length);
            offset += nameBytes.Length;
            buf[offset++] = 4; // type 4 = trigger
            offset += 4;

            NetworkManager.Instance.SendBinaryPublic(buf);
        }

        /// <summary>Called by NetworkManager when receiving 0x07 for this object.</summary>
        public void ApplyRemoteState(byte[] data, int startOffset)
        {
            if (_animator == null) return;
            if (startOffset + 9 > data.Length) return; // need stateHash(4) + normTime(4) + paramCount(1)

            int offset = startOffset;
            int stateHash = BitConverter.ToInt32(data, offset); offset += 4;
            float normalizedTime = BitConverter.ToSingle(data, offset); offset += 4;
            int paramCount = data[offset++];

            // Apply parameters
            for (int i = 0; i < paramCount; i++)
            {
                if (offset >= data.Length) break;
                int nameLen = data[offset++];
                if (nameLen < 0 || offset + nameLen + 5 > data.Length) break; // name + type(1) + value(4)
                string paramName = Encoding.UTF8.GetString(data, offset, nameLen);
                offset += nameLen;

                if (offset >= data.Length) break;
                byte type = data[offset++];

                if (offset + 4 > data.Length) break;
                switch (type)
                {
                    case 0: // int
                        _animator.SetInteger(paramName, BitConverter.ToInt32(data, offset));
                        break;
                    case 1: // float
                        _animator.SetFloat(paramName, BitConverter.ToSingle(data, offset));
                        break;
                    case 2: // bool
                        _animator.SetBool(paramName, BitConverter.ToInt32(data, offset) != 0);
                        break;
                    case 4: // trigger
                        _animator.SetTrigger(paramName);
                        break;
                }
                offset += 4;
            }

            // Crossfade to state if changed
            if (stateHash != 0 && stateHash != _lastStateHash)
            {
                _animator.Play(stateHash, 0, normalizedTime);
                _lastStateHash = stateHash;
            }
        }

        private bool HasAuthority() => _netObj.HasAuthority();
    }
}
