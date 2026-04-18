use log::{debug, error, info, warn};
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::RwLock;
use tokio::time::Duration;
use tokio_tungstenite::tungstenite::Message;

use crate::client::{broadcast_except, new_client, send, Client, ClientMsg};
use crate::persistence::PersistenceDb;
use crate::protocol::{
    ctrl_msg, read_f32, read_i16, read_u16, read_u64, CTRL_CLAIM, CTRL_CLAIM_REJECTED,
    CTRL_DESPAWN, CTRL_PERSIST_GET, CTRL_PERSIST_SET, CTRL_PHYSICS_READY, CTRL_PLAYER_IDENT,
    CTRL_RELEASE, CTRL_SPAWN_REQUEST, CTRL_TIME_PONG, CTRL_WELCOME, MSG_ANIMATOR, MSG_EVENT,
    MSG_INTERACTION_UPDATE, MSG_PLAYER_POS, MSG_PUSH_IMPULSE, MSG_SCALED_INTERACTION,
    MSG_SCENE_INIT, MSG_TIME_PING, MSG_VARIABLE_UPDATE,
};
use crate::room::Room;
use crate::room::RoomMap;
use crate::stats;

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
    stats::bump_client_connected();

    // Register client and decide whether this is the one that bootstraps physics.
    let (client_id, needs_scene_init, physics_already_active, scene_ready_ms) = {
        let mut r = room.write().await;
        let id = r.add_client(client.clone());
        let physics_active = r.physics_ready;
        // Only ask for SCENE_INIT if no one else has sent one yet.
        let needs_init = !physics_active;
        info!(
            "[Room:{}] Client {} connected: client_{} ({} total)",
            room_id, addr, id, r.client_count()
        );
        (id, needs_init, physics_active, r.scene_ready_ms)
    };
    stats::register_client(client_id, addr, &room_id);

    // Send welcome: [u16 client_id][u8 needs_scene_init][u64 unix_millis][u64 scene_ready_ms]
    //   unix_millis    — server's current unix time, used to seed the client's
    //                    NTP-style ServerTime offset.
    //   scene_ready_ms — unix time when physics first went live in this room.
    //                    Shared across all clients so deterministic subsystems
    //                    (NetworkNPC wander, NetworkWaypoint) anchor to the
    //                    same scene-wide timestamp. 0 if physics not yet ready
    //                    — this client will receive the value with the later
    //                    CTRL_PHYSICS_READY broadcast.
    {
        let unix_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0);
        let mut payload = Vec::with_capacity(19);
        payload.extend_from_slice(&client_id.to_le_bytes());
        payload.push(if needs_scene_init { 1 } else { 0 });
        payload.extend_from_slice(&unix_ms.to_le_bytes());
        payload.extend_from_slice(&scene_ready_ms.to_le_bytes());
        send(&client, ctrl_msg(CTRL_WELCOME, &payload)).await;
    }

    // If physics is already live, tell this client immediately so it stops any
    // local simulation and waits for server broadcasts. The payload carries
    // scene_ready_ms so late joiners get the shared anchor even if they missed
    // the original broadcast.
    //
    // We also replay the per-object authoritative state every currently-
    // connected client has already been told about — last CTRL_RELEASE per
    // object (so NetworkNPC wander anchors match), plus currently-active
    // claims (so the new client's IsClaimedByRemote lines up and their
    // NetworkNPC doesn't immediately try to chase an NPC someone else
    // already holds).
    //
    // Without this, a late joiner computes NPC wander from scene_ready_ms
    // while existing clients are anchored to a chase-release that happened
    // minutes ago — NPCs appear at visibly different positions until the
    // next chase/release cycle re-syncs them. Exactly the "friend joined
    // and saw NPCs far from me, they teleported when I walked over" bug
    // reported in the wild.
    if physics_already_active {
        send(
            &client,
            ctrl_msg(CTRL_PHYSICS_READY, &scene_ready_ms.to_le_bytes()),
        )
        .await;

        // Snapshot both maps under a single read lock, then drop the lock
        // before sending (sends await the mpsc and can race with the next
        // broadcast harmlessly — mpsc preserves order per-client).
        let (release_replays, active_claims) = {
            let r = room.read().await;
            (
                r.last_release_bytes.values().cloned().collect::<Vec<_>>(),
                r.claimed_objects
                    .iter()
                    .map(|(&obj_id, &owner)| (obj_id, owner))
                    .collect::<Vec<_>>(),
            )
        };
        for bytes in release_replays {
            send(&client, bytes).await;
        }
        for (obj_id, owner_id) in active_claims {
            let mut claim_msg = Vec::with_capacity(6);
            claim_msg.push(0xFF);
            claim_msg.push(CTRL_CLAIM);
            claim_msg.extend_from_slice(&obj_id.to_le_bytes());
            claim_msg.extend_from_slice(&owner_id.to_le_bytes());
            send(&client, claim_msg).await;
        }
    }

    const IDLE_TIMEOUT_SECS: u64 = 60;
    const POLL_INTERVAL: Duration = Duration::from_secs(2);

    // The previous implementation only checked the WebSocket receive side,
    // which can block for the full idle timeout when a peer half-closes the
    // connection (crash, network pause, sleep). If the send-side task dies
    // first — because `sink.send` errored on a dead TCP — the send
    // channel's receiver is dropped and `client.tx.is_closed()` flips to
    // true. A short poll branch detects that within `POLL_INTERVAL` and we
    // can tear down the handler immediately, stopping the flood of dropped
    // broadcasts to a zombie client.
    let mut seconds_idle: u64 = 0;

    loop {
        tokio::select! {
            recv_result = futures_util::StreamExt::next(&mut rx) => {
                seconds_idle = 0;
                let msg = match recv_result {
                    Some(Ok(m)) => m,
                    Some(Err(e)) => {
                        warn!("[Room:{}] Error from client_{}: {}", room_id, client_id, e);
                        break;
                    }
                    None => break,
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
            _ = tokio::time::sleep(POLL_INTERVAL) => {
                if client.tx.is_closed() {
                    info!(
                        "[Room:{}] client_{} send channel closed (peer dead), disconnecting",
                        room_id, client_id
                    );
                    break;
                }
                seconds_idle = seconds_idle.saturating_add(POLL_INTERVAL.as_secs());
                if seconds_idle >= IDLE_TIMEOUT_SECS {
                    warn!(
                        "[Room:{}] client_{} idle for >{}s, disconnecting",
                        room_id, client_id, IDLE_TIMEOUT_SECS
                    );
                    break;
                }
            }
        }
    }

    handle_disconnect(&room, client_id, &room_id).await;
    stats::bump_client_disconnected();
    stats::unregister_client(client_id);

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

/// Maximum inbound WebSocket frame the server will process. Frames larger
/// than this are a protocol error — the widest legitimate message is a
/// SCENE_INIT with a few thousand bodies (≈120 KB), so 256 KB leaves headroom
/// without letting a misbehaving or malicious client amplify MSG_VARIABLE_UPDATE
/// / MSG_EVENT / MSG_ANIMATOR relays into a DoS against every other peer.
const MAX_FRAME_BYTES: usize = 256 * 1024;

/// Handle a binary message from a client.
async fn handle_binary(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    if data.len() > MAX_FRAME_BYTES {
        warn!(
            "[Room:{}] client_{} sent oversized frame ({} bytes > {}); dropping",
            room_id, client_id, data.len(), MAX_FRAME_BYTES
        );
        return;
    }

    stats::bump_rx(data.len());
    stats::bump_rx_type(data);
    stats::bump_client_rx(client_id, data.len());
    let msg_type = data[0];

    match msg_type {
        MSG_SCENE_INIT => {
            crate::scene::handle_scene_init(room, &room_id.to_string(), data).await;
        }

        MSG_PLAYER_POS => {
            // [0x0C][f32 x][f32 y][f32 z][f32 radius][f32 half_height] = 21 bytes
            if data.len() < 21 {
                return;
            }
            let x = read_f32(data, 1);
            let y = read_f32(data, 5);
            let z = read_f32(data, 9);
            let radius = read_f32(data, 13);
            let half_height = read_f32(data, 17);

            // Update AOI position map.
            {
                let mut r = room.write().await;
                r.player_positions.insert(client_id, [x, y, z]);
            }

            // Update (or create) the player's kinematic capsule so Rapier
            // collisions treat players as solid bodies in the world.
            let pw_opt = room.read().await.physics_world.clone();
            if let Some(pw_arc) = pw_opt {
                let mut pw = pw_arc.lock().await;
                pw.update_player_body(client_id, [x, y, z], radius, half_height);
            }
        }

        MSG_TIME_PING => {
            // [0x0E][u64 client_local_ms] = 9 bytes
            // Echo the client's timestamp back alongside the server's current
            // unix_ms so the client can compute RTT and a better ServerTime
            // offset than the single one-way estimate we gave at welcome.
            if data.len() < 9 {
                return;
            }
            let client_ms = read_u64(data, 1);
            let server_ms = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_millis() as u64)
                .unwrap_or(0);
            let mut payload = Vec::with_capacity(16);
            payload.extend_from_slice(&client_ms.to_le_bytes());
            payload.extend_from_slice(&server_ms.to_le_bytes());
            send(client, ctrl_msg(CTRL_TIME_PONG, &payload)).await;
        }

        MSG_PUSH_IMPULSE => {
            // [0x0D][u16 obj_id][f32 vx][f32 vy][f32 vz] = 15 bytes
            if data.len() < 15 {
                return;
            }
            let obj_id = read_u16(data, 1);
            let vx = read_f32(data, 3);
            let vy = read_f32(data, 7);
            let vz = read_f32(data, 11);

            // Rate-limit sanity cap: 50 m/s is bigger than most intentional
            // pushes and protects against runaway impulses if a client goes wrong.
            const MAX_PUSH_SPEED: f32 = 50.0;
            let speed_sq = vx * vx + vy * vy + vz * vz;
            if speed_sq > MAX_PUSH_SPEED * MAX_PUSH_SPEED {
                return;
            }

            let pw_opt = room.read().await.physics_world.clone();
            if let Some(pw_arc) = pw_opt {
                let mut pw = pw_arc.lock().await;
                pw.apply_push_impulse(obj_id, [vx, vy, vz]);
            }
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
    let count = read_u16(data, 1) as usize;
    if data.len() < 3 + count * bytes_per_entity {
        return;
    }

    // We snapshot (clone) `claimed_objects` instead of holding the room read
    // lock across the physics mutex + broadcast below. MSG_PLAYER_POS acquires
    // a room WRITE lock every frame per client (see handler::handle_binary ->
    // MSG_PLAYER_POS), so holding a read lock here would serialize the entire
    // write path against held-object updates. The map is small (only
    // actively-claimed objects, typically <20 entries), so the clone is a
    // sub-microsecond copy — much cheaper than the contention it avoids.
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
        let obj_id = read_u16(data, offset);
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
            pw.update_claimed(obj_id, px, py, pz, qx, qy, qz, qw);
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

    // Persistence (delegated to persist_rpc module)
    if ctrl == CTRL_PLAYER_IDENT {
        crate::persist_rpc::handle_player_ident(room, client_id, data, room_id).await;
        return;
    }
    if ctrl == CTRL_PERSIST_SET {
        crate::persist_rpc::handle_persist_set(room, client, client_id, data, room_id, db).await;
        return;
    }
    if ctrl == CTRL_PERSIST_GET {
        crate::persist_rpc::handle_persist_get(room, client, client_id, data, room_id, db).await;
        return;
    }

    // Runtime spawn / despawn (delegated to spawn module)
    if ctrl == CTRL_SPAWN_REQUEST {
        crate::spawn::handle_spawn_request(room, client_id, data, room_id).await;
        return;
    }
    if ctrl == CTRL_DESPAWN {
        crate::spawn::handle_despawn(room, client_id, data, room_id).await;
        return;
    }

    // Claim/release need at least 6 bytes (obj_id + sender_id).
    if data.len() < 6 {
        return;
    }
    let obj_id = read_u16(data, 2);

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
                pw.claim(obj_id, client_id);
            }
        }
        CTRL_RELEASE => {
            // Snapshot physics handle + pose under the read lock so we don't
            // hold a physics mutex across the room write lock we're about to
            // take. Ownership is RE-VERIFIED under the write lock below —
            // between these two critical sections the physics tick's stale-
            // claim sweep can auto-release the same object, and without the
            // second check we'd emit a duplicate release broadcast and
            // overwrite last_release_bytes with a stale pose.
            let physics_world_opt = {
                let room_r = room.read().await;
                if room_r.claimed_objects.get(&obj_id) != Some(&client_id) {
                    return;
                }
                room_r.physics_world.clone()
            };

            // Read the authoritative pose BEFORE release() restores the body
            // type — the pose is what every client should re-anchor to.
            let pose = if let Some(ref pw_arc) = physics_world_opt {
                let pw = pw_arc.lock().await;
                pw.read_body_pose(obj_id)
            } else {
                None
            };

            let mut room_w = room.write().await;
            // Re-check ownership — the stale-claim sweep (or a disconnect
            // handler) may have released this object while we were reading
            // the pose. If they did, they already broadcast a release and
            // populated last_release_bytes; silently bail.
            if room_w.claimed_objects.get(&obj_id) != Some(&client_id) {
                return;
            }
            room_w.claimed_objects.remove(&obj_id);
            debug!("[Room:{}] client_{} released object {}", room_id, client_id, obj_id);
            let msg = build_release_broadcast(obj_id, client_id, pose);
            // Remember the latest release per object so late joiners land on
            // the same wander anchor that currently-connected clients are
            // using — see Room::last_release_bytes.
            room_w.last_release_bytes.insert(obj_id, msg.clone());
            // Broadcast to ALL including the releaser so even the chaser
            // re-anchors from the same authoritative pose + timestamp.
            crate::client::broadcast_all(&room_w.clients, &msg);
            drop(room_w);

            if let Some(pw_arc) = physics_world_opt {
                let mut pw = pw_arc.lock().await;
                pw.release(obj_id);
            }
        }
        _ => {}
    }
}

