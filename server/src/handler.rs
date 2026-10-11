use log::{debug, error, info, warn};
use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Instant;
use tokio::sync::RwLock;
use tokio::time::Duration;
use tokio_tungstenite::tungstenite::protocol::WebSocketConfig;
use tokio_tungstenite::tungstenite::Message;

use crate::client::{
    broadcast_except, broadcast_held, new_client, redact_peer, send, Client, ClientMsg,
};
use crate::persistence::PersistenceDb;
use crate::physics::build_initial_snapshot_message;
use crate::protocol::{
    ctrl_msg, read_f32, read_i16, read_u16, read_u64, CTRL_CLAIM, CTRL_CLAIM_REJECTED,
    CTRL_CLIENT_CAPS, CTRL_DESPAWN, CTRL_ERROR, CTRL_PERSIST_GET, CTRL_PERSIST_SET,
    CTRL_PHYSICS_READY, CTRL_PLAYER_IDENT, CTRL_RELEASE, CTRL_SPAWN_REQUEST, CTRL_STATE_WRITER,
    CTRL_TIME_PONG, CTRL_WELCOME, MSG_ANIMATOR, MSG_EVENT, MSG_INTERACTION_UPDATE, MSG_PLAYER_POS,
    MSG_PUSH_IMPULSE, MSG_SCALED_INTERACTION, MSG_SCENE_INIT, MSG_TIME_PING, MSG_VARIABLE_UPDATE,
};
use crate::room::Room;
use crate::room::RoomMap;
use crate::stats;
use crate::variable_state::UpdateError;
use futures_util::SinkExt;

