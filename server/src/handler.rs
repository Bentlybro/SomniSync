use log::{debug, error, info, warn};
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::RwLock;
use tokio::time::{timeout, Duration, Instant};
use tokio_tungstenite::tungstenite::Message;

use crate::client::{broadcast_except, new_client, send, send_try, Client, ClientMsg};
use crate::persistence::PersistenceDb;
use crate::physics::{build_filtered_message, parse_scene_init, PhysicsWorld};
use crate::protocol::{
    ctrl_msg, CTRL_CLAIM, CTRL_CLAIM_REJECTED, CTRL_ERROR, CTRL_PERSIST_DATA, CTRL_PERSIST_GET,
    CTRL_PERSIST_SET, CTRL_PHYSICS_READY, CTRL_PLAYER_IDENT, CTRL_RELEASE, CTRL_WELCOME,
    MSG_ANIMATOR, MSG_EVENT, MSG_INTERACTION_UPDATE, MSG_PLAYER_POS, MSG_SCALED_INTERACTION,
    MSG_SCENE_INIT, MSG_VARIABLE_UPDATE,
};
use crate::room::Room;
use crate::room::RoomMap;

/// Handle a single client connection (runs in its own task).
pub async fn handle_connection(
    stream: tokio::net::TcpStream,
    addr: SocketAddr,
    rooms: RoomMap,
    db: PersistenceDb,
) {
    let room_id = Arc::new(tokio::sync::Mutex::new(String::from("default")));
    let room_id_clone = room_id.clone();

    let callback = move |req: &tokio_tungstenite::tungstenite::handshake::server::Request,
                         resp: tokio_tungstenite::tungstenite::handshake::server::Response|
        -> Result<tokio_tungstenite::tungstenite::handshake::server::Response, tokio_tungstenite::tungstenite::http::Response<Option<String>>> {
        let path = req.uri().path().trim_start_matches('/').to_string();
        if !path.is_empty() {
            if let Ok(mut id) = room_id_clone.try_lock() {
                *id = path;
            }
        }
        Ok(resp)
    };

    let ws = match tokio_tungstenite::accept_hdr_async(stream, callback).await {
        Ok(ws) => ws,
        Err(e) => {
            error!("[Server] WebSocket handshake failed for {}: {}", addr, e);
            return;
        }
    };

    let room_id = room_id.lock().await.clone();

    let room = {
        let mut all_rooms = rooms.write().await;
        all_rooms
            .entry(room_id.clone())
            .or_insert_with(|| {
                info!("[Rooms] Created room '{}'", room_id);
                Arc::new(RwLock::new(Room::new()))
            })
            .clone()
    };

    let (ws_tx, mut rx) = futures_util::StreamExt::split(ws);
    let client = new_client(addr, ws_tx);

    // Register client and decide whether this is the one that bootstraps physics.
    let (client_id, needs_scene_init, physics_already_active) = {
        let mut r = room.write().await;
        let id = r.add_client(client.clone());
        let physics_active = r.physics_ready;
        // Only ask for SCENE_INIT if no one else has sent one yet.
        let needs_init = !physics_active;
        info!(
            "[Room:{}] Client {} connected: client_{} ({} total)",
            room_id, addr, id, r.client_count()
        );
        (id, needs_init, physics_active)
    };

    // Send welcome: [u16 client_id][u8 needs_scene_init][u64 unix_millis]
    // unix_millis gives every client a shared clock reference so components
    // like NetworkWaypoint can do deterministic pathing without broadcasts.
    {
        let unix_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0);
        let mut payload = Vec::with_capacity(11);
        payload.extend_from_slice(&client_id.to_le_bytes());
        payload.push(if needs_scene_init { 1 } else { 0 });
        payload.extend_from_slice(&unix_ms.to_le_bytes());
        send(&client, ctrl_msg(CTRL_WELCOME, &payload)).await;
    }

    // If physics is already live, tell this client immediately so it stops any
    // local simulation and waits for server broadcasts.
    if physics_already_active {
        send(&client, ctrl_msg(CTRL_PHYSICS_READY, &[])).await;
    }

    const IDLE_TIMEOUT_SECS: u64 = 60;

    loop {
        let next = timeout(
            Duration::from_secs(IDLE_TIMEOUT_SECS),
            futures_util::StreamExt::next(&mut rx),
        )
        .await;

        let msg = match next {
            Ok(Some(Ok(m))) => m,
            Ok(Some(Err(e))) => {
                warn!("[Room:{}] Error from client_{}: {}", room_id, client_id, e);
                break;
            }
            Ok(None) => break,
            Err(_) => {
                warn!(
                    "[Room:{}] client_{} idle for >{}s, disconnecting",
                    room_id, client_id, IDLE_TIMEOUT_SECS
                );
                break;
            }
        };

        match msg {
            Message::Binary(data) => {
                if data.is_empty() {
                    continue;
                }
                handle_binary(&room, &client, client_id, &data, &room_id, &db).await;
            }
            Message::Close(_) => break,
            Message::Ping(d) => {
                let _ = client.tx.send(ClientMsg::Pong(d.into())).await;
            }
            _ => {}
        }
    }

    handle_disconnect(&room, client_id, &room_id).await;

    {
        let r = room.read().await;
        if r.client_count() == 0 {
            drop(r);
            let mut all_rooms = rooms.write().await;
            if let Some(r) = all_rooms.get(&room_id) {
                if r.read().await.client_count() == 0 {
                    all_rooms.remove(&room_id);
                    info!("[Rooms] Destroyed empty room '{}'", room_id);
                }
            }
        }
    }
}

