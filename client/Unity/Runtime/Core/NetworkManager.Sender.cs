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
            if (_claimedObjects.Count == 0) return;

            int maxSize = 3 + _claimedObjects.Count * Protocol.BYTES_PER_ENTITY_SCALED;
            byte[] buf = null;
            byte[] scalBuf = null;
            int offset     = 3;
            int scalOffset = 3;
            int count      = 0;
            int scalCount  = 0;

            for (int i = _claimedObjects.Count - 1; i >= 0; i--)
            {
                var obj = _claimedObjects[i];
                if (obj == null || !obj.IsClaimedLocally)
                {
                    _claimedObjects.RemoveAt(i);
                    continue;
                }
                if (!obj.HasMoved(positionThreshold, rotationThreshold)) continue;

                if (obj.syncScale)
                {
                    if (scalBuf == null)
                    {
                        scalBuf = ArrayPool<byte>.Shared.Rent(maxSize);
                        scalBuf[0] = Protocol.MSG_SCALED_INTERACTION;
                    }
                    scalOffset = Protocol.WriteEntityScaled(scalBuf, scalOffset, obj.LocalId,
                        obj.transform.position, obj.transform.rotation, obj.transform.localScale);
                    scalCount++;
                }
                else
                {
                    if (buf == null)
                    {
                        buf = ArrayPool<byte>.Shared.Rent(maxSize);
                        buf[0] = Protocol.MSG_INTERACTION_UPDATE;
                    }
                    offset = Protocol.WriteEntityCompact(buf, offset, obj.LocalId,
                        obj.transform.position, obj.transform.rotation);
                    count++;
                }
            }

            if (count > 0)
            {
                WriteU16(buf, 1, (ushort)count);
                if (SendBinaryPooled(buf, offset))
                {
                    foreach (var obj in _claimedObjects)
                        if (obj != null && !obj.syncScale && obj.HasMoved(positionThreshold, rotationThreshold))
                            obj.MarkSent();
                }
                buf = null;
            }
            if (buf != null) ArrayPool<byte>.Shared.Return(buf);

            if (scalCount > 0)
            {
                WriteU16(scalBuf, 1, (ushort)scalCount);
                if (SendBinaryPooled(scalBuf, scalOffset))
                {
                    foreach (var obj in _claimedObjects)
                        if (obj != null && obj.syncScale && obj.HasMoved(positionThreshold, rotationThreshold))
                            obj.MarkSent();
                }
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
                if (SendBinaryPooled(buf, size)) obj.MarkSent();
            }
            else
            {
                int size = 3 + Protocol.BYTES_PER_ENTITY;
                byte[] buf = ArrayPool<byte>.Shared.Rent(size);
                buf[0] = Protocol.MSG_INTERACTION_UPDATE;
                WriteU16(buf, 1, 1);
                Protocol.WriteEntityCompact(buf, 3, obj.LocalId,
                    obj.transform.position, obj.transform.rotation);
                if (SendBinaryPooled(buf, size)) obj.MarkSent();
            }
        }

        /// <summary>Send a claim message (called by NetworkObject.Grab()).</summary>
        public void SendClaim(ushort objectId)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(6);
            buf[0] = 0xFF; buf[1] = Protocol.CTRL_CLAIM;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinaryPooled(buf, 6);
        }

        /// <summary>Send a release message (called by NetworkObject.Release()).</summary>
        public void SendRelease(ushort objectId)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(6);
            buf[0] = 0xFF; buf[1] = Protocol.CTRL_RELEASE;
            WriteU16(buf, 2, objectId);
            WriteU16(buf, 4, ClientId);
            SendBinaryPooled(buf, 6);
        }

        // =====================================================================
        // Runtime spawn / despawn
        //
        // Call Spawn to request a new networked object at runtime. The server
        // assigns a stable local id, adds it to the Rapier world, and broadcasts
        // CTRL_SPAWN to every client — each client then instantiates the prefab
        // locally and fires NetworkManager.OnObjectSpawned.
        //
        // Prefab identity is just an index into `networkedPrefabs` — every
        // client MUST have the same list in the same order.
        // =====================================================================

        /// <summary>
        /// Request the server spawn a new networked object using the prefab at
        /// <paramref name="prefabIndex"/> in <see cref="networkedPrefabs"/>.
        /// Does not return the spawned object directly — listen for
        /// <see cref="OnObjectSpawned"/> which fires when the server confirms.
        /// </summary>
        public void Spawn(int prefabIndex, Vector3 pos, Quaternion rot)
        {
            if (_ws == null || _ws.State != System.Net.WebSockets.WebSocketState.Open)
            {
                Debug.LogWarning("[Net] Spawn ignored — not connected");
                return;
            }
            if (prefabIndex < 0 || prefabIndex > 255)
            {
                Debug.LogError($"[Net] Spawn: prefab index {prefabIndex} out of range");
                return;
            }
            if (networkedPrefabs == null || prefabIndex >= networkedPrefabs.Count)
            {
                Debug.LogError($"[Net] Spawn: prefab index {prefabIndex} not in networkedPrefabs");
                return;
            }
            var prefab = networkedPrefabs[prefabIndex];
            if (prefab == null)
            {
                Debug.LogError($"[Net] Spawn: prefab at index {prefabIndex} is null");
                return;
            }

            var col = prefab.GetComponent<Collider>();
            var rb  = prefab.GetComponent<Rigidbody>();
            var np  = prefab.GetComponent<NetworkObject>();
            if (np == null || col == null)
            {
                Debug.LogError("[Net] Spawn: prefab must have NetworkObject + Collider");
                return;
            }

            if (!TryGetColliderShape(col, prefab.transform,
                out Protocol.PhysicsShapeType shapeType, out Vector3 shapeParams))
            {
                Debug.LogError("[Net] Spawn: prefab collider is not a sphere/box/capsule");
                return;
            }

            Protocol.PhysicsBodyType bodyType;
            if (rb == null) bodyType = Protocol.PhysicsBodyType.Fixed;
            else if (rb.isKinematic) bodyType = Protocol.PhysicsBodyType.KinematicPosition;
            else bodyType = Protocol.PhysicsBodyType.Dynamic;

            float mass          = rb != null ? rb.mass           : 0f;
            float linearDamp    = rb != null ? rb.linearDamping  : 0f;
            float angularDamp   = rb != null ? rb.angularDamping : 0.05f;
            float restitution   = 0.3f;
            float friction      = 0.4f;
            if (col.sharedMaterial != null)
            {
                restitution = col.sharedMaterial.bounciness;
                friction    = col.sharedMaterial.dynamicFriction;
            }

            byte[] buf = new byte[3 + Protocol.BYTES_PER_SCENE_OBJECT];
            buf[0] = 0xFF;
            buf[1] = Protocol.CTRL_SPAWN_REQUEST;
            buf[2] = (byte)prefabIndex;
            Protocol.WriteSceneObject(buf, 3,
                0 /* id — server assigns */, bodyType, shapeType,
                pos, rot.eulerAngles, shapeParams,
                mass, restitution, friction, linearDamp, angularDamp);
            SendBinary(buf);
        }

        /// <summary>
        /// Convenience overload: look up a prefab in <see cref="networkedPrefabs"/>
        /// by its GameObject name and spawn it. Case-sensitive. Logs an error
        /// and no-ops if the name isn't found.
        ///
        /// Network identity is still index-based on the wire — this is a
        /// local lookup only. Every client must have the same prefab list
        /// in the same order; renaming a prefab in one build without
        /// updating others will silently spawn the wrong thing.
        /// </summary>
        public void Spawn(string prefabName, Vector3 pos, Quaternion rot)
        {
            int idx = GetPrefabIndex(prefabName);
            if (idx < 0)
            {
                Debug.LogError($"[Net] Spawn: no prefab named '{prefabName}' in networkedPrefabs");
                return;
            }
            Spawn(idx, pos, rot);
        }

        /// <summary>
        /// Resolve a prefab name to its index in <see cref="networkedPrefabs"/>.
        /// Returns -1 if not found. First-match wins if names collide.
        /// </summary>
        public int GetPrefabIndex(string prefabName)
        {
            if (networkedPrefabs == null || string.IsNullOrEmpty(prefabName)) return -1;
            for (int i = 0; i < networkedPrefabs.Count; i++)
            {
                var p = networkedPrefabs[i];
                if (p != null && p.name == prefabName) return i;
            }
            return -1;
        }

        /// <summary>
        /// Ask the server to destroy a previously-spawned networked object.
        /// Every client will receive the despawn broadcast and tear down
        /// their local copy.
        /// </summary>
        public void Despawn(ushort localId)
        {
            if (_ws == null || _ws.State != System.Net.WebSockets.WebSocketState.Open) return;
            byte[] buf = ArrayPool<byte>.Shared.Rent(4);
            buf[0] = 0xFF;
            buf[1] = Protocol.CTRL_DESPAWN;
            Protocol.WriteU16(buf, 2, localId);
            SendBinaryPooled(buf, 4);
        }

        /// <summary>
        /// Send a push impulse — server applies momentum to the object via Rapier.
        /// Used by <see cref="PlayerPushDetector"/> when the local player walks
        /// into a pushable object. No claim involved — pure server-side effect.
        /// </summary>
        public void SendPushImpulse(ushort objectId, Vector3 velocity)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(15);
            buf[0] = Protocol.MSG_PUSH_IMPULSE;
            WriteU16(buf, 1, objectId);
            Protocol.WriteFloat(buf, 3,  velocity.x);
            Protocol.WriteFloat(buf, 7,  velocity.y);
            Protocol.WriteFloat(buf, 11, velocity.z);
            SendBinaryPooled(buf, 15);
        }

        // =====================================================================
        // Player position (0x0C) — local player body for server-side AOI
        // and the Rapier player capsule.
        //
        // Format: [0x0C][f32 x][f32 y][f32 z][f32 radius][f32 half_height]
        //         = 21 bytes
        //
        // Capsule dims come from the CharacterController on the local player
        // if present, else fall back to typical human-avatar defaults so the
        // server still has a sane shape.
        // =====================================================================

        private const float DefaultPlayerRadius = 0.3f;
        private const float DefaultPlayerHalfHeight = 0.6f;

        /// <summary>Cached CharacterController on the local player. Looked up lazily
        /// (the local player rig may spawn after NetworkManager) then reused every
        /// frame — GetComponent in a per-frame hot path was showing up in the GC
        /// profile. Invalidated when _localPlayerTransform changes (see
        /// NetworkManager.cs SetLocalPlayerTransform).</summary>
        private CharacterController _cachedPlayerCC;
        private Transform _cachedPlayerCCTransform;
        private bool _hasSentPlayerPosition;
        private Vector3 _lastSentPlayerPosition;
        private float _lastSentPlayerRadius, _lastSentPlayerHalfHeight;
        private float _lastPlayerPositionTime;

        private void SendPlayerPosition()
        {
            if (_localPlayerTransform == null) return;

            // Refresh the cached CharacterController reference if the local
            // player rig changed (scene reload, avatar swap). The lookup walks
            // children, so we don't want to repeat it every frame.
            if (_cachedPlayerCCTransform != _localPlayerTransform)
            {
                _cachedPlayerCCTransform = _localPlayerTransform;
                _cachedPlayerCC = _localPlayerTransform.GetComponent<CharacterController>()
                    ?? _localPlayerTransform.GetComponentInChildren<CharacterController>();
            }

            Vector3 p = _localPlayerTransform.position;
            float radius = DefaultPlayerRadius;
            float halfHeight = DefaultPlayerHalfHeight;

            var cc = _cachedPlayerCC;
            if (cc != null)
            {
                radius = cc.radius;
                // CC.height is the total; Rapier's capsule_y wants the half-
                // height of just the cylindrical section between hemispheres.
                halfHeight = Mathf.Max(0.01f, cc.height * 0.5f - cc.radius);

                // Position: use CC's world-space center so the capsule aligns
                // with the CC rather than being anchored at the feet.
                p = cc.transform.TransformPoint(cc.center);
            }

            // Stationary players need only a periodic refresh. The first
            // update after reconnect is always sent so the server can rebuild
            // AOI state and the Rapier player capsule.
            if (_hasSentPlayerPosition
                && (p - _lastSentPlayerPosition).sqrMagnitude < 0.0001f
                && Mathf.Abs(radius - _lastSentPlayerRadius) < 0.001f
                && Mathf.Abs(halfHeight - _lastSentPlayerHalfHeight) < 0.001f
                && Time.time - _lastPlayerPositionTime < 2f)
                return;

            byte[] buf = ArrayPool<byte>.Shared.Rent(21);
            buf[0] = Protocol.MSG_PLAYER_POS;
            Protocol.WriteFloat(buf, 1,  p.x);
            Protocol.WriteFloat(buf, 5,  p.y);
            Protocol.WriteFloat(buf, 9,  p.z);
            Protocol.WriteFloat(buf, 13, radius);
            Protocol.WriteFloat(buf, 17, halfHeight);
            if (!SendBinaryPooled(buf, 21)) return;
            _hasSentPlayerPosition = true;
            _lastSentPlayerPosition = p;
            _lastSentPlayerRadius = radius;
            _lastSentPlayerHalfHeight = halfHeight;
            _lastPlayerPositionTime = Time.time;
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
        public bool SendSceneInit()
        {
            var allColliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            // Cap the scratch buffer even if the scene has thousands of ignored
            // colliders. Reject the first supported collider beyond the relay cap.
            int maxObjects = Mathf.Min(Protocol.MAX_SCENE_OBJECTS + 1,
                _objects.Count + allColliders.Length);
            int maxSize = 3 + maxObjects * Protocol.BYTES_PER_SCENE_OBJECT;

            byte[] buf = ArrayPool<byte>.Shared.Rent(maxSize);
            buf[0] = Protocol.MSG_SCENE_INIT;
            int offset = 3;
            int count  = 0;

            // === Pass 1: NetworkObjects (Dynamic / Kinematic / Fixed) ===
            foreach (var obj in _objects)
            {
                if (obj == null) continue;
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
                if (count >= Protocol.MAX_SCENE_OBJECTS)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    Debug.LogError($"[Net] SendSceneInit: more than {Protocol.MAX_SCENE_OBJECTS} physics colliders; simplify the scene or combine static colliders.");
                    return false;
                }

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
                if (count >= Protocol.MAX_SCENE_OBJECTS)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    Debug.LogError($"[Net] SendSceneInit: more than {Protocol.MAX_SCENE_OBJECTS} physics colliders; simplify the scene or combine static colliders.");
                    return false;
                }

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
                return false;
            }

            Protocol.WriteU16(buf, 1, (ushort)count);
            if (!SendBinaryPooled(buf, offset)) return false;
            int netObjs = Mathf.Min(_objects.Count, count);
            Debug.Log($"[Net] Sent SCENE_INIT: {count} objects ({netObjs} networked + {count - netObjs} static) to server");
            return true;
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
