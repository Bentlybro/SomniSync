using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles all incoming binary messages from the server.
    /// </summary>
    public partial class NetworkManager
    {
        private void ProcessBinary(byte[] data, int length)
        {
            byte msgType = data[0];

            switch (msgType)
            {
                case 0x01: HandlePositionUpdates(data, length);         break;
                case 0x02: HandleSnapshot(data, length);                break;
                case 0x03: HandleInteractionUpdates(data, length);      break;
                case 0x04: HandleVariableUpdate(data, length);          break;
                case 0x05: HandleScaledPositionUpdates(data, length);   break;
                case 0x06: HandleEvent(data, length);                   break;
                case 0x07: HandleAnimatorState(data, length);           break;
                case 0x08: HandleRigidbodyUpdate(data, length);         break;
                case 0x09: HandleScaledInteractionUpdates(data, length); break;
                case 0x0A: HandleScaledSnapshot(data, length);          break;
                case 0x0B: HandlePositionVelocityUpdates(data, length); break;
                case 0xFF:
                    if (length >= 2)
                    {
                        if (data[1] == Protocol.CTRL_PERSIST_DATA)
                            HandlePersistData(data, length);
                        else
                            HandleControl(data, length);
                    }
                    break;
            }
        }

        // =====================================================================
        // Control Messages (0xFF)
        // =====================================================================

        private void HandleControl(byte[] data, int length)
        {
            byte ctrl = data[1];

            switch (ctrl)
            {
                case 0x01: // You are master
                    if (length < 4) break;
                    ClientId = BitConverter.ToUInt16(data, 2);
                    IsMaster = true;
                    Debug.Log($"[Net] Assigned as MASTER (client_{ClientId})");
                    AssignAllAuthorityToSelf();
                    SendSnapshot();
                    NetworkPersistence.OnNetworkReady(this);
                    if (!IsNetworkReady) { IsNetworkReady = true; OnNetworkReady?.Invoke(); }
                    OnBecameMaster?.Invoke();
                    break;

                case 0x02: // You are spectator
                    if (length < 4) break;
                    ClientId = BitConverter.ToUInt16(data, 2);
                    IsMaster = false;
                    Debug.Log($"[Net] Assigned as SPECTATOR (client_{ClientId})");
                    NetworkPersistence.OnNetworkReady(this);
                    if (!IsNetworkReady) { IsNetworkReady = true; OnNetworkReady?.Invoke(); }
                    OnBecameSpectator?.Invoke();
                    break;

                case 0x03: // Send snapshot (master only)
                    if (length < 4) break;
                    ushort targetClient = BitConverter.ToUInt16(data, 2);
                    Debug.Log($"[Net] Snapshot requested for client_{targetClient}");
                    SendSnapshot();
                    break;

                case 0x04: // Master changed (you're the new master)
                    IsMaster = true;
                    Debug.Log($"[Net] Promoted to MASTER");
                    AssignAllAuthorityToSelf();
                    SendSnapshot();
                    OnBecameMaster?.Invoke();
                    break;

                case 0x0A: // Authority assignment: [u16 clientId][u16 count][u16... objectIds]
                    if (length >= 7)
                    {
                        ushort assignedClient = Protocol.ReadU16(data, 2);
                        ushort assignCount    = Protocol.ReadU16(data, 4);
                        int aOffset = 6;
                        for (int i = 0; i < assignCount && aOffset + 2 <= length; i++)
                        {
                            ushort objId = Protocol.ReadU16(data, aOffset); aOffset += 2;
                            if (_objectMap.TryGetValue(objId, out var assignObj))
                                assignObj.SetStateAuthority(assignedClient);
                        }
                        Debug.Log($"[Net] Authority: client_{assignedClient} now owns {assignCount} objects");
                    }
                    break;

                case 0x05: // Remote client claimed an object
                    if (length >= 6)
                    {
                        ushort claimObjId    = BitConverter.ToUInt16(data, 2);
                        ushort claimClientId = BitConverter.ToUInt16(data, 4);
                        if (_objectMap.TryGetValue(claimObjId, out var claimObj))
                            claimObj.OnRemoteClaim(claimClientId);
                    }
                    break;

                case 0x06: // Remote client released an object
                    if (length >= 6)
                    {
                        ushort releaseObjId = BitConverter.ToUInt16(data, 2);
                        if (_objectMap.TryGetValue(releaseObjId, out var releaseObj))
                            releaseObj.OnRemoteRelease();
                    }
                    break;

                case 0x07: // Player position (for AOI)
                    if (length >= 10)
                    {
                        ushort posClientId = BitConverter.ToUInt16(data, 2);
                        float px = Protocol.ReadHalf(data, 4);
                        float py = Protocol.ReadHalf(data, 6);
                        float pz = Protocol.ReadHalf(data, 8);
                        if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz))
                        {
                            Debug.LogWarning($"[Net] Received invalid player position from client_{posClientId}, ignoring");
                            break;
                        }
                        _playerPositions[posClientId] = new Vector3(px, py, pz);
                        _playerLastSeen[posClientId]  = Time.time;
                    }
                    break;

                case 0xFE: // Server error notification
                    if (length >= 2)
                        Debug.LogError($"[Net] Server error: snapshot too large to relay — reduce scene object count or split into smaller snapshots");
                    break;
            }
        }

        // =====================================================================
        // Position + velocity updates (0x0B) — dead reckoning
        // =====================================================================

        private void HandlePositionVelocityUpdates(byte[] data, int length)
        {
            if (!_sceneReady || length < 7) return;

            ushort senderClientId = Protocol.ReadU16(data, 1);
            ushort sequence       = Protocol.ReadU16(data, 3);

            if (_receivedSequences.TryGetValue(senderClientId, out ushort lastSeq))
            {
                ushort diff = unchecked((ushort)(sequence - lastSeq));
                if (diff == 0 || diff > 32768) return; // duplicate or out-of-order
            }
            _receivedSequences[senderClientId] = sequence;

            ushort count = Protocol.ReadU16(data, 5);
            int offset = 7;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_VELOCITY <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot, vel;
                offset = Protocol.ReadEntityVelocity(data, offset, out localId, out pos, out rot, out vel);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (obj.HasStateAuthority) continue;
                    if (obj.IsClaimedLocally)  continue;
                    if (_snapFramesRemaining > 0)
                        obj.SnapTo(pos, rot);
                    else
                        obj.ApplyUpdate(pos, rot, vel);
                }
            }

            if (_snapFramesRemaining > 0)
                _snapFramesRemaining--;
        }

        // =====================================================================
        // Legacy position updates (0x01)
        // =====================================================================

        private void HandlePositionUpdates(byte[] data, int length)
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
                    if (obj.HasStateAuthority) continue;
                    if (obj.IsClaimedLocally)  continue;
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
        // Interaction updates (0x03)
        // =====================================================================

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
                        obj.ApplyUpdate(pos, rot);
                }
            }
        }

        // =====================================================================
        // Scaled position updates (0x05)
        // =====================================================================

        private void HandleScaledPositionUpdates(byte[] data, int length)
        {
            if (!_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_SCALED <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot, scale;
                offset = Protocol.ReadEntityScaled(data, offset, out localId, out pos, out rot, out scale);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (obj.HasStateAuthority) continue;
                    if (obj.IsClaimedLocally)  continue;
                    if (_snapFramesRemaining > 0)
                        obj.SnapTo(pos, rot, scale);
                    else
                        obj.ApplyUpdate(pos, rot, scale);
                }
            }
        }

        // =====================================================================
        // Scaled interaction updates (0x09)
        // =====================================================================

        private void HandleScaledInteractionUpdates(byte[] data, int length)
        {
            if (!_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_SCALED <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot, scale;
                offset = Protocol.ReadEntityScaled(data, offset, out localId, out pos, out rot, out scale);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (!obj.IsClaimedLocally)
                        obj.ApplyUpdate(pos, rot, scale);
                }
            }
        }

        // =====================================================================
        // Snapshot (0x02)
        // =====================================================================

        private void HandleSnapshot(byte[] data, int length)
        {
            if (!_sceneReady) return;

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
                    if (obj.HasStateAuthority) continue;
                    obj.SnapTo(pos, rot);
                    applied++;
                }
            }

            _snapFramesRemaining = SNAP_FRAMES_AFTER_SNAPSHOT;
            Debug.Log($"[Net] Snapshot applied: {applied}/{count} objects");
        }

        // =====================================================================
        // Scaled Snapshot (0x0A) — full state for new clients, scaled objects
        // =====================================================================

        private void HandleScaledSnapshot(byte[] data, int length)
        {
            if (!_sceneReady) return;

            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;
            int applied = 0;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_SCALED <= length; i++)
            {
                ushort localId;
                Vector3 pos, rot, scale;
                offset = Protocol.ReadEntityScaled(data, offset, out localId, out pos, out rot, out scale);

                if (_objectMap.TryGetValue(localId, out var obj))
                {
                    if (obj.HasStateAuthority) continue;
                    obj.SnapTo(pos, rot, scale);
                    applied++;
                }
            }

            _snapFramesRemaining = SNAP_FRAMES_AFTER_SNAPSHOT;
            Debug.Log($"[Net] Scaled snapshot applied: {applied}/{count} objects");
        }

        // =====================================================================
        // Variable Updates (0x04)
        // =====================================================================

        private void HandleVariableUpdate(byte[] data, int length)
        {
            if (length < 4) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));
            int varCount = data[3];

            if (_objectMap.TryGetValue(objId, out var netVarObj))
            {
                var netVars = netVarObj.GetComponent<NetworkVariables>();
                if (netVars != null)
                    netVars.ApplyRemoteUpdate(data, 4, varCount);
            }
        }

        // =====================================================================
        // Events (0x06)
        // =====================================================================

        private void HandleEvent(byte[] data, int length)
        {
            if (length < 4) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (_objectMap.TryGetValue(objId, out var eventObj))
            {
                var netEvents = eventObj.GetComponent<NetworkEvents>();
                if (netEvents != null)
                    netEvents.ApplyRemoteEvent(data, 3, length);
            }
        }

        // =====================================================================
        // Animator State (0x07)
        // =====================================================================

        private void HandleAnimatorState(byte[] data, int length)
        {
            if (length < 10) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (_objectMap.TryGetValue(objId, out var animObj))
            {
                var netAnim = animObj.GetComponent<NetworkAnimator>();
                if (netAnim != null)
                    netAnim.ApplyRemoteState(data, 3);
            }
        }

        // =====================================================================
        // Persistence response (0xFF / 0x23) — server sending back a loaded value
        // =====================================================================

        internal void HandlePersistData(byte[] data, int length)
        {
            // [0xFF][0x23][u16 req_id][u8 found][u16 val_len][val...]
            if (length < 7) return;
            ushort reqId  = Protocol.ReadU16(data, 2);
            bool found    = data[4] != 0;
            int valLen    = Protocol.ReadU16(data, 5);
            string value  = (found && length >= 7 + valLen)
                ? Encoding.UTF8.GetString(data, 7, valLen)
                : null;
            NetworkPersistence.HandlePersistData(reqId, found, value);
        }

        // =====================================================================
        // Rigidbody Updates (0x08)
        // =====================================================================

        private void HandleRigidbodyUpdate(byte[] data, int length)
        {
            if (length < 15) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (_objectMap.TryGetValue(objId, out var rbObj))
            {
                var netRb = rbObj.GetComponent<NetworkRigidbody>();
                if (netRb != null)
                    netRb.HandleRigidbodyUpdate(data, 3);
            }
        }
    }
}