/// Handle a binary message from a client.
async fn handle_binary(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    let msg_type = data[0];

    match msg_type {
        MSG_SCENE_INIT => {
            handle_scene_init(room, &room_id.to_string(), data).await;
        }

        MSG_PLAYER_POS => {
            // [0x0C][f32 x][f32 y][f32 z] = 13 bytes
            if data.len() < 13 {
                return;
            }
            let x = read_f32(data, 1);
            let y = read_f32(data, 5);
            let z = read_f32(data, 9);
            let mut r = room.write().await;
            r.player_positions.insert(client_id, [x, y, z]);
        }

        MSG_INTERACTION_UPDATE | MSG_SCALED_INTERACTION => {
            // Held-object updates. Only accept if this client actually owns the object.
            // We still relay to other clients; the server Rapier body also tracks the
            // position via the CTRL_CLAIM flow.
            handle_held_object_update(room, client_id, data).await;
        }

        MSG_VARIABLE_UPDATE | MSG_EVENT | MSG_ANIMATOR => {
            // Pure relay — no server state involved.
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data);
        }

        0xFF => {
            handle_control(room, client, client_id, data, room_id, db).await;
        }

        _ => {
            warn!(
                "[Room:{}] Unknown message type 0x{:02X} from client_{}",
                room_id, msg_type, client_id
            );
        }
    }
}

