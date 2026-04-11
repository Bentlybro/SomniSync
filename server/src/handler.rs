use log::{debug, error, info, warn};
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::RwLock;
use tokio::time::{timeout, Duration, Instant};
use tokio_tungstenite::tungstenite::Message;

use crate::client::{broadcast_except, broadcast_all, new_client, send, Client, ClientMsg};
use crate::persistence::PersistenceDb;
use crate::physics::{parse_scene_init, PhysicsWorld};
use crate::protocol::{
    ctrl_msg, ctrl_msg_u16, CTRL_AUTHORITY_ASSIGN, CTRL_CLAIM, CTRL_ERROR, CTRL_MASTER_CHANGED,
    CTRL_PERSIST_DATA, CTRL_PERSIST_GET, CTRL_PERSIST_SET, CTRL_PHYSICS_READY,
    CTRL_PLAYER_IDENT, CTRL_PLAYER_POSITION, CTRL_RELEASE, CTRL_SEND_SNAPSHOT,
    CTRL_YOU_ARE_MASTER, CTRL_YOU_ARE_SPECTATOR, MSG_ANIMATOR, MSG_EVENT, MSG_INTERACTION_UPDATE,
    MSG_POSITION_UPDATE, MSG_POSITION_UPDATE_SCALED, MSG_POSITION_VELOCITY, MSG_RIGIDBODY,
    MSG_SCALED_INTERACTION, MSG_SCENE_INIT, MSG_SNAPSHOT, MSG_SNAPSHOT_SCALED,
    MSG_VARIABLE_UPDATE,
};
use crate::room::Room;
use crate::room::RoomMap;

/// AOI radius squared for physics-mode position filtering (50 m radius)
const AOI_RADIUS_SQ: f32 = 2500.0; // 50m²

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

    let (client_id, is_master) = {
        let mut r = room.write().await;
        let (id, is_master) = r.add_client(client.clone());
        info!(
            "[Room:{}] Client {} connected: client_{} ({} total) [{}]",
            room_id, addr, id, r.client_count(),
            if is_master { "MASTER" } else { "SPECTATOR" }
        );
        (id, is_master)
    };

    if is_master {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_MASTER, client_id)).await;
    } else {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_SPECTATOR, client_id)).await;
        send_cached_snapshot(&room, &client, client_id, &room_id).await;
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
            Ok(None) => break, // stream closed cleanly
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