/// Handle a single client connection (runs in its own task).
#[allow(clippy::result_large_err)] // tungstenite's handshake callback requires an HTTP response error.
pub async fn handle_connection(
    stream: tokio::net::TcpStream,
    addr: SocketAddr,
    rooms: RoomMap,
    db: PersistenceDb,
) {
    let room_id = Arc::new(tokio::sync::Mutex::new(String::from("default")));
    let room_id_clone = room_id.clone();
    let fingerprint = Arc::new(AtomicU64::new(0));
    let fingerprint_clone = fingerprint.clone();

    let callback = move |req: &tokio_tungstenite::tungstenite::handshake::server::Request,
                         resp: tokio_tungstenite::tungstenite::handshake::server::Response|
          -> Result<
        tokio_tungstenite::tungstenite::handshake::server::Response,
        tokio_tungstenite::tungstenite::http::Response<Option<String>>,
    > {
        let path = req.uri().path().trim_start_matches('/').to_string();
        if !valid_room_id(&path) {
            return Err(tokio_tungstenite::tungstenite::http::Response::builder()
                .status(400)
                .body(Some("Invalid room ID".to_string()))
                .expect("static HTTP response"));
        }
        // The query carries only a compatibility fingerprint, never a secret.
        // Reject older or malformed clients before allocating room/client state.
        let scene = req.uri().query().and_then(|q| q.strip_prefix("scene="));
        let scene = scene
            .filter(|s| s.len() == 16 && s.bytes().all(|b| b.is_ascii_hexdigit()))
            .and_then(|s| u64::from_str_radix(s, 16).ok())
            .filter(|&s| s != 0);
        let Some(scene) = scene else {
            return Err(tokio_tungstenite::tungstenite::http::Response::builder()
                .status(400)
                .body(Some("Missing or invalid scene fingerprint".to_string()))
                .expect("static HTTP response"));
        };
        fingerprint_clone.store(scene, Ordering::Relaxed);
        if !path.is_empty() {
            if let Ok(mut id) = room_id_clone.try_lock() {
                *id = path;
            }
        }
        Ok(resp)
    };

    // Enforce the protocol limit before tungstenite allocates an oversized message.
    let ws_config = WebSocketConfig {
        write_buffer_size: 0,
        max_write_buffer_size: 2 * 1024 * 1024,
        max_message_size: Some(MAX_SCENE_INIT_BYTES),
        max_frame_size: Some(MAX_SCENE_INIT_BYTES),
        ..WebSocketConfig::default()
    };
    let mut ws = match tokio::time::timeout(
        Duration::from_secs(10),
        tokio_tungstenite::accept_hdr_async_with_config(stream, callback, Some(ws_config)),
    )
    .await
    {
        Ok(Ok(ws)) => ws,
        Ok(Err(e)) => {
            error!(
                "[Server] WebSocket handshake failed for {}: {}",
                redact_peer(addr),
                e
            );
            return;
        }
        Err(_) => {
            warn!(
                "[Server] WebSocket handshake timed out for {}",
                redact_peer(addr)
            );
            return;
        }
    };

    let room_id = room_id.lock().await.clone();
    let scene_fingerprint = fingerprint.load(Ordering::Relaxed);

    let room = {
        let mut all_rooms = rooms.write().await;
        if let Some(existing) = all_rooms.get(&room_id) {
            Some(existing.clone())
        } else if all_rooms.len() >= 128 {
            None
        } else {
            let new_room = Arc::new(RwLock::new(Room::new()));
            info!("[Rooms] Created room '{}'", room_id);
            all_rooms.insert(room_id.clone(), new_room.clone());
            Some(new_room)
        }
    };
    let Some(room) = room else {
        let _ = ws.send(Message::Binary(ctrl_msg(CTRL_ERROR, &[3]))).await;
        let _ = ws.close(None).await;
        return;
    };

    {
        let mut r = room.write().await;
        if r.scene_fingerprint != 0 && r.scene_fingerprint != scene_fingerprint {
            warn!(
                "[Room:{}] Rejected client with incompatible scene fingerprint",
                room_id
            );
            drop(r);
            let _ = ws.send(Message::Binary(ctrl_msg(CTRL_ERROR, &[2]))).await;
            let _ = ws.close(None).await;
            return;
        }
        r.scene_fingerprint = scene_fingerprint;
    }

    let (ws_tx, mut rx) = futures_util::StreamExt::split(ws);
    let client = new_client(addr, ws_tx);
    // Register client and decide whether this is the one that bootstraps physics.
    let registration = {
        let mut r = room.write().await;
        if r.client_count() >= Room::MAX_CLIENTS {
            None
        } else {
            let id = r.add_client(client.clone());
            let physics_active = r.physics_ready;
            // Only ask for SCENE_INIT if no one else has sent one yet.
            let needs_init = !physics_active;
            info!(
                "[Room:{}] Client {} connected: client_{} ({} total)",
                room_id,
                redact_peer(addr),
                id,
                r.client_count()
            );
            Some((
                id,
                needs_init,
                physics_active,
                r.scene_ready_ms,
                r.state_writer_id,
            ))
        }
    };
    let Some((
        client_id,
        needs_scene_init,
        physics_already_active,
        scene_ready_ms,
        state_writer_id,
    )) = registration
    else {
        send(&client, ctrl_msg(CTRL_ERROR, &[3])).await;
        return;
    };
    stats::bump_client_connected();
    stats::register_client(client_id, addr, &room_id);

    // Send welcome: [u16 client_id][u8 needs_scene_init][u64 unix_millis]
    // [u64 scene_ready_ms][u8 persistence_available]
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
        let mut payload = Vec::with_capacity(20);
        payload.extend_from_slice(&client_id.to_le_bytes());
        payload.push(if needs_scene_init { 1 } else { 0 });
        payload.extend_from_slice(&unix_ms.to_le_bytes());
        payload.extend_from_slice(&scene_ready_ms.to_le_bytes());
        payload.push(u8::from(crate::persist_rpc::unauth_persistence_allowed()));
        send(&client, ctrl_msg(CTRL_WELCOME, &payload)).await;
        send(
            &client,
            ctrl_msg(CTRL_STATE_WRITER, &state_writer_id.to_le_bytes()),
        )
        .await;
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
        let (spawn_replays, release_replays, active_claims, physics_world_opt) = {
            let r = room.read().await;
            (
                r.runtime_spawns.values().cloned().collect::<Vec<_>>(),
                r.last_release_bytes.values().cloned().collect::<Vec<_>>(),
                r.claimed_objects
                    .iter()
                    .map(|(&obj_id, &owner)| (obj_id, owner))
                    .collect::<Vec<_>>(),
                r.physics_world.clone(),
            )
        };

        for bytes in spawn_replays {
            send(&client, bytes).await;
        }

        if let Some(pw_arc) = physics_world_opt {
            let initial_snapshot = {
                let pw = pw_arc.lock().await;
                build_initial_snapshot_message(&pw.snapshot_all_dynamic())
            };
            if let Some(bytes) = initial_snapshot {
                send(&client, bytes).await;
            }
        }

        // Hold the room read lock while enqueueing the snapshot. Variable
        // updates take the write lock, so a live update cannot overtake an
        // older cached value and then be overwritten on this client.
        {
            let r = room.read().await;
            for frame in r.variable_state.snapshot_frames() {
                send(&client, frame).await;
            }
        }

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
    let mut inbound_budget = InboundBudget::new();
    let mut scene_init_seen = false;

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
                let scene_init = matches!(&msg, Message::Binary(data) if data.first() == Some(&MSG_SCENE_INIT));
                if scene_init {
                    if scene_init_seen {
                        warn!("[Room:{}] client_{} repeated SCENE_INIT; disconnecting", room_id, client_id);
                        break;
                    }
                    scene_init_seen = true;
                }
                let event = matches!(&msg, Message::Binary(data) if data.first() == Some(&MSG_EVENT));
                if !inbound_budget.allow(if scene_init { 0 } else { msg.len() }, event) {
                    warn!("[Room:{}] client_{} exceeded inbound traffic budget; disconnecting", room_id, client_id);
                    break;
                }
                match msg {
                    Message::Binary(data) => {
                        if data.is_empty() {
                            continue;
                        }
                        handle_binary(&room, &client, client_id, &data, &room_id, &db).await;
                    }
                    Message::Close(_) => break,
                    Message::Ping(d) => {
                        let _ = client.tx.send(ClientMsg::Pong(d)).await;
                    }
                    _ => {}
                }
            }
            _ = tokio::time::sleep(POLL_INTERVAL) => {
                if client.is_failed() {
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
    stats::unregister_client(&room_id, client_id);

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

/// Relayed messages stay small. Only the one-time scene description may be
/// larger, allowing up to 16,384 bodies without allowing large event relays.
const MAX_FRAME_BYTES: usize = 256 * 1024;
const MAX_SCENE_INIT_BYTES: usize = 3 + 16_384 * 60;
const MAX_ABS_POSITION: f32 = 100_000.0;
const MAX_PLAYER_DIMENSION: f32 = 10.0;

/// Limits one peer's inbound work without limiting room-wide physics output.
/// A one-time SCENE_INIT is accounted for by its separate frame-size cap.
struct InboundBudget {
    bytes: f64,
    messages: f64,
    event_bytes: f64,
    events: f64,
    last: Instant,
}

impl InboundBudget {
    const BYTE_BURST: f64 = (4 * 1024 * 1024) as f64;
    const BYTES_PER_SEC: f64 = (2 * 1024 * 1024) as f64;
    const MESSAGE_BURST: f64 = 400.0;
    const MESSAGES_PER_SEC: f64 = 200.0;
    const EVENT_BYTE_BURST: f64 = (128 * 1024) as f64;
    const EVENT_BYTES_PER_SEC: f64 = (64 * 1024) as f64;
    const EVENT_BURST: f64 = 100.0;
    const EVENTS_PER_SEC: f64 = 30.0;

    fn new() -> Self {
        Self {
            bytes: Self::BYTE_BURST,
            messages: Self::MESSAGE_BURST,
            event_bytes: Self::EVENT_BYTE_BURST,
            events: Self::EVENT_BURST,
            last: Instant::now(),
        }
    }

    fn allow(&mut self, size: usize, event: bool) -> bool {
        self.allow_at(size, event, Instant::now())
    }

    fn allow_at(&mut self, size: usize, event: bool, now: Instant) -> bool {
        let elapsed = now.saturating_duration_since(self.last).as_secs_f64();
        self.last = now;
        self.bytes = (self.bytes + elapsed * Self::BYTES_PER_SEC).min(Self::BYTE_BURST);
        self.messages = (self.messages + elapsed * Self::MESSAGES_PER_SEC).min(Self::MESSAGE_BURST);
        self.event_bytes =
            (self.event_bytes + elapsed * Self::EVENT_BYTES_PER_SEC).min(Self::EVENT_BYTE_BURST);
        self.events = (self.events + elapsed * Self::EVENTS_PER_SEC).min(Self::EVENT_BURST);
        if self.bytes < size as f64
            || self.messages < 1.0
            || (event && (self.event_bytes < size as f64 || self.events < 1.0))
        {
            return false;
        }
        self.bytes -= size as f64;
        self.messages -= 1.0;
        if event {
            self.event_bytes -= size as f64;
            self.events -= 1.0;
        }
        true
    }
}

#[cfg(test)]
mod inbound_budget_tests {
    use super::InboundBudget;
    use std::time::Duration;

    #[test]
    fn traffic_burst_is_bounded_and_recovers() {
        let mut b = InboundBudget::new();
        let start = b.last;
        for _ in 0..400 {
            assert!(b.allow_at(0, false, start));
        }
        assert!(!b.allow_at(0, false, start));
        assert!(b.allow_at(0, false, start + Duration::from_secs(1)));
        assert!(!b.allow_at(4 * 1024 * 1024 + 1, false, start + Duration::from_secs(1)));
    }

    #[test]
    fn event_fanout_has_a_separate_budget() {
        let mut b = InboundBudget::new();
        let start = b.last;
        for _ in 0..100 {
            assert!(b.allow_at(16, true, start));
        }
        assert!(!b.allow_at(16, true, start));
        assert!(b.allow_at(16, true, start + Duration::from_secs(1)));
    }
}

fn valid_room_id(id: &str) -> bool {
    id.len() <= 64
        && id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-' | b'.'))
}

#[cfg(test)]
mod room_id_tests {
    use super::valid_room_id;

    #[test]
    fn room_names_are_bounded_and_path_safe() {
        assert!(valid_room_id(""));
        assert!(valid_room_id("my-world_2.0"));
        assert!(!valid_room_id("a/b"));
        assert!(!valid_room_id("a%2Fb"));
        assert!(!valid_room_id(&"a".repeat(65)));
    }
}

#[inline]
fn finite_abs_le(v: f32, max_abs: f32) -> bool {
    v.is_finite() && v.abs() <= max_abs
}

#[inline]
fn valid_position3(x: f32, y: f32, z: f32) -> bool {
    finite_abs_le(x, MAX_ABS_POSITION)
        && finite_abs_le(y, MAX_ABS_POSITION)
        && finite_abs_le(z, MAX_ABS_POSITION)
}

#[inline]
fn valid_unit_quat(qx: f32, qy: f32, qz: f32, qw: f32) -> bool {
    qx.is_finite()
        && qy.is_finite()
        && qz.is_finite()
        && qw.is_finite()
        && (qx * qx + qy * qy + qz * qz + qw * qw) > 1e-6
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
    let Some(&msg_type) = data.first() else {
        return;
    };
    let max_bytes = if msg_type == MSG_SCENE_INIT {
        MAX_SCENE_INIT_BYTES
    } else {
        MAX_FRAME_BYTES
    };
    if data.len() > max_bytes {
        warn!(
            "[Room:{}] client_{} sent oversized frame ({} bytes > {}); dropping",
            room_id,
            client_id,
            data.len(),
            max_bytes
        );
        return;
    }

    stats::bump_rx(data.len());
    stats::bump_rx_type(data);
    stats::bump_client_rx(room_id, client_id, data.len());
    match msg_type {
        MSG_SCENE_INIT => {
            crate::scene::handle_scene_init(room, room_id, data).await;
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

            if !valid_position3(x, y, z)
                || !radius.is_finite()
                || !half_height.is_finite()
                || radius <= 0.0
                || half_height <= 0.0
                || radius > MAX_PLAYER_DIMENSION
                || half_height > MAX_PLAYER_DIMENSION
            {
                warn!(
                    "[Room:{}] client_{} sent invalid player position/capsule",
                    room_id, client_id
                );
                return;
            }

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

            if !vx.is_finite() || !vy.is_finite() || !vz.is_finite() {
                return;
            }

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

        MSG_VARIABLE_UPDATE => {
            if data.len() < 4 {
                return;
            }
            let obj_id = read_u16(data, 1);
            let mut r = room.write().await;
            if r.claimed_objects
                .get(&obj_id)
                .is_some_and(|&owner| owner != client_id)
            {
                return;
            }
            if !r.claimed_objects.contains_key(&obj_id) && r.state_writer_id != client_id {
                return;
            }
            match r.variable_state.apply(data) {
                Ok(()) => broadcast_except(&r.clients, client_id, data),
                Err(UpdateError::CacheFull) => {
                    drop(r);
                    send(client, ctrl_msg(CTRL_ERROR, &[1])).await;
                }
                Err(UpdateError::Malformed) => {
                    warn!(
                        "[Room:{}] client_{} sent malformed variable update",
                        room_id, client_id
                    );
                }
            }
        }

        MSG_ANIMATOR => {
            if data.len() < 3 {
                return;
            }
            let obj_id = read_u16(data, 1);
            let room = room.read().await;
            if let Some(&owner) = room.claimed_objects.get(&obj_id) {
                if owner != client_id {
                    debug!(
                        "[Room:{}] client_{} tried to relay state for object {} owned by client_{}",
                        room_id, client_id, obj_id, owner
                    );
                    return;
                }
            } else if room.state_writer_id != client_id {
                return;
            }
            broadcast_except(&room.clients, client_id, data);
        }

        MSG_EVENT => {
            if !valid_event(data) {
                return;
            }
            // Events can be addressed to an object's authority by another
            // client, so ownership is not a prerequisite for sending one.
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

/// Validate the complete event envelope before broadcasting it to Unity.
/// Malformed payloads otherwise reach every recipient and can allocate large
/// strings/parameter arrays even when no listener handles the event.
fn valid_event(data: &[u8]) -> bool {
    if data.len() < 6 || data.len() > 4096 || data[0] != MSG_EVENT || data[3] > 2 {
        return false;
    }
    let name_len = data[4] as usize;
    if name_len == 0 || name_len > 64 || 5 + name_len >= data.len() {
        return false;
    }
    if std::str::from_utf8(&data[5..5 + name_len]).is_err() {
        return false;
    }
    let mut offset = 5 + name_len;
    let count = data[offset] as usize;
    if count > 16 {
        return false;
    }
    offset += 1;
    for _ in 0..count {
        if offset + 3 > data.len() {
            return false;
        }
        let kind = data[offset];
        let len = read_u16(data, offset + 1) as usize;
        offset += 3;
        if offset + len > data.len() {
            return false;
        }
        match kind {
            0 if len == 4 => {}
            1 if len == 4 && read_f32(data, offset).is_finite() => {}
            2 if len == 1 && data[offset] <= 1 => {}
            3 if len <= 1024 && std::str::from_utf8(&data[offset..offset + len]).is_ok() => {}
            _ => return false,
        }
        offset += len;
    }
    offset == data.len()
}

#[cfg(test)]
mod event_tests {
    use super::valid_event;

    #[test]
    fn rejects_malformed_events_before_fanout() {
        let valid = b"\x06\x01\x00\x00\x04ping\x01\x00\x04\x00\x05\x00\x00\x00";
        assert!(valid_event(valid));
        assert!(!valid_event(&valid[..valid.len() - 1]));
        let mut wrong_type = valid.to_vec();
        wrong_type[10] = 9;
        assert!(!valid_event(&wrong_type));
        let mut bad_target = valid.to_vec();
        bad_target[3] = 3;
        assert!(!valid_event(&bad_target));
        let mut extra = valid.to_vec();
        extra.push(0);
        assert!(!valid_event(&extra));
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

    // Hold the room read lock until relay. A release or despawn needs the
    // write lock, so it cannot overtake a validated held-object update.
    let room_r = room.read().await;
    let pw_opt = room_r.physics_world.clone();

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
        if room_r.claimed_objects.get(&obj_id) != Some(&client_id) {
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
            if !valid_position3(px, py, pz) || !valid_unit_quat(qx, qy, qz, qw) {
                offset += bytes_per_entity;
                continue;
            }
            if !pw.update_claimed(obj_id, client_id, px, py, pz, qx, qy, qz, qw) {
                offset += bytes_per_entity;
                continue;
            }
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
    broadcast_held(&room_r.clients, client_id, &relay);
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

    if ctrl == CTRL_CLIENT_CAPS {
        if data.len() == 3 && data[2] & 1 != 0 {
            client.enable_compact_positions();
        }
        return;
    }

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
            let rejected = {
                let mut r = room.write().await;
                if !r.physics_ready {
                    true
                } else if let Some(&owner) = r.claimed_objects.get(&obj_id) {
                    owner != client_id
                } else if let Some(pw_arc) = r.physics_world.clone() {
                    let mut pw = pw_arc.lock().await;
                    if !pw.claim(obj_id, client_id) {
                        true
                    } else {
                        r.claimed_objects.insert(obj_id, client_id);
                        r.state_epoch = r.state_epoch.wrapping_add(1);
                        let mut relay = vec![0xFF, CTRL_CLAIM];
                        relay.extend_from_slice(&obj_id.to_le_bytes());
                        relay.extend_from_slice(&client_id.to_le_bytes());
                        broadcast_except(&r.clients, client_id, &relay);
                        debug!(
                            "[Room:{}] client_{} claimed object {}",
                            room_id, client_id, obj_id
                        );
                        false
                    }
                } else {
                    true
                }
            };
            if rejected {
                send(client, ctrl_msg(CTRL_CLAIM_REJECTED, &obj_id.to_le_bytes())).await;
            }
        }
        CTRL_RELEASE => {
            let mut r = room.write().await;
            if r.claimed_objects.get(&obj_id) != Some(&client_id) {
                return;
            }
            let pose = if let Some(pw_arc) = r.physics_world.clone() {
                let mut pw = pw_arc.lock().await;
                let pose = pw.read_body_pose(obj_id);
                pw.release(obj_id);
                pose
            } else {
                None
            };
            r.claimed_objects.remove(&obj_id);
            r.state_epoch = r.state_epoch.wrapping_add(1);
            let msg = build_release_broadcast(obj_id, client_id, pose);
            r.last_release_bytes.insert(obj_id, msg.clone());
            crate::client::broadcast_all(&r.clients, &msg);
            debug!(
                "[Room:{}] client_{} released object {}",
                room_id, client_id, obj_id
            );
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
    let mut room_guard = room.write().await;
    let prior_writer = room_guard.state_writer_id;
    let released_ids = room_guard.remove_client(client_id);
    if room_guard.state_writer_id != prior_writer {
        let writer = ctrl_msg(CTRL_STATE_WRITER, &room_guard.state_writer_id.to_le_bytes());
        crate::client::broadcast_all(&room_guard.clients, &writer);
    }
    info!(
        "[Room:{}] client_{} disconnected ({} remaining, {} objects released)",
        room_id,
        client_id,
        room_guard.client_count(),
        released_ids.len()
    );

    if let Some(pw_arc) = room_guard.physics_world.clone() {
        let mut pw = pw_arc.lock().await;
        let mut poses = Vec::with_capacity(released_ids.len());
        for obj_id in &released_ids {
            poses.push((*obj_id, pw.read_body_pose(*obj_id)));
            pw.release(*obj_id);
        }
        pw.remove_player_body(client_id);
        drop(pw);

        for (obj_id, pose) in poses {
            let msg = build_release_broadcast(obj_id, client_id, pose);
            room_guard.last_release_bytes.insert(obj_id, msg.clone());
            crate::client::broadcast_all(&room_guard.clients, &msg);
        }
    }
}
