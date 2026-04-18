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

                case Protocol.CTRL_RELEASE:
                    HandleRelease(data, length);
                    break;

                case Protocol.CTRL_CLAIM_REJECTED: // Server refused our claim
                    if (length >= 4)
                    {
                        ushort rejObjId = Protocol.ReadU16(data, 2);
                        if (GetObject(rejObjId) is { } rejObj)
                            rejObj.OnClaimRejected();
                    }
                    break;

                case Protocol.CTRL_SPAWN:
                    HandleSpawn(data, length);
                    break;

                case Protocol.CTRL_DESPAWN:
                    if (length >= 4)
                        HandleDespawn(Protocol.ReadU16(data, 2));
                    break;

                case Protocol.CTRL_TIME_PONG:
                    HandleTimePong(data, length);
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
        // Runtime spawn / despawn (CTRL_SPAWN 0x0C, CTRL_DESPAWN 0x0D)
        // =====================================================================

        private void HandleSpawn(byte[] data, int length)
        {
            // [0xFF][0x0C][u16 id][u8 prefab_idx][60B scene_object]
            const int headerEnd = 5; // bytes before scene_object
            if (length < headerEnd + Protocol.BYTES_PER_SCENE_OBJECT) return;

            ushort localId  = Protocol.ReadU16(data, 2);
            byte prefabIdx  = data[4];

            // scene_object layout:
            //   [u16 id][u8 body_type][u8 shape_type][f32 px,py,pz][f32 rx,ry,rz]
            //   [f32 sx,sy,sz][f32 mass][f32 restitution][f32 friction]
            //   [f32 lin_damp][f32 ang_damp]
            int off = headerEnd + 4; // skip id (2) + body_type (1) + shape_type (1)
            float px = Protocol.ReadFloat(data, off); off += 4;
            float py = Protocol.ReadFloat(data, off); off += 4;
            float pz = Protocol.ReadFloat(data, off); off += 4;
            float rx = Protocol.ReadFloat(data, off); off += 4;
            float ry = Protocol.ReadFloat(data, off); off += 4;
            float rz = Protocol.ReadFloat(data, off); off += 4;

            if (networkedPrefabs == null || prefabIdx >= networkedPrefabs.Count)
            {
                Debug.LogWarning($"[Net] Spawn received for unknown prefab index {prefabIdx} (id={localId})");
                return;
            }
            var prefab = networkedPrefabs[prefabIdx];
            if (prefab == null) return;

            var go = Instantiate(prefab, new Vector3(px, py, pz), Quaternion.Euler(rx, ry, rz));
            var netObj = go.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Debug.LogError($"[Net] Spawned prefab {prefabIdx} has no NetworkObject — destroying");
                Destroy(go);
                return;
            }

            netObj.LocalId = localId;

            // Extend _objects to hold the new id (allows gaps if ids have skipped).
            while (_objects.Count <= localId) _objects.Add(null);
            _objects[localId] = netObj;

            OnObjectSpawned?.Invoke(netObj, prefabIdx);
        }

        /// <summary>
        /// Release broadcast carries an authoritative pose + timestamp from the
        /// server so every client snaps to identical post-chase state. Avoids
        /// per-client re-anchor drift that was making NetworkNPCs diverge after
        /// each chase.
        ///
        /// Wire layout (34 bytes):
        ///   [0xFF][0x06][u16 obj_id][u16 client_id]
        ///   [f32 px][f32 py][f32 pz]
        ///   [i16 qx][i16 qy][i16 qz][i16 qw]    (decode each /32767)
        ///   [u64 server_unix_ms]
        /// </summary>
        private void HandleRelease(byte[] data, int length)
        {
            if (length < 6) return;
            ushort releaseObjId = Protocol.ReadU16(data, 2);
            if (GetObject(releaseObjId) is not { } releaseObj) return;

            // Back-compat path if an older server ever sends the 6-byte format.
            if (length < 34)
            {
                releaseObj.OnRemoteRelease(releaseObj.transform.position,
                                           releaseObj.transform.rotation,
                                           0);
                return;
            }

            float px = Protocol.ReadFloat(data, 6);
            float py = Protocol.ReadFloat(data, 10);
            float pz = Protocol.ReadFloat(data, 14);
            short qxs = Protocol.ReadI16(data, 18);
            short qys = Protocol.ReadI16(data, 20);
            short qzs = Protocol.ReadI16(data, 22);
            short qws = Protocol.ReadI16(data, 24);
            ulong serverMs = ReadU64(data, 26);

            Vector3 pos = new Vector3(px, py, pz);
            Quaternion rot = new Quaternion(
                qxs / 32767f,
                qys / 32767f,
                qzs / 32767f,
                qws / 32767f);
            // Re-normalise — quantisation can nudge |q| slightly off 1.
            float mag = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
            if (mag > 1e-5f)
                rot = new Quaternion(rot.x / mag, rot.y / mag, rot.z / mag, rot.w / mag);

            releaseObj.OnRemoteRelease(pos, rot, serverMs);

            // Log to the debug overlay (if one is in the scene) so we can
            // cross-reference what every client received at a given instant.
            NetworkDebug.Instance?.LogReleaseReceived(releaseObjId, pos, rot, serverMs);
        }

        private void HandleDespawn(ushort localId)
        {
            if (localId >= _objects.Count) return;
            var obj = _objects[localId];
            if (obj == null) return;

            _objects[localId] = null;
            OnObjectDespawned?.Invoke(obj);
            Destroy(obj.gameObject);
        }

        // =====================================================================
        // Time sync (CTRL_TIME_PONG 0x0E)
        //
        // NTP-style correction of the ServerTime offset. The welcome message
        // gave us a one-way estimate (accurate to ±RTT/2); this round-trip
        // exchange lets us measure RTT and bake out the one-way-delay error.
        //
        // Payload: [u64 client_local_ms_echoed][u64 server_ms_when_processed]
        // =====================================================================

        private void HandleTimePong(byte[] data, int length)
        {
            if (length < 18) return;

            ulong clientSentMs = ReadU64(data, 2);
            ulong serverMs     = ReadU64(data, 10);

            double localNowMs = Time.timeAsDouble * 1000.0;
            double rttMs = localNowMs - clientSentMs;

            // Filter out garbage — negative (clock jump?) or absurdly high.
            if (rttMs < 0 || rttMs > 5000) return;

            double rttSec = rttMs / 1000.0;
            // Prefer smallest-RTT sample seen so far. Smaller RTT means less
            // jitter error in our symmetric one-way-delay assumption.
            if (rttSec >= BestRttSeconds && BestRttSeconds != double.MaxValue) return;

            BestRttSeconds = rttSec;

            // Estimate server's clock at the moment the pong arrived: the
            // server's stamp (when it processed the ping) plus the one-way
            // delay on the return leg (assumed symmetric = RTT/2).
            double estimatedServerMsNow = serverMs + (rttMs * 0.5);
            double offsetSeconds = (localNowMs - estimatedServerMsNow) / 1000.0;
            SetServerTimeOffsetSeconds(offsetSeconds);
        }

        private static ulong ReadU64(byte[] buf, int offset)
        {
            return ((ulong)buf[offset])
                 | ((ulong)buf[offset + 1] << 8)
                 | ((ulong)buf[offset + 2] << 16)
                 | ((ulong)buf[offset + 3] << 24)
                 | ((ulong)buf[offset + 4] << 32)
                 | ((ulong)buf[offset + 5] << 40)
                 | ((ulong)buf[offset + 6] << 48)
                 | ((ulong)buf[offset + 7] << 56);
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