/// Send cached snapshots to a newly connected spectator + request fresh from master.
async fn send_cached_snapshot(room: &Arc<RwLock<Room>>, client: &Client, client_id: u16, room_id: &str) {
    let room = room.read().await;
    if let Some(ref snapshot) = room.latest_snapshot {
        info!(
            "[Room:{}] Sending cached snapshot to client_{} ({} bytes)",
            room_id, client_id, snapshot.len()
        );
        send(client, snapshot.clone()).await;
    }
    if let Some(ref scaled) = room.latest_scaled_snapshot {
        info!(
            "[Room:{}] Sending cached scaled snapshot to client_{} ({} bytes)",
            room_id, client_id, scaled.len()
        );
        send(client, scaled.clone()).await;
    }
    if let Some(master_id) = room.master_id {
        if let Some(master) = room.clients.get(&master_id) {
            info!(
                "[Room:{}] Requesting fresh snapshot from master (client_{}) for client_{}",
                room_id, master_id, client_id
            );
            send(master, ctrl_msg_u16(CTRL_SEND_SNAPSHOT, client_id)).await;
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
        MSG_POSITION_UPDATE => {
            let room = room.read().await;
            if room.master_id != Some(client_id) {
                return;
            }
            broadcast_except(&room.clients, client_id, data);
        }

        MSG_SNAPSHOT => {
            let mut room = room.write().await;
            if room.master_id != Some(client_id) {
                return;
            }
            const MAX_SNAPSHOT_BYTES: usize = 512 * 1024; // 512 KB
            if data.len() > MAX_SNAPSHOT_BYTES {
                warn!("[Room:{}] Snapshot from master too large ({} bytes), rejecting", room_id, data.len());
                send(client, ctrl_msg(CTRL_ERROR, b"snapshot_too_large")).await;
                return;
            }
            info!(
                "[Room:{}] Received snapshot from master ({} bytes), caching",
                room_id, data.len()
            );
            room.latest_snapshot = Some(data.to_vec());
            broadcast_except(&room.clients, client_id, data);
        }

        MSG_POSITION_VELOCITY => {
            // In physics mode: only relay 0x0B for claimed objects owned by this sender.
            // In relay mode: broadcast as-is.
            let (physics_mode, physics_world_opt) = {
                let r = room.read().await;
                (r.physics_mode, r.physics_world.clone())
            };

            if physics_mode {
                if let Some(pw_arc) = physics_world_opt {
                    handle_physics_mode_0x0b(room, client_id, data, room_id, pw_arc).await;
                }
            } else {
                let room = room.read().await;
                broadcast_except(&room.clients, client_id, data);
            }
        }

        MSG_SCENE_INIT => {
            handle_scene_init(room, &room_id.to_string(), data).await;
        }

        MSG_SNAPSHOT_SCALED => {
            let mut room = room.write().await;
            if room.master_id != Some(client_id) {
                return;
            }
            const MAX_SCALED_SNAPSHOT_BYTES: usize = 512 * 1024;
            if data.len() > MAX_SCALED_SNAPSHOT_BYTES {
                warn!("[Room:{}] Scaled snapshot from master too large ({} bytes), dropping", room_id, data.len());
                return;
            }
            room.latest_scaled_snapshot = Some(data.to_vec());
            broadcast_except(&room.clients, client_id, data);
        }

        MSG_INTERACTION_UPDATE | MSG_VARIABLE_UPDATE | MSG_POSITION_UPDATE_SCALED
        | MSG_EVENT | MSG_ANIMATOR | MSG_RIGIDBODY | MSG_SCALED_INTERACTION => {
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

/// Handle a 0x0B message in physics mode.
/// Only relay entities that are claimed by this sender; update their kinematic positions.
async fn handle_physics_mode_0x0b(
    room: &Arc<RwLock<Room>>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    pw_arc: Arc<tokio::sync::Mutex<PhysicsWorld>>,
) {
    // Minimum size: [0x0B][u16 sender][u16 seq][u16 count] = 7
    if data.len() < 7 {
        return;
    }

    use crate::protocol::MSG_POSITION_VELOCITY;
    const BYTES_PER_ENTITY: usize = 20;

    let count = u16::from_le_bytes([data[5], data[6]]) as usize;
    if data.len() < 7 + count * BYTES_PER_ENTITY {
        return;
    }

    // Parse all entities from the message
    let mut offset = 7usize;
    let mut relayable: Vec<(u16, [f32; 3], [f32; 3], [f32; 3])> = Vec::new();

    // Read claimed objects for this room (under read lock)
    let claimed_objects_snapshot = {
        let r = room.read().await;
        r.claimed_objects.clone()
    };

    for _ in 0..count {
        if offset + BYTES_PER_ENTITY > data.len() {
            break;
        }

        let obj_id = u16::from_le_bytes([data[offset], data[offset + 1]]);
        let px = read_f16(&data[offset + 2..]);
        let py = read_f16(&data[offset + 4..]);
        let pz = read_f16(&data[offset + 6..]);
        let rx_deg = read_rot_u16(&data[offset + 8..]);
        let ry_deg = read_rot_u16(&data[offset + 10..]);
        let rz_deg = read_rot_u16(&data[offset + 12..]);
        let vx = read_f16(&data[offset + 14..]);
        let vy = read_f16(&data[offset + 16..]);
        let vz = read_f16(&data[offset + 18..]);
        offset += BYTES_PER_ENTITY;

        // Only accept this update if this sender claims the object
        if claimed_objects_snapshot.get(&obj_id) != Some(&client_id) {
            continue;
        }

        // Update kinematic position in physics world
        {
            let mut pw = pw_arc.lock().await;
            pw.update_claimed(obj_id, px, py, pz, rx_deg, ry_deg, rz_deg, vx, vy, vz);
        }

        relayable.push((obj_id, [px, py, pz], [rx_deg, ry_deg, rz_deg], [vx, vy, vz]));
    }

    if relayable.is_empty() {
        return;
    }

    // Build a relay 0x0B message containing only the claimed entities
    // Preserve the original sender_id and sequence from the incoming message
    let mut relay_buf = Vec::with_capacity(7 + relayable.len() * BYTES_PER_ENTITY);
    relay_buf.push(MSG_POSITION_VELOCITY);
    relay_buf.push(data[1]); // sender_id lo
    relay_buf.push(data[2]); // sender_id hi
    relay_buf.push(data[3]); // sequence lo
    relay_buf.push(data[4]); // sequence hi
    push_u16_vec(&mut relay_buf, relayable.len() as u16);

    for (obj_id, pos, rot_deg, vel) in &relayable {
        push_u16_vec(&mut relay_buf, *obj_id);
        push_f16_vec(&mut relay_buf, pos[0]);
        push_f16_vec(&mut relay_buf, pos[1]);
        push_f16_vec(&mut relay_buf, pos[2]);
        push_rot_u16_vec(&mut relay_buf, rot_deg[0]);
        push_rot_u16_vec(&mut relay_buf, rot_deg[1]);
        push_rot_u16_vec(&mut relay_buf, rot_deg[2]);
        push_f16_vec(&mut relay_buf, vel[0]);
        push_f16_vec(&mut relay_buf, vel[1]);
        push_f16_vec(&mut relay_buf, vel[2]);
    }

    let room_r = room.read().await;
    broadcast_except(&room_r.clients, client_id, &relay_buf);
    debug!(
        "[Room:{}] Relayed {}/{} claimed entities from client_{} in physics mode",
        room_id,
        relayable.len(),
        count,
        client_id
    );
}

/// Handle a SCENE_INIT (0x10) message — set up server-side physics.
async fn handle_scene_init(room: &Arc<RwLock<Room>>, room_id: &str, data: &[u8]) {
    // First client to send SCENE_INIT wins; ignore duplicates.
    {
        let r = room.read().await;
        if r.physics_mode {
            debug!("[Room:{}] SCENE_INIT ignored (physics already active)", room_id);
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

    // Store physics world and set physics_mode, broadcast CTRL_PHYSICS_READY
    {
        let mut r = room.write().await;
        r.physics_mode = true;
        r.physics_world = Some(pw_arc.clone());

        // [0xFF][0x08] — no payload needed
        let ready_msg = vec![0xFF_u8, CTRL_PHYSICS_READY];
        broadcast_all(&r.clients, &ready_msg);
    }

    // Spawn the physics tick task
    let room_weak = Arc::downgrade(room);
    let room_id_owned = room_id.to_string();

    tokio::spawn(async move {
        physics_tick_task(room_weak, pw_arc, room_id_owned).await;
    });
}

/// Physics tick task — runs at 50Hz physics, 20Hz broadcast.
async fn physics_tick_task(
    room_weak: std::sync::Weak<RwLock<Room>>,
    pw_arc: Arc<tokio::sync::Mutex<PhysicsWorld>>,
    room_id: String,
) {
    const PHYSICS_HZ: u64 = 50;
    const BROADCAST_HZ: u64 = 20;
    const PHYSICS_INTERVAL: Duration = Duration::from_millis(1000 / PHYSICS_HZ);
    const BROADCAST_INTERVAL: Duration = Duration::from_millis(1000 / BROADCAST_HZ);
    // Check for empty room every 2 seconds
    const ROOM_CHECK_INTERVAL: Duration = Duration::from_secs(2);

    let mut last_physics    = Instant::now();
    let mut last_broadcast  = Instant::now();
    let mut last_room_check = Instant::now();
    let mut server_sequence: u16 = 0;

    info!("[Room:{}] Physics tick task started (50Hz sim / 20Hz broadcast)", room_id);

    loop {
        // Yield to the tokio runtime; sleep the minimum needed interval
        let now = Instant::now();
        let next_physics   = last_physics   + PHYSICS_INTERVAL;
        let next_broadcast = last_broadcast + BROADCAST_INTERVAL;
        let next_check     = last_room_check + ROOM_CHECK_INTERVAL;

        let wake_at = next_physics.min(next_broadcast).min(next_check);
        if wake_at > now {
            tokio::time::sleep_until(wake_at).await;
        }

        let now = Instant::now();

        // ── Physics step ────────────────────────────────────────────────────────
        if now >= next_physics {
            last_physics = now;
            let mut pw = pw_arc.lock().await;
            pw.step();
        }

        // ── Broadcast position message ──────────────────────────────────────────
        if now >= next_broadcast {
            last_broadcast = now;

            let msg_opt = {
                let mut pw = pw_arc.lock().await;
                pw.build_position_message(&mut server_sequence)
            };

            if let Some(msg) = msg_opt {
                // Upgrade room weak pointer to check if room still exists
                let room_arc = match room_weak.upgrade() {
                    Some(r) => r,
                    None => {
                        info!("[Room:{}] Physics task: room deallocated, stopping", room_id);
                        return;
                    }
                };

                let r = room_arc.read().await;
                if r.client_count() == 0 {
                    info!("[Room:{}] Physics task: no clients, stopping", room_id);
                    return;
                }

                // Per-client AOI filtering: only send objects within AOI_RADIUS_SQ of player
                for (&cid, client) in &r.clients {
                    let player_pos = r.player_positions.get(&cid).copied();

                    match player_pos {
                        None => {
                            // No known player position — send everything
                            send(client, msg.clone()).await;
                        }
                        Some(ppos) => {
                            let filtered = filter_0x0b_by_aoi(&msg, ppos, AOI_RADIUS_SQ);
                            if let Some(filtered_msg) = filtered {
                                send(client, filtered_msg).await;
                            }
                        }
                    }
                }
            }
        }

        // ── Room liveness check ─────────────────────────────────────────────────
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

/// Filter a 0x0B position message to only include entities within `radius_sq` of `player_pos`.
/// Returns None if no entities pass the filter.
/// Reuses the same sequence number from the original message so all clients on the same physics
/// tick share one sequence, preventing the dedup logic from rejecting valid updates.
fn filter_0x0b_by_aoi(
    msg: &[u8],
    player_pos: [f32; 3],
    radius_sq: f32,
) -> Option<Vec<u8>> {
    if msg.len() < 7 {
        return None;
    }

    const BYTES_PER_ENTITY: usize = 20;
    let count = u16::from_le_bytes([msg[5], msg[6]]) as usize;

    let mut filtered: Vec<&[u8]> = Vec::with_capacity(count);
    let mut offset = 7usize;

    for _ in 0..count {
        if offset + BYTES_PER_ENTITY > msg.len() {
            break;
        }
        let entity = &msg[offset..offset + BYTES_PER_ENTITY];

        // Entity position is at bytes 2..8 (f16 x, y, z)
        let ex = read_f16(&entity[2..]);
        let ey = read_f16(&entity[4..]);
        let ez = read_f16(&entity[6..]);

        let dx = ex - player_pos[0];
        let dy = ey - player_pos[1];
        let dz = ez - player_pos[2];
        let dist_sq = dx * dx + dy * dy + dz * dz;

        if dist_sq <= radius_sq {
            filtered.push(entity);
        }

        offset += BYTES_PER_ENTITY;
    }

    if filtered.is_empty() {
        return None;
    }

    // Reuse the original sequence number from the unfiltered message (bytes 3-4)
    // so all clients on the same physics tick share the same sequence.
    let orig_seq = u16::from_le_bytes([msg[3], msg[4]]);

    let mut out = Vec::with_capacity(7 + filtered.len() * BYTES_PER_ENTITY);
    out.push(0x0B);
    push_u16_vec(&mut out, 0); // sender = server
    push_u16_vec(&mut out, orig_seq);
    push_u16_vec(&mut out, filtered.len() as u16);
    for entity in filtered {
        out.extend_from_slice(entity);
    }

    Some(out)
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

    // --- Player position: broadcast as-is + update server-side position map ---
    if ctrl == CTRL_PLAYER_POSITION {
        if data.len() >= 10 {
            let px = read_f16(&data[4..]);
            let py = read_f16(&data[6..]);
            let pz = read_f16(&data[8..]);

            {
                let mut r = room.write().await;
                if r.physics_mode {
                    r.player_positions.insert(client_id, [px, py, pz]);
                }
                broadcast_except(&r.clients, client_id, data);
            }
        }
        return;
    }

    // --- Authority assignment: broadcast as-is ---
    if ctrl == CTRL_AUTHORITY_ASSIGN {
        if data.len() >= 6 {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data);
        }
        return;
    }

    // --- Persistence: player identity registration (once per connection only) ---
    if ctrl == CTRL_PLAYER_IDENT {
        {
            let room_r = room.read().await;
            if room_r.player_ids.contains_key(&client_id) {
                warn!("[Room:{}] client_{} tried to re-register identity — ignored", room_id, client_id);
                return;
            }
        }
        if data.len() >= 4 {
            let len = data[2] as usize;
            const MAX_ID_LEN: usize = 64;
            if len > MAX_ID_LEN { return; }
            if data.len() >= 3 + len {
                if let Ok(uuid) = std::str::from_utf8(&data[3..3 + len]) {
                    let uuid = uuid.to_string();
                    let mut room = room.write().await;
                    room.player_ids.insert(client_id, uuid.clone());
                    info!("[Room:{}] client_{} identified as player '{}'", room_id, client_id, uuid);
                }
            }
        }
        return;
    }

    // --- Persistence: write key/value ---
    if ctrl == CTRL_PERSIST_SET {
        handle_persist_set(room, client, client_id, data, room_id, db).await;
        return;
    }

    // --- Persistence: read key ---
    if ctrl == CTRL_PERSIST_GET {
        handle_persist_get(room, client, client_id, data, room_id, db).await;
        return;
    }

    // Remaining messages need at least 6 bytes (obj_id + sender_id)
    if data.len() < 6 {
        return;
    }
    let obj_id    = u16::from_le_bytes([data[2], data[3]]);
    let sender_id = u16::from_le_bytes([data[4], data[5]]);

    match ctrl {
        CTRL_CLAIM => {
            let physics_world_opt = {
                let mut room = room.write().await;
                room.claimed_objects.insert(obj_id, sender_id);
                debug!("[Room:{}] Client_{} claimed object {}", room_id, sender_id, obj_id);
                broadcast_except(&room.clients, client_id, data);
                room.physics_world.clone()
            };

            // In physics mode: make the body kinematic
            if let Some(pw_arc) = physics_world_opt {
                let mut pw = pw_arc.lock().await;
                pw.claim(obj_id);
            }
        }
        CTRL_RELEASE => {
            let physics_world_opt = {
                let mut room = room.write().await;
                room.claimed_objects.remove(&obj_id);
                debug!("[Room:{}] Client_{} released object {}", room_id, sender_id, obj_id);
                broadcast_except(&room.clients, client_id, data);
                room.physics_world.clone()
            };

            // In physics mode: restore body to dynamic with last known velocity
            if let Some(pw_arc) = physics_world_opt {
                let mut pw = pw_arc.lock().await;
                pw.release(obj_id);
            }
        }
        _ => {}
    }
}

/// Handle CTRL_PERSIST_SET — write a value to the database.
async fn handle_persist_set(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    let mut offset = 2usize;
    if data.len() < offset + 1 { return; }

    const MAX_NS_LEN: usize    = 64;
    const MAX_KEY_LEN: usize   = 128;
    const MAX_VALUE_LEN: usize = 4096;

    let ns_len = data[offset] as usize; offset += 1;
    if ns_len > MAX_NS_LEN { return; }
    if data.len() < offset + ns_len { return; }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 { return; }
    let key_len = data[offset] as usize; offset += 1;
    if key_len > MAX_KEY_LEN { return; }
    if data.len() < offset + key_len { return; }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };
    offset += key_len;

    if data.len() < offset + 2 { return; }
    let val_len = u16::from_le_bytes([data[offset], data[offset + 1]]) as usize; offset += 2;
    if val_len > MAX_VALUE_LEN {
        warn!("[Room:{}] Persist SET from client_{} exceeded max value size ({} bytes)", room_id, client_id, val_len);
        send(client, ctrl_msg(CTRL_ERROR, b"persist_value_too_large")).await;
        return;
    }
    if data.len() < offset + val_len { return; }
    let value = match std::str::from_utf8(&data[offset..offset + val_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    if let Some(pid) = player_id {
        debug!("[Room:{}] Persist SET player='{}' ns='{}' key='{}'", room_id, pid, namespace, key);
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.set(&pid, &namespace, &key, &value)).await.ok();
    } else {
        warn!("[Room:{}] Persist SET from client_{} with no registered player ID", room_id, client_id);
    }
}

/// Handle CTRL_PERSIST_GET — read a value and send it back to the requesting client.
async fn handle_persist_get(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    let mut offset = 2usize;
    if data.len() < offset + 2 { return; }
    let req_id = u16::from_le_bytes([data[offset], data[offset + 1]]); offset += 2;

    const MAX_NS_LEN: usize  = 64;
    const MAX_KEY_LEN: usize = 128;

    if data.len() < offset + 1 { return; }
    let ns_len = data[offset] as usize; offset += 1;
    if ns_len > MAX_NS_LEN { return; }
    if data.len() < offset + ns_len { return; }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 { return; }
    let key_len = data[offset] as usize; offset += 1;
    if key_len > MAX_KEY_LEN { return; }
    if data.len() < offset + key_len { return; }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    let value = if let Some(pid) = player_id {
        debug!("[Room:{}] Persist GET player='{}' ns='{}' key='{}'", room_id, pid, namespace, key);
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.get(&pid, &namespace, &key))
            .await
            .unwrap_or(None)
    } else {
        warn!("[Room:{}] Persist GET from client_{} with no registered player ID", room_id, client_id);
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

/// Handle client disconnect — cleanup and promote new master if needed (relay mode only).
async fn handle_disconnect(room: &Arc<RwLock<Room>>, client_id: u16, room_id: &str) {
    let (physics_mode, promotion): (bool, Option<(u16, Arc<crate::client::Client>)>) = {
        let mut room_guard = room.write().await;
        let physics_mode = room_guard.physics_mode;

        // Remove player position tracking
        room_guard.player_positions.remove(&client_id);

        let new_master_id = if physics_mode {
            // In physics mode: server is always the authority — just remove the client
            room_guard.remove_client(client_id);
            None
        } else {
            room_guard.remove_client(client_id)
        };

        info!(
            "[Room:{}] Client_{} disconnected ({} remaining)",
            room_id, client_id, room_guard.client_count()
        );

        let promotion = new_master_id.and_then(|id| {
            room_guard.clients.get(&id).map(|c| (id, Arc::clone(c)))
        });

        (physics_mode, promotion)
    };

    // Relay mode only: promote new master
    if !physics_mode {
        if let Some((new_master_id, new_master_client)) = promotion {
            info!("[Room:{}] Promoting client_{} to master", room_id, new_master_id);
            send(
                &new_master_client,
                ctrl_msg(CTRL_MASTER_CHANGED, &new_master_id.to_le_bytes()),
            )
            .await;
        }
    }
}

// ─── Inline binary helpers used in handler ────────────────────────────────────

#[inline]
fn read_f16(data: &[u8]) -> f32 {
    use half::f16;
    let bits = u16::from_le_bytes([data[0], data[1]]);
    f16::from_bits(bits).to_f32()
}

#[inline]
fn read_rot_u16(data: &[u8]) -> f32 {
    let raw = u16::from_le_bytes([data[0], data[1]]);
    raw as f32 * (360.0 / 65535.0)
}

#[inline]
fn push_u16_vec(buf: &mut Vec<u8>, v: u16) {
    buf.push((v & 0xFF) as u8);
    buf.push((v >> 8) as u8);
}

#[inline]
fn push_f16_vec(buf: &mut Vec<u8>, v: f32) {
    use half::f16;
    let bits = f16::from_f32(v).to_bits();
    buf.push((bits & 0xFF) as u8);
    buf.push((bits >> 8) as u8);
}

#[inline]
fn push_rot_u16_vec(buf: &mut Vec<u8>, degrees: f32) {
    let mut d = degrees % 360.0;
    if d < 0.0 { d += 360.0; }
    let encoded = (d * (65535.0 / 360.0)) as u16;
    push_u16_vec(buf, encoded);
}
