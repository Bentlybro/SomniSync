using System;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles all incoming binary messages from the server.
    ///
    /// Architecture: the server is authoritative. All position updates arrive
    /// as 0x0B (MSG_POSITION_VELOCITY) — we never receive client-authoritative
    /// snapshots or legacy position updates any more.
    /// </summary>
    public partial class NetworkManager
    {
        private void ProcessBinary(byte[] data, int length)
        {
            byte msgType = data[0];

            switch (msgType)
            {
                case 0x03: HandleInteractionUpdates(data, length);       break;
                case 0x04: HandleVariableUpdate(data, length);           break;
                case 0x06: HandleEvent(data, length);                    break;
                case 0x07: HandleAnimatorState(data, length);            break;
                case 0x09: HandleScaledInteractionUpdates(data, length); break;
                case 0x0B: HandlePositionVelocityUpdates(data, length);  break;
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
                case Protocol.CTRL_WELCOME: // [u16 client_id][u8 needs_scene_init][u64 unix_millis]
                    if (length < 5) break;
                    ClientId = Protocol.ReadU16(data, 2);
                    _needsSceneInit = data[4] != 0;

                    if (length >= 13)
                    {
                        // Read u64 server unix millis → compute offset so ServerTime is
                        // (approximately) agreed on by every client in the room.
                        ulong serverMs =
                              (ulong)data[5]
                            | ((ulong)data[6] << 8)
                            | ((ulong)data[7] << 16)
                            | ((ulong)data[8] << 24)
                            | ((ulong)data[9] << 32)
                            | ((ulong)data[10] << 40)
                            | ((ulong)data[11] << 48)
                            | ((ulong)data[12] << 56);
                        double serverSec = serverMs / 1000.0;
                        _serverTimeOffset = Time.timeAsDouble - serverSec;
                    }

                    Debug.Log($"[Net] Welcome — client_{ClientId}, needs_scene_init={_needsSceneInit}");

                    if (_needsSceneInit && _sceneReady && !_sceneInitSent)
                    {
                        SendSceneInit();
                        _sceneInitSent = true;
                    }

                    NetworkPersistence.OnNetworkReady(this);
                    if (!IsNetworkReady) { IsNetworkReady = true; OnNetworkReady?.Invoke(); }
                    break;

                case Protocol.CTRL_CLAIM: // Remote client claimed an object
                    if (length >= 6)
                    {
                        ushort claimObjId    = Protocol.ReadU16(data, 2);
                        ushort claimClientId = Protocol.ReadU16(data, 4);
                        if (GetObject(claimObjId) is { } claimObj)
                            claimObj.OnRemoteClaim(claimClientId);
                    }
                    break;

                case Protocol.CTRL_RELEASE: // Remote client released an object
                    if (length >= 6)
                    {
                        ushort releaseObjId = Protocol.ReadU16(data, 2);
                        if (GetObject(releaseObjId) is { } releaseObj)
                            releaseObj.OnRemoteRelease();
                    }
                    break;

                case Protocol.CTRL_CLAIM_REJECTED: // Server refused our claim
                    if (length >= 4)
                    {
                        ushort rejObjId = Protocol.ReadU16(data, 2);
                        if (GetObject(rejObjId) is { } rejObj)
                            rejObj.OnClaimRejected();
                    }
                    break;

                case Protocol.CTRL_PHYSICS_READY:
                    IsPhysicsReady = true;
                    Debug.Log("[Net] Server physics active");
                    break;

                case 0xFE: // Server error notification
                    Debug.LogError("[Net] Server reported an error");
                    break;
            }
        }

        // =====================================================================
        // Position + velocity updates (0x0B) — server-authoritative, dead reckoning
        // =====================================================================

        private void HandlePositionVelocityUpdates(byte[] data, int length)
        {
            if (!_sceneReady || length < 7) return;

            ushort senderClientId = Protocol.ReadU16(data, 1);
            ushort sequence       = Protocol.ReadU16(data, 3);

            if (_receivedSequences.TryGetValue(senderClientId, out ushort lastSeq))
            {
                ushort diff = unchecked((ushort)(sequence - lastSeq));
                if (diff == 0 || diff > 32768) return;
            }
            _receivedSequences[senderClientId] = sequence;

            ushort count = Protocol.ReadU16(data, 5);
            int offset = 7;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_VELOCITY <= length; i++)
            {
                ushort localId;
                Vector3 pos, vel;
                Quaternion rot;
                offset = Protocol.ReadEntityVelocity(data, offset, out localId, out pos, out rot, out vel);

                if (GetObject(localId) is { } obj)
                {
                    if (obj.IsClaimedLocally) continue;
                    obj.ApplyUpdate(pos, rot, vel);
                }
            }
        }

        // =====================================================================
        // Interaction updates (0x03) — relayed held-object positions from another client
        // =====================================================================

        private void HandleInteractionUpdates(byte[] data, int length)
        {
            if (!_sceneReady) return;

            ushort count = Protocol.ReadU16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY <= length; i++)
            {
                ushort localId;
                Vector3 pos;
                Quaternion rot;
                offset = Protocol.ReadEntityCompact(data, offset, out localId, out pos, out rot);

                if (GetObject(localId) is { } obj)
                {
                    if (!obj.IsClaimedLocally)
                        obj.ApplyUpdate(pos, rot);
                }
            }
        }

        // =====================================================================
        // Scaled interaction updates (0x09)
        // =====================================================================

        private void HandleScaledInteractionUpdates(byte[] data, int length)
        {
            if (!_sceneReady) return;

            ushort count = Protocol.ReadU16(data, 1);
            int offset = 3;

            for (int i = 0; i < count && offset + Protocol.BYTES_PER_ENTITY_SCALED <= length; i++)
            {
                ushort localId;
                Vector3 pos, scale;
                Quaternion rot;
                offset = Protocol.ReadEntityScaled(data, offset, out localId, out pos, out rot, out scale);

                if (GetObject(localId) is { } obj)
                {
                    if (!obj.IsClaimedLocally)
                        obj.ApplyUpdate(pos, rot, scale);
                }
            }
        }

        // =====================================================================
        // Variable Updates (0x04)
        // =====================================================================

        private void HandleVariableUpdate(byte[] data, int length)
        {
            if (length < 4) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));
            int varCount = data[3];

            if (GetObject(objId) is { } netVarObj && netVarObj.CachedVariables != null)
                netVarObj.CachedVariables.ApplyRemoteUpdate(data, 4, varCount);
        }

        // =====================================================================
        // Events (0x06)
        // =====================================================================

        private void HandleEvent(byte[] data, int length)
        {
            if (length < 4) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (GetObject(objId) is { } eventObj && eventObj.CachedEvents != null)
                eventObj.CachedEvents.ApplyRemoteEvent(data, 3, length);
        }

        // =====================================================================
        // Animator State (0x07)
        // =====================================================================

        private void HandleAnimatorState(byte[] data, int length)
        {
            if (length < 10) return;
            ushort objId = (ushort)(data[1] | (data[2] << 8));

            if (GetObject(objId) is { } animObj && animObj.CachedAnimator != null)
                animObj.CachedAnimator.ApplyRemoteState(data, 3);
        }

        // =====================================================================
        // Persistence response (0xFF / 0x23)
        // =====================================================================

        internal void HandlePersistData(byte[] data, int length)
        {
            if (length < 7) return;
            ushort reqId  = Protocol.ReadU16(data, 2);
            bool found    = data[4] != 0;
            int valLen    = Protocol.ReadU16(data, 5);
            string value  = (found && length >= 7 + valLen)
                ? Encoding.UTF8.GetString(data, 7, valLen)
                : null;
            NetworkPersistence.HandlePersistData(reqId, found, value);
        }
    }
}
