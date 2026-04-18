//! Runtime spawn / despawn handlers.
//!
//! Split out of handler.rs. Both messages take a short control header + a
//! scene-object descriptor (spawn) or just an obj_id (despawn), mutate the
//! room's physics world, and broadcast the result to every client.

use std::sync::Arc;

use log::{info, warn};
use tokio::sync::RwLock;

use crate::client::broadcast_all;
use crate::physics::parse_scene_init;
use crate::protocol::{read_u16, CTRL_DESPAWN, CTRL_SPAWN, MSG_SCENE_INIT};
use crate::room::Room;

/// Client asked to spawn an object at runtime. Assign it an id, add it to
/// Rapier, broadcast the full descriptor (now with id) to every client.
///
/// Wire format:
///   request:   [0xFF][0x0B][u8 prefab_idx][60B scene_object]                    = 63 bytes
///   broadcast: [0xFF][0x0C][u16 id][u8 prefab_idx][60B scene_object][u64 spawn_ms] = 73 bytes
///
/// The scene_object layout mirrors SCENE_INIT entries — same 60 bytes, same
/// parser, so bodies spawned at runtime look identical to baked-in ones.
/// The trailing spawn_ms is the server unix time at broadcast, letting every
/// client anchor deterministic subsystems (NetworkNPC wander) to a shared
/// per-object birth timestamp rather than each client's local Instantiate time.
pub(crate) async fn handle_spawn_request(
    room: &Arc<RwLock<Room>>,
    _client_id: u16,
    data: &[u8],
    room_id: &str,
) {
    const HEADER: usize = 3; // [0xFF][0x0B][u8 prefab_idx]
    const SCENE_OBJECT_BYTES: usize = 60;
    /// Cap on runtime-spawned objects per room. Without this a misbehaving or
    /// malicious client can spam CTRL_SPAWN_REQUEST up to u16::MAX (≈65k
    /// Rapier bodies) and exhaust server memory + pin the physics tick. 4096
    /// is comfortably above any sane gameplay need.
    const MAX_RUNTIME_SPAWNS: u16 = 4096;

    if data.len() < HEADER + SCENE_OBJECT_BYTES {
        return;
    }
    let prefab_idx = data[2];

    // Parse scene_object using the SCENE_INIT parser — feed it a matching
    // framed buffer: [0x10][u16 count=1][60B object].
    let mut framed = Vec::with_capacity(3 + SCENE_OBJECT_BYTES);
    framed.push(MSG_SCENE_INIT);
    framed.extend_from_slice(&1u16.to_le_bytes());
    framed.extend_from_slice(&data[HEADER..HEADER + SCENE_OBJECT_BYTES]);
    let mut desc = match parse_scene_init(&framed) {
        Some(mut list) if !list.is_empty() => list.swap_remove(0),
        _ => {
            warn!("[Room:{}] CTRL_SPAWN_REQUEST failed to parse scene object", room_id);
            return;
        }
    };

    // Need physics world + next id.
    let (pw_opt, assigned_id) = {
        let mut r = room.write().await;
        if !r.physics_ready || r.physics_world.is_none() {
            warn!("[Room:{}] CTRL_SPAWN_REQUEST before physics ready", room_id);
            return;
        }
        let id = r.next_runtime_id;
        if id >= MAX_RUNTIME_SPAWNS {
            warn!(
                "[Room:{}] CTRL_SPAWN_REQUEST: spawn cap hit ({} >= {}); refusing",
                room_id, id, MAX_RUNTIME_SPAWNS
            );
            return;
        }
        r.next_runtime_id = id.wrapping_add(1);
        (r.physics_world.clone(), id)
    };
    desc.id = assigned_id;

    if let Some(pw_arc) = pw_opt {
        let mut pw = pw_arc.lock().await;
        pw.add_object(&desc);
    }

    // Broadcast to every client:
    //   [0xFF][0x0C][u16 id][u8 prefab_idx][60B scene_object][u64 spawn_server_ms]
    let spawn_server_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);

    let mut msg = Vec::with_capacity(2 + 2 + 1 + SCENE_OBJECT_BYTES + 8);
    msg.push(0xFF);
    msg.push(CTRL_SPAWN);
    msg.extend_from_slice(&assigned_id.to_le_bytes());
    msg.push(prefab_idx);
    msg.extend_from_slice(&data[HEADER..HEADER + SCENE_OBJECT_BYTES]);
    // Patch the scene_object's own id field (its first 2 bytes) to match the
    // server-assigned id — client's parser will read it from there.
    const SCENE_OBJ_ID_OFFSET: usize = 5; // after [0xFF][0x0C][u16 outer_id][u8 prefab]
    msg[SCENE_OBJ_ID_OFFSET]     = (assigned_id & 0xFF) as u8;
    msg[SCENE_OBJ_ID_OFFSET + 1] = (assigned_id >> 8) as u8;
    msg.extend_from_slice(&spawn_server_ms.to_le_bytes());

    let r = room.read().await;
    broadcast_all(&r.clients, &msg);
    info!(
        "[Room:{}] spawned object id={} prefab={} spawn_ms={}",
        room_id, assigned_id, prefab_idx, spawn_server_ms
    );
}

/// Either direction: destroy a runtime-spawned object. Either a client asks
/// to destroy one they own, or the server is tearing one down. Server removes
/// from Rapier, clears any claim, broadcasts to all clients.
pub(crate) async fn handle_despawn(
    room: &Arc<RwLock<Room>>,
    _client_id: u16,
    data: &[u8],
    room_id: &str,
) {
    if data.len() < 4 {
        return;
    }
    let obj_id = read_u16(data, 2);

    let pw_opt = {
        let mut r = room.write().await;
        // Clear any claim + stored release bytes on the object being
        // despawned — the id is retired, no late joiner should replay
        // stale state for it.
        r.claimed_objects.remove(&obj_id);
        r.last_release_bytes.remove(&obj_id);
        r.physics_world.clone()
    };

    if let Some(pw_arc) = pw_opt {
        let mut pw = pw_arc.lock().await;
        pw.remove_object(obj_id);
    }

    let mut msg = vec![0xFF, CTRL_DESPAWN];
    msg.extend_from_slice(&obj_id.to_le_bytes());
    let r = room.read().await;
    broadcast_all(&r.clients, &msg);
    info!("[Room:{}] despawned object id={}", room_id, obj_id);
}
