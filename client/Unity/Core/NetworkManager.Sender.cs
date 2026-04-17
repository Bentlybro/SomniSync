using System.Buffers;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles all outgoing messages from the client.
    ///
    /// Architecture: server-authoritative. Clients only send:
    ///   - SCENE_INIT (one-shot, by the first connecting client)
    ///   - Held-object position updates (while grabbing)
    ///   - Player position (for server-side AOI)
    ///   - Claim/release for grabs
    ///   - Events, variables, animator state (still relayed through server to others)
    ///
    /// Allocation strategy: buffers are rented from ArrayPool. Ownership transfers
    /// to the send loop via SendBinaryPooled which returns the buffer after send.
    /// </summary>
    public partial class NetworkManager
    {
        // =====================================================================
        // Interaction updates (batch, called each frame)
        // Only includes locally-claimed (grabbed) objects.
        // =====================================================================

        private void SendInteractionUpdates()
        {
            if (_objects.Count == 0) return;

            int maxSize = 3 + _objects.Count * Protocol.BYTES_PER_ENTITY_SCALED;

            byte[] buf     = ArrayPool<byte>.Shared.Rent(maxSize);
            byte[] scalBuf = ArrayPool<byte>.Shared.Rent(maxSize);

            buf[0]     = Protocol.MSG_INTERACTION_UPDATE;
            scalBuf[0] = Protocol.MSG_SCALED_INTERACTION;
            int offset     = 3;
            int scalOffset = 3;
            int count      = 0;
            int scalCount  = 0;

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                if (!obj.IsClaimedLocally) continue;
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;

                if (obj.syncScale)
                {
                    scalOffset = Protocol.WriteEntityScaled(scalBuf, scalOffset, obj.LocalId,
                        obj.transform.position, obj.transform.rotation, obj.transform.localScale);
                    scalCount++;
                }
                else
                {
                    offset = Protocol.WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.rotation);
                    count++;
                }
                obj.MarkSent();
            }

            if (count > 0)
            {
                WriteU16(buf, 1, (ushort)count);
                SendBinaryPooled(buf, offset);
                buf = null;
            }
            if (buf != null) ArrayPool<byte>.Shared.Return(buf);

            if (scalCount > 0)
            {
                WriteU16(scalBuf, 1, (ushort)scalCount);
                SendBinaryPooled(scalBuf, scalOffset);
                scalBuf = null;
            }
            if (scalBuf != null) ArrayPool<byte>.Shared.Return(scalBuf);
        }

        /// <summary>
        /// Send a single object's position as an interaction update (used on release).
        /// </summary>
        public void SendInteractionUpdate(NetworkObject obj)
        {
            if (obj.syncScale)
            {
                int size = 3 + Protocol.BYTES_PER_ENTITY_SCALED;
                byte[] buf = ArrayPool<byte>.Shared.Rent(size);
                buf[0] = Protocol.MSG_SCALED_INTERACTION;
                WriteU16(buf, 1, 1);
                Protocol.WriteEntityScaled(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.rotation, obj.transform.localScale);
                obj.MarkSent();
                SendBinaryPooled(buf, size);
            }
            else
            {
                int size = 3 + Protocol.BYTES_PER_ENTITY;
                byte[] buf = ArrayPool<byte>.Shared.Rent(size);
                buf[0] = Protocol.MSG_INTERACTION_UPDATE;
                WriteU16(buf, 1, 1);
                Protocol.WriteEntityCompact(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.rotation);
                obj.MarkSent();
                SendBinaryPooled(buf, size);
            }
        }

        /// <summary>Send a claim message (called by NetworkObject.Grab()).</summary>
        public void SendClaim(ushort objectId)
        {
            byte[] buf = new byte[6];
            buf[0] = 0xFF; buf[1] = Protocol.CTRL_CLAIM;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinary(buf);
        }

        /// <summary>Send a release message (called by NetworkObject.Release()).</summary>
        public void SendRelease(ushort objectId)
        {
            byte[] buf = new byte[6];
            buf[0] = 0xFF; buf[1] = Protocol.CTRL_RELEASE;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinary(buf);
        }

        // =====================================================================
        // Player position (0x0C) — local player head/body for server-side AOI
        // Format: [0x0C][f32 x][f32 y][f32 z] = 13 bytes
        // =====================================================================

        private void SendPlayerPosition()
        {
            if (_localPlayerTransform == null) return;

            Vector3 p = _localPlayerTransform.position;
            byte[] buf = new byte[13];
            buf[0] = Protocol.MSG_PLAYER_POS;
            Protocol.WriteFloat(buf, 1, p.x);
            Protocol.WriteFloat(buf, 5, p.y);
            Protocol.WriteFloat(buf, 9, p.z);
            SendBinary(buf);
        }

        // =====================================================================
        // SCENE_INIT (0x10) — send physics scene description to server
        // =====================================================================

        /// <summary>
        /// Scan the scene and send all physics-relevant colliders to the server so it can
        /// start Rapier3D simulation.
        ///
        /// Two passes:
        ///   1. Registered NetworkObjects — may be Dynamic, Kinematic, or Fixed.
        ///   2. Static world geometry (floors, walls, terrain) with a Collider but
        ///      no NetworkObject. Sent as Fixed bodies so Rapier knows about them.
        ///
        /// MeshColliders are skipped (no analytic Rapier shape).
        /// </summary>
        public void SendSceneInit()
        {
            var allColliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            int maxObjects = _objects.Count + allColliders.Length;
            int maxSize = 3 + maxObjects * Protocol.BYTES_PER_SCENE_OBJECT;

            byte[] buf = ArrayPool<byte>.Shared.Rent(maxSize);
            buf[0] = Protocol.MSG_SCENE_INIT;
            int offset = 3;
            int count  = 0;

            // === Pass 1: NetworkObjects (Dynamic / Kinematic / Fixed) ===
            foreach (var obj in _objects)
            {
                var rb  = obj.GetComponent<Rigidbody>();
                var col = obj.GetComponent<Collider>();
                if (col == null) continue;

                Protocol.PhysicsBodyType  bodyType;
                Protocol.PhysicsShapeType shapeType;
                Vector3 shapeParams;

                if (rb == null || col.gameObject.isStatic)
                    bodyType = Protocol.PhysicsBodyType.Fixed;
                else if (!obj.WantsDynamicPhysics)
                    bodyType = Protocol.PhysicsBodyType.KinematicPosition;
                else
                    bodyType = Protocol.PhysicsBodyType.Dynamic;

                if (!TryGetColliderShape(col, obj.transform, out shapeType, out shapeParams))
                    continue;

                float mass           = rb != null ? rb.mass           : 0f;
                float linearDamping  = rb != null ? rb.linearDamping  : 0f;
                float angularDamping = rb != null ? rb.angularDamping : 0.05f;
                float restitution    = 0.3f;
                float friction       = 0.4f;
                if (col.sharedMaterial != null)
                {
                    restitution = col.sharedMaterial.bounciness;
                    friction    = col.sharedMaterial.dynamicFriction;
                }

                Vector3 worldPos = GetColliderWorldCenter(col, obj.transform);

                offset = Protocol.WriteSceneObject(buf, offset,
                    obj.LocalId, bodyType, shapeType,
                    worldPos, obj.transform.eulerAngles, shapeParams,
                    mass, restitution, friction, linearDamping, angularDamping);
                count++;
            }

            // === Pass 2: Static world geometry without NetworkObject ===
            ushort staticId = (ushort)_objects.Count;
            foreach (var col in allColliders)
            {
                if (col == null) continue;
                if (col.GetComponent<NetworkObject>() != null) continue;
                if (col is MeshCollider) continue;

                var rb = col.GetComponent<Rigidbody>();
                bool isStatic = col.gameObject.isStatic || rb == null;
                if (!isStatic) continue;
                if (col.isTrigger) continue;
                if (staticId >= ushort.MaxValue - 1)
                {
                    Debug.LogWarning("[Net] SendSceneInit: static collider count exceeded ID space, some skipped");
                    break;
                }

                Protocol.PhysicsShapeType shapeType;
                Vector3 shapeParams;
                if (!TryGetColliderShape(col, col.transform, out shapeType, out shapeParams))
                    continue;

                float restitution = col.sharedMaterial != null ? col.sharedMaterial.bounciness      : 0.3f;
                float friction    = col.sharedMaterial != null ? col.sharedMaterial.dynamicFriction : 0.4f;
                Vector3 worldPos  = GetColliderWorldCenter(col, col.transform);

                offset = Protocol.WriteSceneObject(buf, offset,
                    staticId++, Protocol.PhysicsBodyType.Fixed, shapeType,
                    worldPos, col.transform.eulerAngles, shapeParams,
                    0f, restitution, friction, 0f, 0.05f);
                count++;
            }

            if (count == 0)
            {
                ArrayPool<byte>.Shared.Return(buf);
                Debug.LogWarning("[Net] SendSceneInit: no physics-relevant colliders found, nothing sent");
                return;
            }

            Protocol.WriteU16(buf, 1, (ushort)count);
            SendBinaryPooled(buf, offset);
            int netObjs = Mathf.Min(_objects.Count, count);
            Debug.Log($"[Net] Sent SCENE_INIT: {count} objects ({netObjs} networked + {count - netObjs} static) to server");
        }

        /// <summary>Returns the world-space centre of a collider (respects the local center offset).</summary>
        private static Vector3 GetColliderWorldCenter(Collider col, Transform t)
        {
            if (col is BoxCollider     bc) return t.TransformPoint(bc.center);
            if (col is SphereCollider  sc) return t.TransformPoint(sc.center);
            if (col is CapsuleCollider cc) return t.TransformPoint(cc.center);
            return t.position;
        }

        /// <summary>
        /// Converts a Unity Collider to a Rapier shape type + parameters.
        /// Returns false for MeshCollider and any unknown collider types.
        /// </summary>
        private static bool TryGetColliderShape(Collider col, Transform t,
            out Protocol.PhysicsShapeType shapeType, out Vector3 shapeParams)
        {
            if (col is SphereCollider sc)
            {
                shapeType = Protocol.PhysicsShapeType.Ball;
                float r = sc.radius * Mathf.Max(t.lossyScale.x, Mathf.Max(t.lossyScale.y, t.lossyScale.z));
                shapeParams = new Vector3(r, 0f, 0f);
                return true;
            }
            if (col is BoxCollider bc)
            {
                shapeType = Protocol.PhysicsShapeType.Cuboid;
                shapeParams = Vector3.Scale(bc.size, t.lossyScale) * 0.5f;
                return true;
            }
            if (col is CapsuleCollider cc)
            {
                shapeType = Protocol.PhysicsShapeType.Capsule;
                float worldRadius = cc.radius * Mathf.Max(t.lossyScale.x, t.lossyScale.z);
                float worldHalfHeight = Mathf.Max(0f, cc.height * 0.5f * t.lossyScale.y - worldRadius);
                shapeParams = new Vector3(worldRadius, worldHalfHeight, 0f);
                return true;
            }
            shapeType   = default;
            shapeParams = default;
            return false;
        }
    }
}
