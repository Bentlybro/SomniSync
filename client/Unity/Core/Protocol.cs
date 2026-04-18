using System;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Shared binary encoding/decoding helpers for the network protocol.
    /// Used by NetworkManager and any component that serializes data.
    ///
    /// Architecture: server-authoritative. Clients never send authoritative
    /// position updates — those come from the server (0x0B) based on its own
    /// Rapier3D simulation. Clients only send: SCENE_INIT (bootstrap), player
    /// position (for AOI), held-object updates (0x03/0x09), variables, events,
    /// animator state, and control messages.
    /// </summary>
    public static class Protocol
    {
        // =====================================================================
        // Message types
        // =====================================================================

        public const byte MSG_INTERACTION_UPDATE      = 0x03; // client → server: held-object position
        public const byte MSG_VARIABLE_UPDATE         = 0x04; // NetworkVariables (any → others)
        public const byte MSG_EVENT                   = 0x06; // fire-and-forget RPCs (any → others)
        public const byte MSG_ANIMATOR                = 0x07; // animator state (any → others)
        public const byte MSG_SCALED_INTERACTION      = 0x09; // scaled held-object position
        public const byte MSG_POSITION_VELOCITY       = 0x0B; // server → clients: authoritative position + velocity
        /// <summary>Client → server: local player position (for server-side AOI). [0x0C][f32 x][f32 y][f32 z]</summary>
        public const byte MSG_PLAYER_POS              = 0x0C;
        /// <summary>Client → server: apply a push impulse to an object. [0x0D][u16 obj_id][f32 vx][f32 vy][f32 vz]</summary>
        public const byte MSG_PUSH_IMPULSE            = 0x0D;
        /// <summary>Client → server: time sync ping. [0x0E][u64 client_local_ms]. Server replies with CTRL_TIME_PONG.</summary>
        public const byte MSG_TIME_PING               = 0x0E;
        /// <summary>Client → server: physics scene description. [0x10][u16 count][N × 60B objects]</summary>
        public const byte MSG_SCENE_INIT              = 0x10;
        public const byte MSG_CONTROL                 = 0xFF;

        // =====================================================================
        // Control subtypes  (second byte inside 0xFF messages)
        // =====================================================================

        /// <summary>Server → client on connect: [u16 client_id][u8 needs_scene_init]</summary>
        public const byte CTRL_WELCOME           = 0x01;
        public const byte CTRL_CLAIM             = 0x05;
        public const byte CTRL_RELEASE           = 0x06;
        /// <summary>Server → requester: the claim was rejected (someone else owns the object). [0xFF][0x07][u16 obj_id]</summary>
        public const byte CTRL_CLAIM_REJECTED    = 0x07;

        /// <summary>Client → server: spawn a networked prefab. [0xFF][0x0B][u8 prefab_idx][60B scene_object] (id=0)</summary>
        public const byte CTRL_SPAWN_REQUEST     = 0x0B;
        /// <summary>Server → all: a runtime object has been spawned. [0xFF][0x0C][u16 id][u8 prefab_idx][60B scene_object]</summary>
        public const byte CTRL_SPAWN             = 0x0C;
        /// <summary>Either direction: destroy a runtime object. [0xFF][0x0D][u16 id]</summary>
        public const byte CTRL_DESPAWN           = 0x0D;
        /// <summary>Server → client: time sync reply. [0xFF][0x0E][u64 client_local_ms][u64 server_ms]</summary>
        public const byte CTRL_TIME_PONG         = 0x0E;
        /// <summary>Server → all: physics sim is now live. Stop any local simulation.</summary>
        public const byte CTRL_PHYSICS_READY     = 0x08;

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

        /// <summary>22 bytes: [u16 id][f32 x/y/z][i16 qx/qy/qz/qw]</summary>
        public const int BYTES_PER_ENTITY = 22;

        /// <summary>28 bytes: compact + [half sx/sy/sz]</summary>
        public const int BYTES_PER_ENTITY_SCALED = 28;

        /// <summary>28 bytes: compact + [half vx/vy/vz] for dead reckoning.</summary>
        public const int BYTES_PER_ENTITY_VELOCITY = 28;

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
        // Quantized quaternion — 4 × i16 in [-1, 1]  = 8 bytes
        // Precision ≈ 3e-5 per component, far tighter than half-float rotation
        // and free of Euler gimbal-lock artifacts.
        // =====================================================================

        private const float QUAT_SCALE = 32767f;

        public static void WriteQuat(byte[] buf, int offset, Quaternion q)
        {
            WriteI16(buf, offset,     (short)Mathf.Clamp(Mathf.Round(q.x * QUAT_SCALE), -32768, 32767));
            WriteI16(buf, offset + 2, (short)Mathf.Clamp(Mathf.Round(q.y * QUAT_SCALE), -32768, 32767));
            WriteI16(buf, offset + 4, (short)Mathf.Clamp(Mathf.Round(q.z * QUAT_SCALE), -32768, 32767));
            WriteI16(buf, offset + 6, (short)Mathf.Clamp(Mathf.Round(q.w * QUAT_SCALE), -32768, 32767));
        }

        public static Quaternion ReadQuat(byte[] buf, int offset)
        {
            float x = ReadI16(buf, offset)     / QUAT_SCALE;
            float y = ReadI16(buf, offset + 2) / QUAT_SCALE;
            float z = ReadI16(buf, offset + 4) / QUAT_SCALE;
            float w = ReadI16(buf, offset + 6) / QUAT_SCALE;
            // Renormalize — quantization + drift can take |q| slightly off 1.
            float mag = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (mag < 1e-6f) return Quaternion.identity;
            float inv = 1f / mag;
            return new Quaternion(x * inv, y * inv, z * inv, w * inv);
        }

        public static void WriteI16(byte[] buf, int offset, short value)
        {
            buf[offset]     = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        public static short ReadI16(byte[] buf, int offset)
            => (short)(buf[offset] | (buf[offset + 1] << 8));

        // =====================================================================
        // Compact entity   [u16 id][f32 x/y/z][i16 qx/qy/qz/qw] = 22 bytes
        // =====================================================================

        public static int WriteEntityCompact(byte[] buf, int offset, ushort id, Vector3 pos, Quaternion rot)
        {
            WriteU16(buf, offset, id);       offset += 2;
            WriteFloat(buf, offset, pos.x);  offset += 4;
            WriteFloat(buf, offset, pos.y);  offset += 4;
            WriteFloat(buf, offset, pos.z);  offset += 4;
            WriteQuat(buf, offset, rot);     offset += 8;
            return offset;
        }

        public static int ReadEntityCompact(byte[] buf, int offset, out ushort id, out Vector3 pos, out Quaternion rot)
        {
            id = ReadU16(buf, offset); offset += 2;
            float px = ReadFloat(buf, offset); offset += 4;
            float py = ReadFloat(buf, offset); offset += 4;
            float pz = ReadFloat(buf, offset); offset += 4;
            rot = ReadQuat(buf, offset);       offset += 8;
            pos = new Vector3(px, py, pz);
            return offset;
        }

        public static float ReadFloat(byte[] buf, int offset)
            => BitConverter.ToSingle(buf, offset);

        // =====================================================================
        // Scaled entity   compact + [half sx/sy/sz] = 28 bytes
        // =====================================================================

        public static int WriteEntityScaled(byte[] buf, int offset, ushort id, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            offset = WriteEntityCompact(buf, offset, id, pos, rot);
            WriteHalf(buf, offset, scale.x); offset += 2;
            WriteHalf(buf, offset, scale.y); offset += 2;
            WriteHalf(buf, offset, scale.z); offset += 2;
            return offset;
        }

        public static int ReadEntityScaled(byte[] buf, int offset, out ushort id, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            offset = ReadEntityCompact(buf, offset, out id, out pos, out rot);
            float sx = ReadHalf(buf, offset); offset += 2;
            float sy = ReadHalf(buf, offset); offset += 2;
            float sz = ReadHalf(buf, offset); offset += 2;
            scale = new Vector3(sx, sy, sz);
            return offset;
        }

        // =====================================================================
        // Velocity entity   compact + [half vx/vy/vz] = 28 bytes
        // Used by MSG_POSITION_VELOCITY (0x0B) for dead reckoning.
        // =====================================================================

        public static int ReadEntityVelocity(byte[] buf, int offset, out ushort id, out Vector3 pos, out Quaternion rot, out Vector3 vel)
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
