using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles all outgoing position, interaction, and snapshot messages.
    /// </summary>
    public partial class NetworkManager
    {
        // =====================================================================
        // Authority position updates (0x0B) — any client, for owned objects
        // Format: [0x0B][u16 senderClientId][u16 sequence][u16 count][20B entities...]
        // =====================================================================

        private void SendAuthorityPositionUpdates()
        {
            if (_objects.Count == 0) return;

            int movedCount = 0;
            int movedScaledCount = 0;
            foreach (var obj in _objects)
            {
                if (!obj.HasStateAuthority) continue;
                if (!obj.IsAOIActive) continue;
                if (obj.IsClaimedByRemote) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                if (obj.syncScale) movedScaledCount++;
                else movedCount++;
            }

            // 0x0B — non-scaled with velocity for dead reckoning
            if (movedCount > 0)
            {
                _sendSequence++;
                int size = 7 + movedCount * Protocol.BYTES_PER_ENTITY_VELOCITY;
                byte[] buf = new byte[size];
                buf[0] = Protocol.MSG_POSITION_VELOCITY;
                Protocol.WriteU16(buf, 1, ClientId);
                Protocol.WriteU16(buf, 3, _sendSequence);
                Protocol.WriteU16(buf, 5, (ushort)movedCount);
                int offset = 7;
                foreach (var obj in _objects)
                {
                    if (!obj.HasStateAuthority || obj.syncScale) continue;
                    if (!obj.IsAOIActive || obj.IsClaimedByRemote) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = Protocol.WriteEntityVelocity(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.GetCurrentVelocity());
                    obj.MarkSent();
                }
                SendBinary(buf);
            }

            // 0x05 — scaled objects (scale-synced objects are usually slow, no velocity needed)
            if (movedScaledCount > 0)
            {
                int size = 3 + movedScaledCount * Protocol.BYTES_PER_ENTITY_SCALED;
                byte[] buf = new byte[size];
                buf[0] = Protocol.MSG_POSITION_UPDATE_SCALED;
                WriteU16(buf, 1, (ushort)movedScaledCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (!obj.HasStateAuthority || !obj.syncScale) continue;
                    if (!obj.IsAOIActive || obj.IsClaimedByRemote) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = Protocol.WriteEntityScaled(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                    obj.MarkSent();
                }
                SendBinary(buf);
            }
        }

        // =====================================================================
        // Interaction updates (batch, called each frame)
        // =====================================================================

        private void SendInteractionUpdates()
        {
            int movedCount = 0;
            int movedScaledCount = 0;
            foreach (var obj in _objects)
            {
                if (!obj.IsClaimedLocally) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                if (obj.syncScale) movedScaledCount++;
                else movedCount++;
            }

            // Non-scaled interaction updates (0x03)
            if (movedCount > 0)
            {
                int size = 3 + movedCount * Protocol.BYTES_PER_ENTITY;
                byte[] buf = new byte[size];
                buf[0] = 0x03;
                WriteU16(buf, 1, (ushort)movedCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (!obj.IsClaimedLocally || obj.syncScale) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles);
                    obj.MarkSent();
                }
                SendBinary(buf);
            }

            // Scaled interaction updates (0x09)
            if (movedScaledCount > 0)
            {
                int size = 3 + movedScaledCount * Protocol.BYTES_PER_ENTITY_SCALED;
                byte[] buf = new byte[size];
                buf[0] = 0x09;
                WriteU16(buf, 1, (ushort)movedScaledCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (!obj.IsClaimedLocally || !obj.syncScale) continue;
                    if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;
                    offset = Protocol.WriteEntityScaled(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                    obj.MarkSent();
                }
                SendBinary(buf);
            }
        }

        /// <summary>
        /// Send a single object's position as an interaction update (used on release).
        /// </summary>
        public void SendInteractionUpdate(NetworkObject obj)
        {
            if (obj.syncScale)
            {
                byte[] buf = new byte[3 + Protocol.BYTES_PER_ENTITY_SCALED];
                buf[0] = 0x09;
                WriteU16(buf, 1, 1);
                Protocol.WriteEntityScaled(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                obj.MarkSent();
                SendBinary(buf);
            }
            else
            {
                byte[] buf = new byte[3 + Protocol.BYTES_PER_ENTITY];
                buf[0] = 0x03;
                WriteU16(buf, 1, 1);
                WriteEntityCompact(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles);
                obj.MarkSent();
                SendBinary(buf);
            }
        }

        /// <summary>
        /// Send a claim message for an object (called by NetworkObject.Grab()).
        /// </summary>
        public void SendClaim(ushort objectId)
        {
            // [0xFF][0x05][u16 object_id][u16 client_id]
            byte[] buf = new byte[6];
            buf[0] = 0xFF;
            buf[1] = 0x05;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinary(buf);
        }

        /// <summary>
        /// Send a release message for an object (called by NetworkObject.Release()).
        /// </summary>
        public void SendRelease(ushort objectId)
        {
            // [0xFF][0x06][u16 object_id][u16 client_id]
            byte[] buf = new byte[6];
            buf[0] = 0xFF;
            buf[1] = 0x06;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinary(buf);
        }

        // =====================================================================
        // Snapshot (0x02) — full state for new clients
        // =====================================================================

        private void SendSnapshot()
        {
            if (_objects.Count == 0) return;

            int normalCount = 0;
            int scaledCount = 0;
            foreach (var obj in _objects)
            {
                if (obj.syncScale) scaledCount++;
                else normalCount++;
            }

            // Non-scaled snapshot (0x02)
            if (normalCount > 0)
            {
                int size = 3 + normalCount * Protocol.BYTES_PER_ENTITY;
                byte[] buf = new byte[size];
                buf[0] = 0x02;
                WriteU16(buf, 1, (ushort)normalCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (obj.syncScale) continue;
                    offset = WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles);
                }
                SendBinary(buf);
            }

            // Scaled snapshot (0x05 — same handler as scaled position updates)
            if (scaledCount > 0)
            {
                int size = 3 + scaledCount * Protocol.BYTES_PER_ENTITY_SCALED;
                byte[] buf = new byte[size];
                buf[0] = Protocol.MSG_POSITION_UPDATE_SCALED;
                WriteU16(buf, 1, (ushort)scaledCount);
                int offset = 3;
                foreach (var obj in _objects)
                {
                    if (!obj.syncScale) continue;
                    offset = Protocol.WriteEntityScaled(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                }
                SendBinary(buf);
            }

            Debug.Log($"[Net] Sending snapshot: {normalCount} normal + {scaledCount} scaled objects");
        }
    }
}
