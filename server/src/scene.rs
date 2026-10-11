//! Scene bootstrap + the per-room authoritative physics tick task.
//!
//! Split out of handler.rs so the connection-lifecycle code stays focused on
//! the per-client message loop. The scene-init handler and the tick task are
//! a single logical unit — the handler spawns the task — so they live in one
//! file.

use std::sync::Arc;

use log::info;
use log::{debug, warn};
use tokio::sync::RwLock;
use tokio::time::{Duration, Instant};

use crate::client::{broadcast_all, send_try, Client};
use crate::handler::build_release_broadcast;
use crate::physics::{build_filtered_message, parse_scene_init, PhysicsWorld};
use crate::protocol::{ctrl_msg, CTRL_PHYSICS_READY};
use crate::room::Room;

/// Handle a SCENE_INIT (0x10) — bootstrap the authoritative physics world.
pub(crate) async fn handle_scene_init(room: &Arc<RwLock<Room>>, room_id: &str, data: &[u8]) {
    {
        let mut r = room.write().await;
        if r.physics_ready || r.scene_initializing {
            debug!(
                "[Room:{}] SCENE_INIT ignored (physics already ready or initializing)",
                room_id
            );
            return;
        }
        r.scene_initializing = true;
    }

    let objects = match parse_scene_init(data) {
        Some(objs) => objs,
        None => {
            warn!("[Room:{}] Failed to parse SCENE_INIT", room_id);
            room.write().await.scene_initializing = false;
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

    // Mark physics ready and broadcast to every connected client. Stamp the
    // room-wide scene_ready_ms here — it's the single canonical "physics went
    // live at T" value every client anchors deterministic subsystems to. The
    // CTRL_PHYSICS_READY broadcast carries it so clients that connected before
    // SCENE_INIT landed learn the value now.
    let initial_object_count = objects.len() as u16;
    let scene_ready_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);
    {
        let mut r = room.write().await;
        if r.physics_ready {
            r.scene_initializing = false;
            debug!(
                "[Room:{}] SCENE_INIT ignored after build (physics already ready)",
                room_id
            );
            return;
        }
        r.physics_ready = true;
        r.scene_initializing = false;
        r.physics_world = Some(pw_arc.clone());
        r.scene_object_count = initial_object_count;
        // Runtime spawns get IDs starting right after the baked-in scene objects.
        r.next_runtime_id = initial_object_count;
        r.scene_ready_ms = scene_ready_ms;
        let ready_msg = ctrl_msg(CTRL_PHYSICS_READY, &scene_ready_ms.to_le_bytes());
        broadcast_all(&r.clients, &ready_msg);
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
    /// If a claimed body hasn't received an update in this long, assume the
    /// grabbing client's socket stalled and auto-release so other clients can
    /// interact again. TCP idle-kick is 60s but that's way too long to leave
    /// grabbed objects frozen in-world.
    const STALE_CLAIM_TIMEOUT: Duration = Duration::from_secs(5);
    const STALE_CLAIM_CHECK_INTERVAL: Duration = Duration::from_secs(2);

    let mut last_physics = Instant::now();
    let mut last_broadcast = Instant::now();
    let mut last_room_check = Instant::now();
    let mut last_claim_check = Instant::now();
    let mut server_sequence: u16 = 0;
    let mut tick: u32 = 0;

    info!(
        "[Room:{}] Physics tick task started (50Hz sim / 20Hz broadcast)",
        room_id
    );

    loop {
        let now = Instant::now();
        let next_physics = last_physics + PHYSICS_INTERVAL;
        let next_broadcast = last_broadcast + BROADCAST_INTERVAL;
        let next_check = last_room_check + ROOM_CHECK_INTERVAL;
        let next_claim_check = last_claim_check + STALE_CLAIM_CHECK_INTERVAL;

        let wake_at = next_physics
            .min(next_broadcast)
            .min(next_check)
            .min(next_claim_check);
        if wake_at > now {
            tokio::time::sleep_until(wake_at).await;
        }

        let now = Instant::now();
        let do_physics = now >= next_physics;
        let do_broadcast = now >= next_broadcast;

        // Single physics-mutex acquisition per tick: step + snapshot for broadcast.
        let buckets_opt = if do_physics || do_broadcast {
            let mut pw = pw_arc.lock().await;
            if do_physics {
                last_physics = now;
                let t0 = Instant::now();
                pw.step();
                let us = t0.elapsed().as_micros() as u64;
                if let Some(s) = crate::stats::get() {
                    s.record_physics_tick(us);
                }
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
            let buckets = match buckets_opt {
                Some(s) => s,
                None => continue,
            };

            server_sequence = server_sequence.wrapping_add(1);
            let seq = server_sequence;
            tick = tick.wrapping_add(1);

            let room_arc = match room_weak.upgrade() {
                Some(r) => r,
                None => {
                    info!(
                        "[Room:{}] Physics task: room deallocated, stopping",
                        room_id
                    );
                    return;
                }
            };

            // Snapshot clients + player positions while briefly holding the
            // read lock. We intentionally clone rather than hold the lock
            // across the broadcast loop below: MSG_PLAYER_POS grabs a write
            // lock every frame per client, and build_filtered_message per
            // recipient takes real CPU — holding a reader would serialize all
            // of that. Both maps are small (≤64 clients), so the clones are
            // cheaper than the contention they avoid.
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

            let bc_start = Instant::now();
            for (cid, client) in &clients_clone {
                let pp = player_pos_clone.get(cid).copied();
                if let Some(msg) =
                    build_filtered_message(&buckets.awake, &buckets.just_slept, pp, tick, seq)
                {
                    send_try(client, bytes::Bytes::from(msg));
                }
            }
            if let Some(s) = crate::stats::get() {
                s.record_physics_broadcast(bc_start.elapsed().as_micros() as u64);
            }
        }

        if now >= next_check {
            last_room_check = now;
            let room_arc = match room_weak.upgrade() {
                Some(r) => r,
                None => {
                    info!(
                        "[Room:{}] Physics task: room deallocated, stopping",
                        room_id
                    );
                    return;
                }
            };
            if room_arc.read().await.client_count() == 0 {
                info!(
                    "[Room:{}] Physics task: no clients remain, stopping",
                    room_id
                );
                return;
            }
        }

        // ── Stale claim cleanup ───────────────────────────────────────────
        // If a client grabs something and their send loop stalls (TCP keeps
        // the socket nominally alive but no packets flow), the object stays
        // frozen kinematic and unclaimable by anyone else until the 60s idle
        // disconnect fires. Sweep periodically so grabs self-release sooner.
        if now >= next_claim_check {
            last_claim_check = now;
            let stale = {
                let pw = pw_arc.lock().await;
                pw.stale_claims(STALE_CLAIM_TIMEOUT)
            };
            if !stale.is_empty() {
                let room_arc = match room_weak.upgrade() {
                    Some(r) => r,
                    None => return,
                };

                // Snapshot poses BEFORE release_stale (which may reset kinematic
                // state), so broadcasts carry authoritative pre-release position.
                let mut poses = Vec::with_capacity(stale.len());
                {
                    let pw = pw_arc.lock().await;
                    for (obj_id, _) in &stale {
                        poses.push(pw.read_body_pose(*obj_id));
                    }
                }
                {
                    let mut pw = pw_arc.lock().await;
                    for (obj_id, _) in &stale {
                        pw.release_stale(*obj_id);
                    }
                }

                let mut r = room_arc.write().await;
                for (i, (obj_id, owner)) in stale.iter().enumerate() {
                    if r.claimed_objects.get(obj_id) == Some(owner) {
                        r.claimed_objects.remove(obj_id);
                        let pose = poses.get(i).copied().flatten();
                        let msg = build_release_broadcast(*obj_id, *owner, pose);
                        r.last_release_bytes.insert(*obj_id, msg.clone());
                        broadcast_all(&r.clients, &msg);
                        info!(
                            "[Room:{}] stale claim on obj_{} (owner client_{}) auto-released",
                            room_id, obj_id, owner
                        );
                    }
                }
            }
        }
    }
}
