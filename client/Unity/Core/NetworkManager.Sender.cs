using System.Buffers;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Handles all outgoing position, interaction, and snapshot messages.
    ///
    /// Allocation strategy: every method rents a buffer from ArrayPool, writes into it,
    /// then passes ownership to SendBinaryPooled(). The async send loop returns the buffer
    /// to the pool after the WebSocket send completes — zero heap allocations in hot paths.
    /// If nothing is queued (count == 0), the buffer is returned immediately.
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

            int maxVelSize    = 7 + _objects.Count * Protocol.BYTES_PER_ENTITY_VELOCITY;
            int maxScaledSize = 3 + _objects.Count * Protocol.BYTES_PER_ENTITY_SCALED;

            byte[] velBuf    = ArrayPool<byte>.Shared.Rent(maxVelSize);
            byte[] scaledBuf = ArrayPool<byte>.Shared.Rent(maxScaledSize);

            velBuf[0]    = Protocol.MSG_POSITION_VELOCITY;
            scaledBuf[0] = Protocol.MSG_POSITION_UPDATE_SCALED;
            Protocol.WriteU16(velBuf, 1, ClientId);

            int velOffset    = 7; // [type][sender u16][seq u16][count u16] written below
            int scaledOffset = 3;
            int velCount     = 0;
            int scaledCount  = 0;

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                if (!obj.HasStateAuthority)                              continue;
                if (obj.IsClaimedByRemote)                               continue;
                if (obj.IsRigidbody != rigidbodies)                      continue;
                if (obj.IsSleeping)                                      continue;
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

            if (velCount > 0)
            {
                _sendSequence++;
                Protocol.WriteU16(velBuf, 3, _sendSequence);
                Protocol.WriteU16(velBuf, 5, (ushort)velCount);
                SendBinaryPooled(velBuf, velOffset); // ownership transferred
                velBuf = null;
            }
            if (velBuf != null) ArrayPool<byte>.Shared.Return(velBuf);

            if (scaledCount > 0)
            {
                WriteU16(scaledBuf, 1, (ushort)scaledCount);
                SendBinaryPooled(scaledBuf, scaledOffset); // ownership transferred
                scaledBuf = null;
            }
            if (scaledBuf != null) ArrayPool<byte>.Shared.Return(scaledBuf);
        }

        // =====================================================================
        // Interaction updates (batch, called each frame)
        // =====================================================================

        private void SendInteractionUpdates()
        {
            int maxSize = 3 + _objects.Count * Protocol.BYTES_PER_ENTITY_SCALED;

            byte[] buf    = ArrayPool<byte>.Shared.Rent(maxSize);
            byte[] scalBuf = ArrayPool<byte>.Shared.Rent(maxSize);

            buf[0]     = 0x03;
            scalBuf[0] = 0x09;
            int offset    = 3;
            int scalOffset = 3;
            int count     = 0;
            int scalCount  = 0;

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
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
                buf[0] = 0x09;
                WriteU16(buf, 1, 1);
                Protocol.WriteEntityScaled(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles, obj.transform.localScale);
                obj.MarkSent();
                SendBinaryPooled(buf, size);
            }
            else
            {
                int size = 3 + Protocol.BYTES_PER_ENTITY;
                byte[] buf = ArrayPool<byte>.Shared.Rent(size);
                buf[0] = 0x03;
                WriteU16(buf, 1, 1);
                WriteEntityCompact(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.eulerAngles);
                obj.MarkSent();
                SendBinaryPooled(buf, size);
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

            normBuf[0] = Protocol.MSG_SNAPSHOT;
            scalBuf[0] = Protocol.MSG_SNAPSHOT_SCALED;
            int normOffset = 3;
            int scalOffset = 3;
            int normCount  = 0;
            int scalCount  = 0;

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                // ALL objects — see comment in original: snapshots are authoritative world state,
                // skipping sleeping objects causes spectators to miss final resting positions.
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

            if (normCount > 0)
            {
                WriteU16(normBuf, 1, (ushort)normCount);
                SendBinaryPooled(normBuf, normOffset);
                normBuf = null;
            }
            if (normBuf != null) ArrayPool<byte>.Shared.Return(normBuf);

            if (scalCount > 0)
            {
                WriteU16(scalBuf, 1, (ushort)scalCount);
                SendBinaryPooled(scalBuf, scalOffset);
                scalBuf = null;
            }
            if (scalBuf != null) ArrayPool<byte>.Shared.Return(scalBuf);

            Debug.Log($"[Net] Sending snapshot: {normCount} normal + {scalCount} scaled objects");
        }

        // =====================================================================
        // AOI farewell batch — zero-velocity updates for all objects deactivated
        // this AOI tick. Batched into one message instead of one per object.
        // =====================================================================

        // =====================================================================
        // SCENE_INIT (0x10) — send physics scene description to server
        // =====================================================================

        /// <summary>
        /// Scan the scene and send all physics-relevant colliders to the server so it can
        /// start Rapier3D simulation.
        ///
        /// Two passes:
        ///   1. Registered NetworkObjects — may be Dynamic, Kinematic, or Fixed.
        ///   2. Static world geometry (floors, walls, terrain) that have a Collider but
        ///      no NetworkObject. These are sent as Fixed bodies so Rapier knows about them
        ///      even though the server never sends position updates for them.
        ///
        /// MeshColliders are skipped (no analytic Rapier shape). Call once after the scene
        /// is ready and you want the server to take over physics.
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
                else if (rb.isKinematic)
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

                // Use the collider's world-space centre so BoxCollider.center offsets are respected
                Vector3 worldPos = GetColliderWorldCenter(col, obj.transform);

                offset = Protocol.WriteSceneObject(buf, offset,
                    obj.LocalId, bodyType, shapeType,
                    worldPos, obj.transform.eulerAngles, shapeParams,
                    mass, restitution, friction, linearDamping, angularDamping);
                count++;
            }

            // === Pass 2: Static world geometry without NetworkObject (floors, walls, etc.) ===
            // IDs start after the registered NetworkObject range so they never collide.
            ushort staticId = (ushort)_objects.Count;
            foreach (var col in allColliders)
            {
                if (col == null) continue;
                if (col.GetComponent<NetworkObject>() != null) continue; // handled in Pass 1
                if (col is MeshCollider) continue;

                var rb = col.GetComponent<Rigidbody>();
                // Only include static GameObjects or trigger-less colliders without a Rigidbody
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
                float friction    = col.sharedMaterial != null ? col.sharedMaterial.dynamicFriction  : 0.4f;
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
            if (col is BoxCollider    bc) return t.TransformPoint(bc.center);
            if (col is SphereCollider sc) return t.TransformPoint(sc.center);
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
                // Half-extents in world space
                shapeParams = Vector3.Scale(bc.size, t.lossyScale) * 0.5f;
                return true;
            }
            if (col is CapsuleCollider cc)
            {
                shapeType = Protocol.PhysicsShapeType.Capsule;
                float worldRadius = cc.radius * Mathf.Max(t.lossyScale.x, t.lossyScale.z);
                // Unity cc.height = total height (cylinder + 2 hemispheres).
                // Rapier capsule_y(half_height, radius): half_height is cylindrical section only.
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