/// Handle an interaction-update (0x03 or 0x09) for a held object.
/// The server parses each entity, updates the kinematic body in Rapier, and
/// relays only the entities the sender actually owns.
///
/// Entity wire format:
///   compact  (0x03): 22 bytes — [u16 id][f32 x/y/z][i16 qx/qy/qz/qw]
///   scaled   (0x09): 28 bytes — compact + [f16 sx/sy/sz]
async fn handle_held_object_update(room: &Arc<RwLock<Room>>, client_id: u16, data: &[u8]) {
    if data.len() < 3 {
        return;
    }
    let is_scaled = data[0] == MSG_SCALED_INTERACTION;
    let bytes_per_entity = if is_scaled { 28 } else { 22 };
    let count = u16::from_le_bytes([data[1], data[2]]) as usize;
    if data.len() < 3 + count * bytes_per_entity {
        return;
    }

    let (claimed_snapshot, pw_opt) = {
        let r = room.read().await;
        (r.claimed_objects.clone(), r.physics_world.clone())
    };

    let mut relay = Vec::with_capacity(data.len());
    relay.push(data[0]);
    relay.extend_from_slice(&[0, 0]); // count placeholder
    let mut kept: u16 = 0;
    let mut pw_guard = match &pw_opt {
        Some(arc) => Some(arc.lock().await),
        None => None,
    };

    let mut offset = 3usize;
    for _ in 0..count {
        let obj_id = u16::from_le_bytes([data[offset], data[offset + 1]]);
        if claimed_snapshot.get(&obj_id) != Some(&client_id) {
            offset += bytes_per_entity;
            continue;
        }

        if let Some(pw) = pw_guard.as_deref_mut() {
            let px = read_f32(data, offset + 2);
            let py = read_f32(data, offset + 6);
            let pz = read_f32(data, offset + 10);
            let qx = read_i16(data, offset + 14) as f32 / 32767.0;
            let qy = read_i16(data, offset + 16) as f32 / 32767.0;
            let qz = read_i16(data, offset + 18) as f32 / 32767.0;
            let qw = read_i16(data, offset + 20) as f32 / 32767.0;
            // Velocity isn't on this wire format; pass zero (server uses cached
            // velocity on release for momentum transfer).
            pw.update_claimed(obj_id, px, py, pz, qx, qy, qz, qw, 0.0, 0.0, 0.0);
        }

        relay.extend_from_slice(&data[offset..offset + bytes_per_entity]);
        kept += 1;
        offset += bytes_per_entity;
    }

    if kept == 0 {
        return;
    }

    relay[1] = (kept & 0xFF) as u8;
    relay[2] = (kept >> 8) as u8;

    drop(pw_guard);
    let room_r = room.read().await;
    broadcast_except(&room_r.clients, client_id, &relay);
}

/// Handle a SCENE_INIT (0x10) — bootstrap the authoritative physics world.
async fn handle_scene_init(room: &Arc<RwLock<Room>>, room_id: &str, data: &[u8]) {
    // First SCENE_INIT wins; ignore the rest.
    {
        let r = room.read().await;
        if r.physics_ready {
            debug!("[Room:{}] SCENE_INIT ignored (physics already ready)", room_id);
            return;
        }
    }

    let objects = match parse_scene_init(data) {
        Some(objs) => objs,
        None => {
            warn!("[Room:{}] Failed to parse SCENE_INIT", room_id);
            return;
        }
    };

    info!(
        "[Room:{}] SCENE_INIT received: {} physics objects. Entering physics mode.",
        room_id,
        objects.len()
    );

    let mut world = PhysicsWorld::new();
    for desc in &objects {
        world.add_object(desc);
    }

    let pw_arc = Arc::new(tokio::sync::Mutex::new(world));

    // Mark physics ready and broadcast to every connected client.
    {
        let mut r = room.write().await;
        r.physics_ready = true;
        r.physics_world = Some(pw_arc.clone());
        let ready_msg = vec![0xFF_u8, CTRL_PHYSICS_READY];
        crate::client::broadcast_all(&r.clients, &ready_msg);
    }

    // Spawn the physics tick task.
    let room_weak = Arc::downgrade(room);
    let room_id_owned = room_id.to_string();

    tokio::spawn(async move {
        physics_tick_task(room_weak, pw_arc, room_id_owned).await;
    });
}

