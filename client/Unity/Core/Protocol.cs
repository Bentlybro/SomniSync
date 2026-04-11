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
        // Message types
        // =====================================================================

        public const byte MSG_POSITION_UPDATE         = 0x01; // legacy master-only batch (kept for compat)
        public const byte MSG_SNAPSHOT                = 0x02; // full world state (master → all)
        public const byte MSG_INTERACTION_UPDATE      = 0x03; // claimed-object updates (any client)
        public const byte MSG_VARIABLE_UPDATE         = 0x04; // NetworkVariables
        public const byte MSG_POSITION_UPDATE_SCALED  = 0x05; // scaled objects batch
        public const byte MSG_EVENT                   = 0x06; // fire-and-forget RPCs
        public const byte MSG_ANIMATOR                = 0x07; // animator state
        public const byte MSG_RIGIDBODY               = 0x08; // velocity sync
        public const byte MSG_SCALED_INTERACTION      = 0x09; // claimed scaled-object updates
        public const byte MSG_SNAPSHOT_SCALED         = 0x0A; // full scaled-object snapshot
        public const byte MSG_POSITION_VELOCITY       = 0x0B; // authority position + velocity (dead reckoning)
        /// <summary>Client → server: physics scene description. [0x10][u16 count][N × 60B objects]</summary>
        public const byte MSG_SCENE_INIT              = 0x10;
        public const byte MSG_CONTROL                 = 0xFF;

        // =====================================================================
        // Control subtypes  (second byte inside 0xFF messages)
        // =====================================================================

        public const byte CTRL_YOU_ARE_MASTER    = 0x01;
        public const byte CTRL_YOU_ARE_SPECTATOR = 0x02;
        public const byte CTRL_SEND_SNAPSHOT     = 0x03;
        public const byte CTRL_MASTER_CHANGED    = 0x04;
        public const byte CTRL_CLAIM             = 0x05;
        public const byte CTRL_RELEASE           = 0x06;
        public const byte CTRL_PLAYER_POSITION   = 0x07;
        /// <summary>Server → all: server physics mode is now active; clear all StateAuthority. [0xFF][0x08]</summary>
        public const byte CTRL_PHYSICS_READY     = 0x08;
        /// <summary>
        /// Master → all: assign StateAuthority for a set of objects to one client.
        /// Format: [0xFF][0x0A][u16 clientId][u16 count][u16... objectIds]
        /// </summary>
        public const byte CTRL_AUTHORITY_ASSIGN  = 0x0A;

        // --- Persistence control subtypes ---
        /// <summary>Client → server: register stable player UUID. [0xFF][0x20][u8 len][utf8 uuid...]</summary>
        public const byte CTRL_PLAYER_IDENT  = 0x20;
        /// <summary>Client → server: write key/value. [0xFF][0x21][u8 ns_len][ns][u8 key_len][key][u16 val_len][val]</summary>
        public const byte CTRL_PERSIST_SET   = 0x21;
        /// <summary>Client → server: read request. [0xFF][0x22][u16 req_id][u8 ns_len][ns][u8 key_len][key]</summary>
        public const byte CTRL_PERSIST_GET   = 0x22;
        /// <summary>Server → client: read response. [0xFF][0x23][u16 req_id][u8 found][u16 val_len][val]</summary>
        public const byte CTRL_PERSIST_DATA  = 0x23;

        // =====================================================================
        // Size constants
        // =====================================================================

        /// <summary>14 bytes: [u16 id][half x/y/z][u16 rx/ry/rz]</summary>
        public const int BYTES_PER_ENTITY = 14;

        /// <summary>20 bytes: compact + [half sx/sy/sz]</summary>
        public const int BYTES_PER_ENTITY_SCALED = 20;

        /// <summary>20 bytes: compact + [half vx/vy/vz] for dead reckoning.</summary>
        public const int BYTES_PER_ENTITY_VELOCITY = 20;

        // =====================================================================
        // Basic types
        // =====================================================================

        public static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset]     = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)(value >> 8);
        }

        public static ushort ReadU16(byte[] buf, int offset)
            => (ushort)(buf[offset] | (buf[offset + 1] << 8));

        public static void WriteFloat(byte[] buf, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            buf[offset]     = bytes[0];
            buf[offset + 1] = bytes[1];
            buf[offset + 2] = bytes[2];
            buf[offset + 3] = bytes[3];
        }

        // =====================================================================
        // Half-precision float (IEEE 754 float16)
        // Range ±65504, precision ~0.001 at small values
        // =====================================================================

        public static void WriteHalf(byte[] buf, int offset, float value)
        {
            int bits     = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            int sign     = (bits >> 16) & 0x8000;
            int exp      = ((bits >> 23) & 0xFF) - 127 + 15;
            int mantissa = bits & 0x007FFFFF;

            ushort half;
            if (exp <= 0)
                half = (ushort)sign;
            else if (exp >= 31)
                half = (ushort)(sign | 0x7C00);
            else
                half = (ushort)(sign | (exp << 10) | (mantissa >> 13));

            buf[offset]     = (byte)(half & 0xFF);
            buf[offset + 1] = (byte)(half >> 8);
        }

        public static float ReadHalf(byte[] buf, int offset)
        {
            ushort half     = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            int    sign     = (half >> 15) & 1;
            int    exp      = (half >> 10) & 0x1F;
            int    mantissa = half & 0x3FF;

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
        // Quantized rotation  0-360° → u16 0-65535, precision ≈ 0.005°
        // =====================================================================

        public static void WriteRotU16(byte[] buf, int offset, float degrees)
        {
            degrees = degrees % 360f;
            if (degrees < 0) degrees += 360f;
            ushort val = (ushort)(degrees * (65535f / 360f));
            buf[offset]     = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)(val >> 8);
        }

        public static float ReadRotU16(byte[] buf, int offset)
        {
            ushort val = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            return val * (360f / 65535f);
        }

        // =====================================================================
        // Compact entity   [u16 id][half x/y/z][u16 rx/ry/rz] = 14 bytes
        // =====================================================================

        public static int WriteEntityCompact(byte[] buf, int offset, ushort id, Vector3 pos, Vector3 rot)
        {
            WriteU16(buf, offset, id);       offset += 2;
            WriteHalf(buf, offset, pos.x);   offset += 2;
            WriteHalf(buf, offset, pos.y);   offset += 2;
            WriteHalf(buf, offset, pos.z);   offset += 2;
            WriteRotU16(buf, offset, rot.x); offset += 2;
            WriteRotU16(buf, offset, rot.y); offset += 2;
            WriteRotU16(buf, offset, rot.z); offset += 2;
            return offset;
        }

        public static int ReadEntityCompact(byte[] buf, int offset, out ushort id, out Vector3 pos, out Vector3 rot)
        {
            id = ReadU16(buf, offset); offset += 2;
            float px = ReadHalf(buf, offset);   offset += 2;
            float py = ReadHalf(buf, offset);   offset += 2;
            float pz = ReadHalf(buf, offset);   offset += 2;
            float rx = ReadRotU16(buf, offset); offset += 2;
            float ry = ReadRotU16(buf, offset); offset += 2;
            float rz = ReadRotU16(buf, offset); offset += 2;
            pos = new Vector3(px, py, pz);
            rot = new Vector3(rx, ry, rz);
            return offset;
        }

        // =====================================================================
        // Scaled entity   compact + [half sx/sy/sz] = 20 bytes
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

        // =====================================================================
        // Velocity entity   compact + [half vx/vy/vz] = 20 bytes
        // Used by MSG_POSITION_VELOCITY (0x0B) for dead reckoning.
        // =====================================================================

        public static int WriteEntityVelocity(byte[] buf, int offset, ushort id, Vector3 pos, Vector3 rot, Vector3 vel)
        {
            offset = WriteEntityCompact(buf, offset, id, pos, rot);
            WriteHalf(buf, offset, vel.x); offset += 2;
            WriteHalf(buf, offset, vel.y); offset += 2;
            WriteHalf(buf, offset, vel.z); offset += 2;
            return offset;
        }

        public static int ReadEntityVelocity(byte[] buf, int offset, out ushort id, out Vector3 pos, out Vector3 rot, out Vector3 vel)
        {
            offset = ReadEntityCompact(buf, offset, out id, out pos, out rot);
            float vx = ReadHalf(buf, offset); offset += 2;
            float vy = ReadHalf(buf, offset); offset += 2;
            float vz = ReadHalf(buf, offset); offset += 2;
            vel = new Vector3(vx, vy, vz);
            return offset;
        }

        // =====================================================================
        // Physics scene description  (MSG_SCENE_INIT 0x10)
        // Each object is exactly 60 bytes in the wire format.
        // =====================================================================

        /// <summary>Body type enum matching the server's Rapier3D body types.</summary>
        public enum PhysicsBodyType : byte { Dynamic = 0, Fixed = 1, KinematicPosition = 2 }

        /// <summary>Shape type enum matching Rapier3D collider shapes.</summary>
        public enum PhysicsShapeType : byte { Ball = 0, Cuboid = 1, Capsule = 2 }

        /// <summary>Wire size of one physics object in a SCENE_INIT message (bytes).</summary>
        public const int BYTES_PER_SCENE_OBJECT = 60;

        /// <summary>
        /// Write one object's physics description into <paramref name="buf"/> at <paramref name="offset"/>.
        /// Advances and returns the new offset.
        /// </summary>
        public static int WriteSceneObject(byte[] buf, int offset,
            ushort id, PhysicsBodyType bodyType, PhysicsShapeType shapeType,
            Vector3 position, Vector3 rotationEuler, Vector3 shapeParams,
            float mass, float restitution, float friction, float linearDamping, float angularDamping)
        {
            WriteU16(buf, offset, id);                          offset += 2;
            buf[offset] = (byte)bodyType;                       offset += 1;
            buf[offset] = (byte)shapeType;                      offset += 1;
            WriteFloat(buf, offset, position.x);                offset += 4;
            WriteFloat(buf, offset, position.y);                offset += 4;
            WriteFloat(buf, offset, position.z);                offset += 4;
            WriteFloat(buf, offset, rotationEuler.x);           offset += 4;
            WriteFloat(buf, offset, rotationEuler.y);           offset += 4;
            WriteFloat(buf, offset, rotationEuler.z);           offset += 4;
            WriteFloat(buf, offset, shapeParams.x);             offset += 4;
            WriteFloat(buf, offset, shapeParams.y);             offset += 4;
            WriteFloat(buf, offset, shapeParams.z);             offset += 4;
            WriteFloat(buf, offset, mass);                      offset += 4;
            WriteFloat(buf, offset, restitution);               offset += 4;
            WriteFloat(buf, offset, friction);                  offset += 4;
            WriteFloat(buf, offset, linearDamping);             offset += 4;
            WriteFloat(buf, offset, angularDamping);            offset += 4;
            return offset;
        }
    }
}
