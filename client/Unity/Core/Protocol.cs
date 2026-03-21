using System;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Shared binary encoding/decoding helpers for the network protocol.
    /// Used by NetworkManager and any component that serializes data.
    /// </summary>
    public static class Protocol
    {
        // =====================================================================
        // Protocol constants
        // =====================================================================

        public const byte MSG_POSITION_UPDATE = 0x01;
        public const byte MSG_SNAPSHOT = 0x02;
        public const byte MSG_INTERACTION_UPDATE = 0x03;
        public const byte MSG_VARIABLE_UPDATE = 0x04;
        public const byte MSG_POSITION_UPDATE_SCALED = 0x05;
        public const byte MSG_EVENT = 0x06;
        public const byte MSG_ANIMATOR = 0x07;
        public const byte MSG_CONTROL = 0xFF;

        public const byte CTRL_YOU_ARE_MASTER = 0x01;
        public const byte CTRL_YOU_ARE_SPECTATOR = 0x02;
        public const byte CTRL_SEND_SNAPSHOT = 0x03;
        public const byte CTRL_MASTER_CHANGED = 0x04;
        public const byte CTRL_CLAIM = 0x05;
        public const byte CTRL_RELEASE = 0x06;

        /// <summary>Bytes per entity in compact format (half-float pos + u16 rot).</summary>
        public const int BYTES_PER_ENTITY = 14;
        /// <summary>Bytes per entity with scale (pos + rot + half-float scale).</summary>
        public const int BYTES_PER_ENTITY_SCALED = 20;

        // =====================================================================
        // Basic types
        // =====================================================================

        public static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)(value >> 8);
        }

        public static ushort ReadU16(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        }

        public static void WriteFloat(byte[] buf, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            buf[offset] = bytes[0];
            buf[offset + 1] = bytes[1];
            buf[offset + 2] = bytes[2];
            buf[offset + 3] = bytes[3];
        }

        // =====================================================================
        // Half-precision float (IEEE 754 float16)
        // Range: ±65504, precision ~0.001 at small values
        // =====================================================================

        public static void WriteHalf(byte[] buf, int offset, float value)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            int sign = (bits >> 16) & 0x8000;
            int exp = ((bits >> 23) & 0xFF) - 127 + 15;
            int mantissa = bits & 0x007FFFFF;

            ushort half;
            if (exp <= 0)
                half = (ushort)sign;
            else if (exp >= 31)
                half = (ushort)(sign | 0x7C00);
            else
                half = (ushort)(sign | (exp << 10) | (mantissa >> 13));

            buf[offset] = (byte)(half & 0xFF);
            buf[offset + 1] = (byte)(half >> 8);
        }

        public static float ReadHalf(byte[] buf, int offset)
        {
            ushort half = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            int sign = (half >> 15) & 1;
            int exp = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;

            int fbits;
            if (exp == 0)
            {
                if (mantissa == 0)
                    fbits = sign << 31;
                else
                {
                    exp = 1;
                    while ((mantissa & 0x400) == 0) { mantissa <<= 1; exp--; }
                    mantissa &= 0x3FF;
                    fbits = (sign << 31) | ((exp + 127 - 15) << 23) | (mantissa << 13);
                }
            }
            else if (exp == 31)
                fbits = (sign << 31) | 0x7F800000 | (mantissa << 13);
            else
                fbits = (sign << 31) | ((exp + 127 - 15) << 23) | (mantissa << 13);

            return BitConverter.ToSingle(BitConverter.GetBytes(fbits), 0);
        }

        // =====================================================================
        // Quantized rotation (0-360° → uint16 0-65535)
        // Precision: 360/65535 ≈ 0.005°
        // =====================================================================

        public static void WriteRotU16(byte[] buf, int offset, float degrees)
        {
            degrees = degrees % 360f;
            if (degrees < 0) degrees += 360f;
            ushort val = (ushort)(degrees * (65535f / 360f));
            buf[offset] = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)(val >> 8);
        }

        public static float ReadRotU16(byte[] buf, int offset)
        {
            ushort val = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            return val * (360f / 65535f);
        }

        // =====================================================================
        // Compact entity encoding
        // [u16 id][half x][half y][half z][u16 rx][u16 ry][u16 rz] = 14 bytes
        // =====================================================================

        public static int WriteEntityCompact(byte[] buf, int offset, ushort id, Vector3 pos, Vector3 rot)
        {
            WriteU16(buf, offset, id); offset += 2;
            WriteHalf(buf, offset, pos.x); offset += 2;
            WriteHalf(buf, offset, pos.y); offset += 2;
            WriteHalf(buf, offset, pos.z); offset += 2;
            WriteRotU16(buf, offset, rot.x); offset += 2;
            WriteRotU16(buf, offset, rot.y); offset += 2;
            WriteRotU16(buf, offset, rot.z); offset += 2;
            return offset;
        }

        public static int ReadEntityCompact(byte[] buf, int offset, out ushort id, out Vector3 pos, out Vector3 rot)
        {
            id = ReadU16(buf, offset); offset += 2;
            float px = ReadHalf(buf, offset); offset += 2;
            float py = ReadHalf(buf, offset); offset += 2;
            float pz = ReadHalf(buf, offset); offset += 2;
            float rx = ReadRotU16(buf, offset); offset += 2;
            float ry = ReadRotU16(buf, offset); offset += 2;
            float rz = ReadRotU16(buf, offset); offset += 2;
            pos = new Vector3(px, py, pz);
            rot = new Vector3(rx, ry, rz);
            return offset;
        }

        // =====================================================================
        // Scaled entity encoding (pos + rot + scale = 20 bytes)
        // [u16 id][half x][half y][half z][u16 rx][u16 ry][u16 rz][half sx][half sy][half sz]
        // =====================================================================

        public static int WriteEntityScaled(byte[] buf, int offset, ushort id, Vector3 pos, Vector3 rot, Vector3 scale)
        {
            offset = WriteEntityCompact(buf, offset, id, pos, rot);
            WriteHalf(buf, offset, scale.x); offset += 2;
            WriteHalf(buf, offset, scale.y); offset += 2;
            WriteHalf(buf, offset, scale.z); offset += 2;
            return offset;
        }

        public static int ReadEntityScaled(byte[] buf, int offset, out ushort id, out Vector3 pos, out Vector3 rot, out Vector3 scale)
        {
            offset = ReadEntityCompact(buf, offset, out id, out pos, out rot);
            float sx = ReadHalf(buf, offset); offset += 2;
            float sy = ReadHalf(buf, offset); offset += 2;
            float sz = ReadHalf(buf, offset); offset += 2;
            scale = new Vector3(sx, sy, sz);
            return offset;
        }
    }
}