/// Physics tick task — 50Hz simulation, 20Hz broadcast.
async fn physics_tick_task(
    room_weak: std::sync::Weak<RwLock<Room>>,
    pw_arc: Arc<tokio::sync::Mutex<PhysicsWorld>>,
    room_id: String,
) {
    const PHYSICS_HZ: u64 = 50;
    const BROADCAST_HZ: u64 = 20;
    const PHYSICS_INTERVAL: Duration = Duration::from_millis(1000 / PHYSICS_HZ);
    const BROADCAST_INTERVAL: Duration = Duration::from_millis(1000 / BROADCAST_HZ);
    const ROOM_CHECK_INTERVAL: Duration = Duration::from_secs(2);

    let mut last_physics = Instant::now();
    let mut last_broadcast = Instant::now();
    let mut last_room_check = Instant::now();
    let mut server_sequence: u16 = 0;
    let mut tick: u32 = 0;

    info!("[Room:{}] Physics tick task started (50Hz sim / 20Hz broadcast)", room_id);

    loop {
        let now = Instant::now();
        let next_physics = last_physics + PHYSICS_INTERVAL;
        let next_broadcast = last_broadcast + BROADCAST_INTERVAL;
        let next_check = last_room_check + ROOM_CHECK_INTERVAL;

        let wake_at = next_physics.min(next_broadcast).min(next_check);
        if wake_at > now {
            tokio::time::sleep_until(wake_at).await;
        }

        let now = Instant::now();
        let do_physics = now >= next_physics;
        let do_broadcast = now >= next_broadcast;

        // Single physics-mutex acquisition per tick: step + snapshot for broadcast.
        let snapshot_opt = if do_physics || do_broadcast {
            let mut pw = pw_arc.lock().await;
            if do_physics {
                last_physics = now;
                pw.step();
            }
            if do_broadcast {
                last_broadcast = now;
                Some(pw.snapshot_dynamic())
            } else {
                None
            }
        } else {
            None
        };

        if do_broadcast {
            let snapshot = match snapshot_opt {
                Some(s) => s,
                None => continue,
            };

            server_sequence = server_sequence.wrapping_add(1);
            let seq = server_sequence;
            tick = tick.wrapping_add(1);

            let room_arc = match room_weak.upgrade() {
                Some(r) => r,
                None => {
                    info!("[Room:{}] Physics task: room deallocated, stopping", room_id);
                    return;
                }
            };

            // Hold the read lock only long enough to snapshot per-client state.
            let (clients_clone, player_pos_clone): (
                Vec<(u16, Arc<Client>)>,
                std::collections::HashMap<u16, [f32; 3]>,
            ) = {
                let r = room_arc.read().await;
                if r.client_count() == 0 {
                    info!("[Room:{}] Physics task: no clients, stopping", room_id);
                    return;
                }
                (
                    r.clients.iter().map(|(&id, c)| (id, c.clone())).collect(),
                    r.player_positions.clone(),
                )
            };

            // Build + send one filtered message per client. Per-client because
            // AOI + LOD depend on where that player is.
            for (cid, client) in &clients_clone {
                let pp = player_pos_clone.get(cid).copied();
                if let Some(msg) = build_filtered_message(&snapshot, pp, tick, seq) {
                    send_try(client, bytes::Bytes::from(msg));
                }
            }
        }

        if now >= next_check {
            last_room_check = now;
            let room_arc = match room_weak.upgrade() {
                Some(r) => r,
                None => {
                    info!("[Room:{}] Physics task: room deallocated, stopping", room_id);
                    return;
                }
            };
            if room_arc.read().await.client_count() == 0 {
                info!("[Room:{}] Physics task: no clients remain, stopping", room_id);
                return;
            }
        }
    }
}

