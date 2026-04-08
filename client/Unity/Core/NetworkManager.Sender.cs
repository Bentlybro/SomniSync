using System.Buffers;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles all outgoing position, interaction, and snapshot messages.
    /// Uses ArrayPool + single-pass writes to minimise per-frame allocations.
    /// </summary>
    public partial class NetworkManager
    {
        // =====================================================================
        // Authority position updates (0x0B) — any client, for owned objects
        // Format: [0x0B][u16 senderClientId][u16 sequence][u16 count][20B entities...]
        // rigidbodies=true  → only physics objects (20Hz)
        // rigidbodies=false → only non-physics objects (60Hz)
        // =====================================================================

        private void SendAuthorityPositionUpdates(bool rigidbodies)
        {
            if (_objects.Count == 0) return;

            // --- Non-scaled: single pass, write into pooled buffer, send if count > 0 ---
            int maxVelSize = 7 + _objects.Count * Protocol.BYTES_PER_ENTITY_VELOCITY;
            byte[] velBuf = ArrayPool<byte>.Shared.Rent(maxVelSize);
            velBuf[0] = Protocol.MSG_POSITION_VELOCITY;
            Protocol.WriteU16(velBuf, 1, ClientId);
            // sequence written after we know count > 0
            int velOffset = 7;
            int velCount = 0;

            int maxScaledSize = 3 + _objects.Count * Protocol.BYTES_PER_ENTITY_SCALED;
            byte[] scaledBuf = ArrayPool<byte>.Shared.Rent(maxScaledSize);
            scaledBuf[0] = Protocol.MSG_POSITION_UPDATE_SCALED;
            int scaledOffset = 3;
            int scaledCount = 0;

            foreach (var obj in _objects)
            {
                if (!obj.HasStateAuthority) continue;
                if (!obj.IsAOIActive) continue;
                if (obj.IsClaimedByRemote) continue;
                if (obj.IsRigidbody != rigidbodies) continue;
                if (obj.IsSleeping) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;

                if (obj.syncScale)
                {
                    scaledOffset = Protocol.WriteEntityScaled(scaledBuf, scaledOffset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                    scaledCount++;
                }
                else
                {
                    velOffset = Protocol.WriteEntityVelocity(velBuf, velOffset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.GetCurrentVelocity());
                    velCount++;
                }
                obj.MarkSent();
            }

            try
            {
                if (velCount > 0)
                {
                    _sendSequence++;
                    Protocol.WriteU16(velBuf, 3, _sendSequence);
                    Protocol.WriteU16(velBuf, 5, (ushort)velCount);
                    SendBinarySegment(velBuf, velOffset);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(velBuf); }

            try
            {
                if (scaledCount > 0)
                {
                    WriteU16(scaledBuf, 1, (ushort)scaledCount);
                    SendBinarySegment(scaledBuf, scaledOffset);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(scaledBuf); }
        }

        // =====================================================================
        // Interaction updates (batch, called each frame)
        // =====================================================================

        private void SendInteractionUpdates()
        {
            int maxSize = 3 + _objects.Count * Protocol.BYTES_PER_ENTITY_SCALED; // worst case
            byte[] buf     = ArrayPool<byte>.Shared.Rent(maxSize);
            byte[] scalBuf = ArrayPool<byte>.Shared.Rent(maxSize);

            buf[0]     = 0x03;
            scalBuf[0] = 0x09;
            int offset = 3, scalOffset = 3;
            int count = 0, scalCount = 0;

            foreach (var obj in _objects)
            {
                if (!obj.IsClaimedLocally) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;

                if (obj.syncScale)
                {
                    scalOffset = Protocol.WriteEntityScaled(scalBuf, scalOffset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                    scalCount++;
                }
                else
                {
                    offset = WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles);
                    count++;
                }
                obj.MarkSent();
            }

            try
            {
                if (count > 0)
                {
                    WriteU16(buf, 1, (ushort)count);
                    SendBinarySegment(buf, offset);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }

            try
            {
                if (scalCount > 0)
                {
                    WriteU16(scalBuf, 1, (ushort)scalCount);
                    SendBinarySegment(scalBuf, scalOffset);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(scalBuf); }
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

        /// <summary>Send a claim message (called by NetworkObject.Grab()).</summary>
        public void SendClaim(ushort objectId)
        {
            byte[] buf = new byte[6];
            buf[0] = 0xFF; buf[1] = 0x05;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinary(buf);
        }

        /// <summary>Send a release message (called by NetworkObject.Release()).</summary>
        public void SendRelease(ushort objectId)
        {
            byte[] buf = new byte[6];
            buf[0] = 0xFF; buf[1] = 0x06;
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

            int maxNormal = 3 + _objects.Count * Protocol.BYTES_PER_ENTITY;
            int maxScaled = 3 + _objects.Count * Protocol.BYTES_PER_ENTITY_SCALED;
            byte[] normBuf = ArrayPool<byte>.Shared.Rent(maxNormal);
            byte[] scalBuf = ArrayPool<byte>.Shared.Rent(maxScaled);

            normBuf[0] = 0x02;
            scalBuf[0] = Protocol.MSG_POSITION_UPDATE_SCALED;
            int normOffset = 3, scalOffset = 3;
            int normCount = 0, scalCount = 0;

            foreach (var obj in _objects)
            {
                if (obj.syncScale)
                {
                    scalOffset = Protocol.WriteEntityScaled(scalBuf, scalOffset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                    scalCount++;
                }
                else
                {
                    normOffset = WriteEntityCompact(normBuf, normOffset, obj.LocalId,
                        obj.transform.position, obj.transform.eulerAngles);
                    normCount++;
                }
            }

            try
            {
                if (normCount > 0)
                {
                    WriteU16(normBuf, 1, (ushort)normCount);
                    SendBinarySegment(normBuf, normOffset);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(normBuf); }

            try
            {
                if (scalCount > 0)
                {
                    WriteU16(scalBuf, 1, (ushort)scalCount);
                    SendBinarySegment(scalBuf, scalOffset);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(scalBuf); }

            Debug.Log($"[Net] Sending snapshot: {normCount} normal + {scalCount} scaled objects");
        }

        // =====================================================================
        // Send a correctly-sized copy of a pooled buffer segment
        // =====================================================================

        private void SendBinarySegment(byte[] buf, int length)
        {
            byte[] msg = new byte[length];
            System.Buffer.BlockCopy(buf, 0, msg, 0, length);
            SendBinary(msg);
        }
    }
}