/// Build a CTRL_RELEASE broadcast carrying the authoritative release pose +
/// the server's current unix_ms. Every recipient (including the original
/// claimer) re-anchors from these values so deterministic subcomponents
/// (NetworkNPC's wander) don't drift per-client.
///
/// Wire layout:
///   [0xFF][0x06][u16 obj_id][u16 client_id]
///   [f32 px][f32 py][f32 pz]
///   [i16 qx][i16 qy][i16 qz][i16 qw]   (quantised quaternion, /32767 to decode)
///   [u64 server_unix_ms]
///   = 34 bytes
pub(crate) fn build_release_broadcast(
    obj_id: u16,
    client_id: u16,
    pose: Option<([f32; 3], [f32; 4])>,
) -> Vec<u8> {
    let (pos, quat) = pose.unwrap_or(([0.0; 3], [0.0, 0.0, 0.0, 1.0]));
    let server_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);

    info!(
        "[Release=>] obj={} by_client={} pos=({:.3},{:.3},{:.3}) serverMs={}",
        obj_id, client_id, pos[0], pos[1], pos[2], server_ms
    );

    let mut buf = Vec::with_capacity(34);
    buf.push(0xFF);
    buf.push(CTRL_RELEASE);
    buf.extend_from_slice(&obj_id.to_le_bytes());
    buf.extend_from_slice(&client_id.to_le_bytes());
    buf.extend_from_slice(&pos[0].to_le_bytes());
    buf.extend_from_slice(&pos[1].to_le_bytes());
    buf.extend_from_slice(&pos[2].to_le_bytes());
    for c in quat.iter() {
        let q = (c.clamp(-1.0, 1.0) * 32767.0).round() as i16;
        buf.extend_from_slice(&q.to_le_bytes());
    }
    buf.extend_from_slice(&server_ms.to_le_bytes());
    buf
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
        // Read poses first so release broadcasts match authoritative state.
        let mut poses = Vec::with_capacity(released_ids.len());
        {
            let pw = pw_arc.lock().await;
            for obj_id in &released_ids {
                poses.push((*obj_id, pw.read_body_pose(*obj_id)));
            }
        }

        {
            let mut pw = pw_arc.lock().await;
            for obj_id in &released_ids {
                pw.release(*obj_id);
            }
            pw.remove_player_body(client_id);
        }

        if !released_ids.is_empty() {
            // Write lock — we need to record each release into
            // last_release_bytes for late-joiner replay.
            let mut room_w = room.write().await;
            for (obj_id, pose) in poses {
                let msg = build_release_broadcast(obj_id, client_id, pose);
                room_w.last_release_bytes.insert(obj_id, msg.clone());
                crate::client::broadcast_all(&room_w.clients, &msg);
            }
        }
    }
}