/// Handle control messages (0xFF).
async fn handle_control(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    if data.len() < 2 {
        return;
    }
    let ctrl = data[1];

    // Persistence
    if ctrl == CTRL_PLAYER_IDENT {
        handle_player_ident(room, client_id, data, room_id).await;
        return;
    }
    if ctrl == CTRL_PERSIST_SET {
        handle_persist_set(room, client, client_id, data, room_id, db).await;
        return;
    }
    if ctrl == CTRL_PERSIST_GET {
        handle_persist_get(room, client, client_id, data, room_id, db).await;
        return;
    }

    // Claim/release need at least 6 bytes (obj_id + sender_id).
    if data.len() < 6 {
        return;
    }
    let obj_id = u16::from_le_bytes([data[2], data[3]]);

    match ctrl {
        CTRL_CLAIM => {
            let (physics_world_opt, rejected) = {
                let mut room = room.write().await;
                if let Some(&owner) = room.claimed_objects.get(&obj_id) {
                    if owner != client_id {
                        debug!(
                            "[Room:{}] client_{} tried to claim {} but owned by client_{}",
                            room_id, client_id, obj_id, owner
                        );
                        (room.physics_world.clone(), true)
                    } else {
                        // Same client re-claiming their own object — accept idempotently.
                        (room.physics_world.clone(), false)
                    }
                } else {
                    room.claimed_objects.insert(obj_id, client_id);
                    debug!("[Room:{}] client_{} claimed object {}", room_id, client_id, obj_id);
                    let mut relay = vec![0xFF, CTRL_CLAIM];
                    relay.extend_from_slice(&obj_id.to_le_bytes());
                    relay.extend_from_slice(&client_id.to_le_bytes());
                    broadcast_except(&room.clients, client_id, &relay);
                    (room.physics_world.clone(), false)
                }
            };

            if rejected {
                // Tell the requester to unwind its optimistic grab.
                send(
                    client,
                    ctrl_msg(CTRL_CLAIM_REJECTED, &obj_id.to_le_bytes()),
                )
                .await;
                return;
            }

            if let Some(pw_arc) = physics_world_opt {
                let mut pw = pw_arc.lock().await;
                pw.claim(obj_id);
            }
        }
        CTRL_RELEASE => {
            let physics_world_opt = {
                let mut room = room.write().await;
                // Only the owner can release.
                if room.claimed_objects.get(&obj_id) != Some(&client_id) {
                    return;
                }
                room.claimed_objects.remove(&obj_id);
                debug!("[Room:{}] client_{} released object {}", room_id, client_id, obj_id);
                let mut relay = vec![0xFF, CTRL_RELEASE];
                relay.extend_from_slice(&obj_id.to_le_bytes());
                relay.extend_from_slice(&client_id.to_le_bytes());
                broadcast_except(&room.clients, client_id, &relay);
                room.physics_world.clone()
            };

            if let Some(pw_arc) = physics_world_opt {
                let mut pw = pw_arc.lock().await;
                pw.release(obj_id);
            }
        }
        _ => {}
    }
}

async fn handle_player_ident(room: &Arc<RwLock<Room>>, client_id: u16, data: &[u8], room_id: &str) {
    {
        let room_r = room.read().await;
        if room_r.player_ids.contains_key(&client_id) {
            warn!(
                "[Room:{}] client_{} tried to re-register identity — ignored",
                room_id, client_id
            );
            return;
        }
    }
    if data.len() < 4 {
        return;
    }
    let len = data[2] as usize;
    const MAX_ID_LEN: usize = 64;
    if len > MAX_ID_LEN || data.len() < 3 + len {
        return;
    }
    if let Ok(uuid) = std::str::from_utf8(&data[3..3 + len]) {
        let uuid = uuid.to_string();
        let mut room = room.write().await;
        room.player_ids.insert(client_id, uuid.clone());
        info!("[Room:{}] client_{} identified as player '{}'", room_id, client_id, uuid);
    }
}

async fn handle_persist_set(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    let mut offset = 2usize;
    if data.len() < offset + 1 {
        return;
    }

    const MAX_NS_LEN: usize = 64;
    const MAX_KEY_LEN: usize = 128;
    const MAX_VALUE_LEN: usize = 4096;

    let ns_len = data[offset] as usize;
    offset += 1;
    if ns_len > MAX_NS_LEN || data.len() < offset + ns_len {
        return;
    }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 {
        return;
    }
    let key_len = data[offset] as usize;
    offset += 1;
    if key_len > MAX_KEY_LEN || data.len() < offset + key_len {
        return;
    }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };
    offset += key_len;

    if data.len() < offset + 2 {
        return;
    }
    let val_len = u16::from_le_bytes([data[offset], data[offset + 1]]) as usize;
    offset += 2;
    if val_len > MAX_VALUE_LEN {
        warn!(
            "[Room:{}] Persist SET from client_{} exceeded max value size ({} bytes)",
            room_id, client_id, val_len
        );
        send(client, ctrl_msg(CTRL_ERROR, b"persist_value_too_large")).await;
        return;
    }
    if data.len() < offset + val_len {
        return;
    }
    let value = match std::str::from_utf8(&data[offset..offset + val_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    if let Some(pid) = player_id {
        debug!(
            "[Room:{}] Persist SET player='{}' ns='{}' key='{}'",
            room_id, pid, namespace, key
        );
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.set(&pid, &namespace, &key, &value))
            .await
            .ok();
    } else {
        warn!(
            "[Room:{}] Persist SET from client_{} with no registered player ID",
            room_id, client_id
        );
    }
}

