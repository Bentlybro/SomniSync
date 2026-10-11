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
    let first_runtime_id = objects
        .iter()
        .map(|object| u32::from(object.id) + 1)
        .max()
        .unwrap_or(0);
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
        r.next_runtime_id = first_runtime_id;
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
    const BROADCAST_HZ: u64 = 20;
    const BROADCAST_INTERVAL: Duration = Duration::from_millis(1000 / BROADCAST_HZ);
    const FAST_PHYSICS_HZ: u32 = 50;
    const LOADED_PHYSICS_HZ: u32 = 30;
    const SLOW_STEP_US: f64 = 16_000.0;
    const RECOVER_STEP_US: f64 = 10_000.0;
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
    let mut physics_hz = FAST_PHYSICS_HZ;
    let mut physics_interval = Duration::from_secs_f64(1.0 / physics_hz as f64);
    let mut step_us_ema = 0.0_f64;
    let mut step_samples: u32 = 0;
    let mut recovery_steps: u32 = 0;

    info!(
        "[Room:{}] Physics tick task started (50Hz sim / 20Hz broadcast)",
        room_id
    );

    loop {
        let now = Instant::now();
        let next_physics = last_physics + physics_interval;
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

        // Single physics-mutex acquisition per tick: step + snapshot for broadcast.
        let buckets_opt = if do_physics || now >= next_broadcast {
            let mut pw = pw_arc.lock().await;
            if do_physics {
                last_physics = now;
                let t0 = Instant::now();
                pw.step();
                let us = t0.elapsed().as_micros() as u64;
                if let Some(s) = crate::stats::get() {
                    s.record_physics_tick(us);
                }
                step_samples = step_samples.saturating_add(1);
                step_us_ema = if step_samples == 1 {
                    us as f64
                } else {
                    step_us_ema * 0.9 + us as f64 * 0.1
                };
                if physics_hz == FAST_PHYSICS_HZ {
                    // The moving average ignores isolated OS scheduling spikes
                    // while reacting within about a second to sustained load.
                    if step_samples >= 20 && step_us_ema >= SLOW_STEP_US {
                        physics_hz = LOADED_PHYSICS_HZ;
                        physics_interval = Duration::from_secs_f64(1.0 / physics_hz as f64);
                        pw.set_step_hz(physics_hz);
                        step_samples = 0;
                        info!(
                            "[Room:{}] Physics load high; stepping at {} Hz",
                            room_id, physics_hz
                        );
                    }
                } else {
                    recovery_steps = if step_us_ema <= RECOVER_STEP_US {
                        recovery_steps.saturating_add(1)
                    } else {
                        0
                    };
                    if recovery_steps >= 150 {
                        physics_hz = FAST_PHYSICS_HZ;
                        physics_interval = Duration::from_secs_f64(1.0 / physics_hz as f64);
                        pw.set_step_hz(physics_hz);
                        recovery_steps = 0;
                        step_samples = 0;
                        info!(
                            "[Room:{}] Physics load recovered; stepping at {} Hz",
                            room_id, physics_hz
                        );
                    }
                }
            }
            // A heavy physics step can cross a broadcast deadline. Check
            // after stepping and keep deadlines anchored to the 20 Hz clock;
            // anchoring to the late send time would compound the delay.
            let after_step = Instant::now();
            if after_step >= last_broadcast + BROADCAST_INTERVAL {
                if after_step - last_broadcast > Duration::from_secs(1) {
                    last_broadcast = after_step;
                } else {
                    while after_step >= last_broadcast + BROADCAST_INTERVAL {
                        last_broadcast += BROADCAST_INTERVAL;
                    }
                }
                Some(pw.snapshot_dynamic())
            } else {
                None
            }
        } else {
            None
        };

        if let Some(buckets) = buckets_opt {
            server_sequence = server_sequence.wrapping_add(1).max(1);
            let seq = server_sequence;
            let sleep_seq = if buckets.just_slept.is_empty() {
                seq
            } else {
                server_sequence = server_sequence.wrapping_add(1).max(1);
                server_sequence
            };
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
            let (clients_clone, player_pos_clone, state_epoch) = {
                let r = room_arc.read().await;
                if r.client_count() == 0 {
                    info!("[Room:{}] Physics task: no clients, stopping", room_id);
                    return;
                }
                // A sleeping body is a one-shot final pose. It must use the
                // ordered queue; a coalesced state frame may be overwritten.
                let settled: Vec<_> = buckets
                    .just_slept
                    .iter()
                    .filter(|entry| !r.claimed_objects.contains_key(&entry.obj_id))
                    .cloned()
                    .collect();
                if let Some(msg) =
                    build_filtered_message(&[], &settled, None, tick, sleep_seq, false)
                {
                    broadcast_all(&r.clients, &msg);
                }
                (
                    r.clients
                        .iter()
                        .map(|(&id, c)| (id, c.clone()))
                        .collect::<Vec<(u16, Arc<Client>)>>(),
                    r.player_positions.clone(),
                    r.state_epoch,
                )
            };

            let bc_start = Instant::now();
            // Clients that have not sent a player position all need the same
            // unfiltered snapshot. Serialize it once per wire format and share
            // the Bytes allocation across their send tasks.
            let mut shared_compact: Option<bytes::Bytes> = None;
            let mut shared_legacy: Option<bytes::Bytes> = None;
            for (cid, client) in &clients_clone {
                let pp = player_pos_clone.get(cid).copied();
                let msg = if pp.is_none() {
                    let cached = if client.supports_compact_positions() {
                        &mut shared_compact
                    } else {
                        &mut shared_legacy
                    };
                    if cached.is_none() {
                        *cached = build_filtered_message(
                            &buckets.awake,
                            &[],
                            None,
                            tick,
                            seq,
                            client.supports_compact_positions(),
                        )
                        .map(bytes::Bytes::from);
                    }
                    cached.clone()
                } else {
                    build_filtered_message(
                        &buckets.awake,
                        &[],
                        pp,
                        tick,
                        seq,
                        client.supports_compact_positions(),
                    )
                    .map(bytes::Bytes::from)
                };
                if let Some(msg) = msg {
                    let r = room_arc.read().await;
                    if r.state_epoch == state_epoch {
                        send_try(client, msg);
                    }
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
            let room_arc = match room_weak.upgrade() {
                Some(r) => r,
                None => return,
            };
            // Keep ownership and physics state in one lock order. A fresh
            // update or a new claim cannot race an old timeout decision.
            let mut r = room_arc.write().await;
            let mut pw = pw_arc.lock().await;
            for (obj_id, owner) in pw.stale_claims(STALE_CLAIM_TIMEOUT) {
                if r.claimed_objects.get(&obj_id) != Some(&owner) {
                    continue;
                }
                let pose = pw.read_body_pose(obj_id);
                if pw.release_stale(obj_id) != Some(owner) {
                    continue;
                }
                r.claimed_objects.remove(&obj_id);
                r.state_epoch = r.state_epoch.wrapping_add(1);
                let msg = build_release_broadcast(obj_id, owner, pose);
                r.last_release_bytes.insert(obj_id, msg.clone());
                broadcast_all(&r.clients, &msg);
                info!(
                    "[Room:{}] stale claim on obj_{} (owner client_{}) auto-released",
                    room_id, obj_id, owner
                );
            }
        }
    }
}