async fn handle_persist_get(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    let mut offset = 2usize;
    if data.len() < offset + 2 {
        return;
    }
    let req_id = u16::from_le_bytes([data[offset], data[offset + 1]]);
    offset += 2;

    const MAX_NS_LEN: usize = 64;
    const MAX_KEY_LEN: usize = 128;

    if data.len() < offset + 1 {
        return;
    }
    let ns_len = data[offset] as usize;
    offset += 1;
    if ns_len > MAX_NS_LEN || data.len() < offset + ns_len {
        return;
    }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 {
        return;
    }
    let key_len = data[offset] as usize;
    offset += 1;
    if key_len > MAX_KEY_LEN || data.len() < offset + key_len {
        return;
    }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    let value = if let Some(pid) = player_id {
        debug!(
            "[Room:{}] Persist GET player='{}' ns='{}' key='{}'",
            room_id, pid, namespace, key
        );
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.get(&pid, &namespace, &key))
            .await
            .unwrap_or(None)
    } else {
        warn!(
            "[Room:{}] Persist GET from client_{} with no registered player ID",
            room_id, client_id
        );
        None
    };

    let found = value.is_some();
    let val_bytes = value.as_deref().unwrap_or("").as_bytes();
    let mut resp = Vec::with_capacity(7 + val_bytes.len());
    resp.push(0xFF);
    resp.push(CTRL_PERSIST_DATA);
    resp.extend_from_slice(&req_id.to_le_bytes());
    resp.push(if found { 1 } else { 0 });
    resp.extend_from_slice(&(val_bytes.len() as u16).to_le_bytes());
    resp.extend_from_slice(val_bytes);

    send(client, resp).await;
}

/// On disconnect: release any held objects (restore Rapier to Dynamic), clean
/// up client state. No master promotion — the server keeps simulating.
async fn handle_disconnect(room: &Arc<RwLock<Room>>, client_id: u16, room_id: &str) {
    let (released_ids, pw_opt) = {
        let mut room_guard = room.write().await;
        let released = room_guard.remove_client(client_id);
        info!(
            "[Room:{}] client_{} disconnected ({} remaining, {} objects released)",
            room_id,
            client_id,
            room_guard.client_count(),
            released.len()
        );
        (released, room_guard.physics_world.clone())
    };

    if let Some(pw_arc) = pw_opt {
        if !released_ids.is_empty() {
            let mut pw = pw_arc.lock().await;
            for obj_id in &released_ids {
                pw.release(*obj_id);
            }
        }
    }

    // Broadcast release for every object this client was holding so other
    // clients stop showing the held-object visual state.
    if !released_ids.is_empty() {
        let room_r = room.read().await;
        for obj_id in &released_ids {
            let mut relay = vec![0xFF, CTRL_RELEASE];
            relay.extend_from_slice(&obj_id.to_le_bytes());
            relay.extend_from_slice(&client_id.to_le_bytes());
            crate::client::broadcast_all(&room_r.clients, &relay);
        }
    }
}

// ─── Inline binary helpers ────────────────────────────────────────────────────

#[inline]
fn read_f32(data: &[u8], offset: usize) -> f32 {
    f32::from_le_bytes([data[offset], data[offset + 1], data[offset + 2], data[offset + 3]])
}

#[inline]
fn read_i16(data: &[u8], offset: usize) -> i16 {
    i16::from_le_bytes([data[offset], data[offset + 1]])
}
