use std::collections::HashMap;
use std::time::{Duration, Instant};

use half::f16;
use log::warn;
use rapier3d::prelude::{
    nalgebra, BroadPhaseMultiSap, CCDSolver, ColliderBuilder, ColliderSet, ImpulseJointSet,
    IntegrationParameters, IslandManager, MultibodyJointSet, NarrowPhase, PhysicsPipeline,
    RigidBodyBuilder, RigidBodyHandle, RigidBodySet, RigidBodyType, vector,
};

use crate::protocol::MSG_POSITION_VELOCITY;

/// Description of one physics object sent in a SCENE_INIT message.
pub struct ObjectDesc {
    pub id: u16,
    pub body_type: u8,  // 0=Dynamic, 1=Fixed/Static, 2=KinematicPosition
    pub shape_type: u8, // 0=Ball(sphere), 1=Cuboid(box), 2=Capsule
    pub position: [f32; 3],
    pub rotation_euler_deg: [f32; 3],
    pub shape_params: [f32; 3], // Ball:[radius,0,0], Cuboid:[hx,hy,hz], Capsule:[radius,half_height,0]
    pub mass: f32,
    pub restitution: f32,
    pub friction: f32,
    pub linear_damping: f32,
    pub angular_damping: f32,
}

/// Server-side physics simulation using Rapier3D.
pub struct PhysicsWorld {
    pub rigid_bodies: RigidBodySet,
    pub colliders: ColliderSet,
    pipeline: PhysicsPipeline,
    island_manager: IslandManager,
    broad_phase: BroadPhaseMultiSap,
    narrow_phase: NarrowPhase,
    impulse_joints: ImpulseJointSet,
    multibody_joints: MultibodyJointSet,
    ccd_solver: CCDSolver,
    integration_params: IntegrationParameters,

    /// Maps network object ID → Rapier RigidBodyHandle
    pub object_to_body: HashMap<u16, RigidBodyHandle>,
    /// Maps Rapier RigidBodyHandle → network object ID
    pub body_to_object: HashMap<RigidBodyHandle, u16>,
    /// Currently-claimed (kinematic) objects and their owning client. Tracks
    /// the last position + timestamp + derived velocity per body; velocity is
    /// recomputed from position deltas so throws transfer real momentum even
    /// though the held-object wire format has no velocity field.
    pub claimed: HashMap<u16, ClaimedTrack>,
    /// Per-body "was awake last snapshot" — used to detect sleep transitions.
    /// On awake→sleeping, the final position is sent to every client regardless
    /// of AOI so they know where the body actually came to rest.
    awake_prev: HashMap<u16, bool>,
    /// Kinematic capsule per player so Rapier sees player-object collisions
    /// directly (pushing spheres naturally as you walk into them). Keyed by
    /// client_id. Dimensions are whatever the client reports with its player
    /// position update, so every player's body shape matches their actual rig.
    player_bodies: HashMap<u16, PlayerBody>,
}

struct PlayerBody {
    handle: RigidBodyHandle,
    radius: f32,
    half_height: f32,
}

impl PhysicsWorld {
    /// Create a new physics world with standard Earth gravity.
    pub fn new() -> Self {
        let mut integration_params = IntegrationParameters::default();
        integration_params.dt = 1.0 / 50.0; // 50 Hz simulation

        Self {
            rigid_bodies: RigidBodySet::new(),
            colliders: ColliderSet::new(),
            pipeline: PhysicsPipeline::new(),
            island_manager: IslandManager::new(),
            broad_phase: BroadPhaseMultiSap::new(),
            narrow_phase: NarrowPhase::new(),
            impulse_joints: ImpulseJointSet::new(),
            multibody_joints: MultibodyJointSet::new(),
            ccd_solver: CCDSolver::new(),
            integration_params,
            object_to_body: HashMap::new(),
            body_to_object: HashMap::new(),
            claimed: HashMap::new(),
            awake_prev: HashMap::new(),
            player_bodies: HashMap::new(),
        }
    }

    /// Update (or create) the kinematic capsule that represents a player in
    /// the physics world. Called every time the client reports a new position.
    /// Capsule dims are tolerated changing — if the client re-sends different
    /// radius/half_height (e.g. crouch), the collider is rebuilt.
    pub fn update_player_body(
        &mut self,
        client_id: u16,
        pos: [f32; 3],
        radius: f32,
        half_height: f32,
    ) {
        let r = radius.max(0.05);
        let hh = half_height.max(0.01);

        if let Some(pb) = self.player_bodies.get(&client_id) {
            // Shape unchanged — just move the body.
            if (pb.radius - r).abs() < 1e-3 && (pb.half_height - hh).abs() < 1e-3 {
                if let Some(rb) = self.rigid_bodies.get_mut(pb.handle) {
                    rb.set_next_kinematic_translation(vector![pos[0], pos[1], pos[2]]);
                }
                return;
            }
            // Shape changed — drop and recreate below.
            let handle = pb.handle;
            self.remove_body_handle(handle);
            self.player_bodies.remove(&client_id);
        }

        let rb = RigidBodyBuilder::kinematic_position_based()
            .translation(vector![pos[0], pos[1], pos[2]])
            .build();
        let handle = self.rigid_bodies.insert(rb);

        let collider = ColliderBuilder::capsule_y(hh, r)
            .friction(0.3)
            .restitution(0.0)
            .build();
        self.colliders
            .insert_with_parent(collider, handle, &mut self.rigid_bodies);

        self.player_bodies.insert(
            client_id,
            PlayerBody {
                handle,
                radius: r,
                half_height: hh,
            },
        );
    }

    /// Remove a player's capsule from the physics world (on disconnect).
    pub fn remove_player_body(&mut self, client_id: u16) {
        if let Some(pb) = self.player_bodies.remove(&client_id) {
            self.remove_body_handle(pb.handle);
        }
    }

    /// Remove a runtime-spawned object from the physics world. Used by the
    /// despawn path. Leaves the id reserved in the ID counter (no reuse).
    pub fn remove_object(&mut self, obj_id: u16) {
        if let Some(handle) = self.object_to_body.remove(&obj_id) {
            self.body_to_object.remove(&handle);
            self.claimed.remove(&obj_id);
            self.awake_prev.remove(&obj_id);
            self.remove_body_handle(handle);
        }
    }

    fn remove_body_handle(&mut self, handle: RigidBodyHandle) {
        self.rigid_bodies.remove(
            handle,
            &mut self.island_manager,
            &mut self.colliders,
            &mut self.impulse_joints,
            &mut self.multibody_joints,
            true, // wake up neighbors so nearby spheres respond to the removal
        );
    }

    /// Add one physics body described by `desc`.
    pub fn add_object(&mut self, desc: &ObjectDesc) {
        // Build rotation quaternion from Euler degrees (XYZ order)
        let rx = desc.rotation_euler_deg[0].to_radians();
        let ry = desc.rotation_euler_deg[1].to_radians();
        let rz = desc.rotation_euler_deg[2].to_radians();
        let rot = nalgebra::UnitQuaternion::from_euler_angles(rx, ry, rz);

        let body_type = match desc.body_type {
            1 => RigidBodyType::Fixed,
            2 => RigidBodyType::KinematicPositionBased,
            _ => RigidBodyType::Dynamic,
        };

        let rb = RigidBodyBuilder::new(body_type)
            .translation(vector![
                desc.position[0],
                desc.position[1],
                desc.position[2]
            ])
            .rotation(rot.scaled_axis())
            .linear_damping(desc.linear_damping)
            .angular_damping(desc.angular_damping)
            .build();

        let handle = self.rigid_bodies.insert(rb);

        // Build collider
        let collider = match desc.shape_type {
            0 => {
                // Ball — shape_params[0] = radius
                let radius = desc.shape_params[0].max(0.001);
                ColliderBuilder::ball(radius)
                    .restitution(desc.restitution)
                    .friction(desc.friction)
                    .density(if desc.mass > 0.0 {
                        // Rapier takes density; compute from mass and volume
                        let vol = (4.0 / 3.0) * std::f32::consts::PI * radius * radius * radius;
                        desc.mass / vol.max(f32::EPSILON)
                    } else {
                        1.0
                    })
                    .build()
            }
            1 => {
                // Cuboid — shape_params = half-extents [hx, hy, hz]
                let hx = desc.shape_params[0].max(0.001);
                let hy = desc.shape_params[1].max(0.001);
                let hz = desc.shape_params[2].max(0.001);
                ColliderBuilder::cuboid(hx, hy, hz)
                    .restitution(desc.restitution)
                    .friction(desc.friction)
                    .density(if desc.mass > 0.0 {
                        let vol = 8.0 * hx * hy * hz;
                        desc.mass / vol.max(f32::EPSILON)
                    } else {
                        1.0
                    })
                    .build()
            }
            2 => {
                // Capsule — shape_params[0]=radius, shape_params[1]=half_height
                let radius = desc.shape_params[0].max(0.001);
                let half_height = desc.shape_params[1].max(0.001);
                ColliderBuilder::capsule_y(half_height, radius)
                    .restitution(desc.restitution)
                    .friction(desc.friction)
                    .density(if desc.mass > 0.0 {
                        // Approximate capsule volume
                        let cyl_vol = std::f32::consts::PI * radius * radius * 2.0 * half_height;
                        let cap_vol = (4.0 / 3.0) * std::f32::consts::PI * radius * radius * radius;
                        let vol = cyl_vol + cap_vol;
                        desc.mass / vol.max(f32::EPSILON)
                    } else {
                        1.0
                    })
                    .build()
            }
            other => {
                warn!("[Physics] Unknown shape type {} for object {}, defaulting to unit sphere", other, desc.id);
                ColliderBuilder::ball(0.5)
                    .restitution(desc.restitution)
                    .friction(desc.friction)
                    .build()
            }
        };

        self.colliders.insert_with_parent(collider, handle, &mut self.rigid_bodies);
        self.object_to_body.insert(desc.id, handle);
        self.body_to_object.insert(handle, desc.id);
    }

    /// Advance the simulation by one fixed timestep (dt = 1/50 s).
    pub fn step(&mut self) {
        let gravity = vector![0.0_f32, -9.81, 0.0];
        let physics_hooks = ();
        let event_handler = ();

        self.pipeline.step(
            &gravity,
            &self.integration_params,
            &mut self.island_manager,
            &mut self.broad_phase,
            &mut self.narrow_phase,
            &mut self.rigid_bodies,
            &mut self.colliders,
            &mut self.impulse_joints,
            &mut self.multibody_joints,
            &mut self.ccd_solver,
            None,
            &physics_hooks,
            &event_handler,
        );
    }

    /// Make the body for `obj_id` kinematic (a player has grabbed it). Stores
    /// the current position so the first `update_claimed` can derive a sensible
    /// velocity delta.
    pub fn claim(&mut self, obj_id: u16, owner: u16) {
        let pos = if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                rb.set_body_type(RigidBodyType::KinematicPositionBased, true);
                let t = rb.translation();
                [t.x, t.y, t.z]
            } else {
                [0.0, 0.0, 0.0]
            }
        } else {
            [0.0, 0.0, 0.0]
        };

        self.claimed.insert(
            obj_id,
            ClaimedTrack {
                owner,
                last_pos: pos,
                last_update: Instant::now(),
                velocity: [0.0, 0.0, 0.0],
            },
        );
    }

    /// Release a claim explicitly (no caller-chosen velocity) — used for stale
    /// claim cleanup. Applies the last derived velocity so the object coasts
    /// on exactly like a normal release.
    pub fn release_stale(&mut self, obj_id: u16) -> Option<u16> {
        let track = self.claimed.remove(&obj_id)?;
        if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                rb.set_body_type(RigidBodyType::Dynamic, true);
                rb.set_linvel(
                    vector![track.velocity[0], track.velocity[1], track.velocity[2]],
                    true,
                );
            }
        }
        Some(track.owner)
    }

    /// Walk `claimed` and return (obj_id, owner) for any entry whose last
    /// update was more than `timeout` ago. Handler uses this to clean up
    /// stuck claims from stalled clients.
    pub fn stale_claims(&self, timeout: Duration) -> Vec<(u16, u16)> {
        let now = Instant::now();
        self.claimed
            .iter()
            .filter(|(_, t)| now.duration_since(t.last_update) > timeout)
            .map(|(&id, t)| (id, t.owner))
            .collect()
    }

    /// Apply a push impulse to a dynamic body — used when a player walks into
    /// an object. Passes a velocity vector; internally multiplied by mass so it
    /// behaves as a proper momentum transfer through Rapier. Kinematic / fixed
    /// bodies are ignored.
    pub fn apply_push_impulse(&mut self, obj_id: u16, velocity: [f32; 3]) {
        if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                if rb.body_type() != RigidBodyType::Dynamic {
                    return;
                }
                let mass = rb.mass();
                if mass <= 0.0 {
                    return;
                }
                let impulse = vector![
                    velocity[0] * mass,
                    velocity[1] * mass,
                    velocity[2] * mass
                ];
                rb.apply_impulse(impulse, true);
            }
        }
    }

    /// Restore a claimed body to dynamic, applying the velocity derived from
    /// the client's last few position updates. Throws properly transfer
    /// momentum even though the wire format carries no velocity field.
    pub fn release(&mut self, obj_id: u16) {
        let velocity = self
            .claimed
            .remove(&obj_id)
            .map(|t| t.velocity)
            .unwrap_or([0.0, 0.0, 0.0]);

        if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                rb.set_body_type(RigidBodyType::Dynamic, true);
                rb.set_linvel(vector![velocity[0], velocity[1], velocity[2]], true);
            }
        }
    }

    /// Update the kinematic body position. Derives velocity from the position
    /// delta over real time so `release` can transfer the motion as momentum.
    #[allow(clippy::too_many_arguments)]
    pub fn update_claimed(
        &mut self,
        obj_id: u16,
        px: f32, py: f32, pz: f32,
        qx: f32, qy: f32, qz: f32, qw: f32,
    ) {
        if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                rb.set_next_kinematic_translation(vector![px, py, pz]);
                let q = nalgebra::Quaternion::new(qw, qx, qy, qz);
                let rot = nalgebra::UnitQuaternion::from_quaternion(q);
                rb.set_next_kinematic_rotation(rot);
            }
        }

        // Smooth velocity with exponential averaging so a single jittery
        // packet doesn't set a wildly wrong release velocity.
        if let Some(track) = self.claimed.get_mut(&obj_id) {
            let now = Instant::now();
            let dt = now.duration_since(track.last_update).as_secs_f32();
            if dt > 1e-4 && dt < 0.5 {
                let instant = [
                    (px - track.last_pos[0]) / dt,
                    (py - track.last_pos[1]) / dt,
                    (pz - track.last_pos[2]) / dt,
                ];
                const SMOOTHING: f32 = 0.5; // 50/50 blend with previous sample
                track.velocity = [
                    track.velocity[0] * (1.0 - SMOOTHING) + instant[0] * SMOOTHING,
                    track.velocity[1] * (1.0 - SMOOTHING) + instant[1] * SMOOTHING,
                    track.velocity[2] * (1.0 - SMOOTHING) + instant[2] * SMOOTHING,
                ];
            }
            track.last_pos = [px, py, pz];
            track.last_update = now;
        }
    }

    /// Snapshot every dynamic body whose state clients may need to see this tick.
    ///
    /// Two buckets are returned:
    ///
    ///   **awake**: Currently-moving bodies. Subject to per-client AOI +
    ///   LOD filtering when building per-client messages.
    ///
    ///   **just_slept**: Bodies that transitioned awake→sleeping *this tick*.
    ///   Broadcast unconditionally (bypassing AOI) so every client learns the
    ///   final resting position, even if they were nowhere near when the body
    ///   settled. Without this, far-away clients see objects frozen at their
    ///   last mid-flight position indefinitely — a tower of ghost spheres.
    pub fn snapshot_dynamic(&mut self) -> SnapshotBuckets {
        let mut awake = Vec::with_capacity(self.object_to_body.len());
        let mut just_slept = Vec::new();

        for (&obj_id, &handle) in &self.object_to_body {
            if self.claimed.contains_key(&obj_id) {
                continue;
            }
            let rb = match self.rigid_bodies.get(handle) {
                Some(r) => r,
                None => continue,
            };
            if rb.body_type() != RigidBodyType::Dynamic {
                continue;
            }

            let is_awake = !rb.is_sleeping();
            let was_awake = self.awake_prev.get(&obj_id).copied().unwrap_or(false);

            let pos = *rb.translation();
            let rot = rb.rotation();
            let vel = *rb.linvel();
            let snap = DynamicSnapshot {
                obj_id,
                pos: [pos.x, pos.y, pos.z],
                quat: [rot.i, rot.j, rot.k, rot.w],
                vel: [vel.x, vel.y, vel.z],
            };

            if is_awake {
                awake.push(snap);
            } else if was_awake {
                // Awake→sleeping transition — final resting state for all clients.
                just_slept.push(snap);
            }
        }

        // Update wake-state tracking after building the buckets.
        for (&obj_id, &handle) in &self.object_to_body {
            if let Some(rb) = self.rigid_bodies.get(handle) {
                self.awake_prev.insert(obj_id, !rb.is_sleeping());
            }
        }

        SnapshotBuckets { awake, just_slept }
    }
}

/// Output of `snapshot_dynamic` — bodies that need broadcasting this tick,
/// split by how they should be filtered.
pub struct SnapshotBuckets {
    pub awake: Vec<DynamicSnapshot>,
    pub just_slept: Vec<DynamicSnapshot>,
}

/// Per-tick snapshot of one dynamic body. Rotation is stored as a quaternion
/// (x, y, z, w) — avoids Euler-angle gimbal instability on the wire.
#[derive(Clone)]
pub struct DynamicSnapshot {
    pub obj_id: u16,
    pub pos: [f32; 3],
    pub quat: [f32; 4],
    pub vel: [f32; 3],
}

/// State tracked per claimed (kinematic) body so `release` can transfer real
/// momentum and the claim-timeout system can detect stalled owners.
pub struct ClaimedTrack {
    pub owner: u16,
    pub last_pos: [f32; 3],
    pub last_update: Instant,
    pub velocity: [f32; 3],
}

// ─── AOI + LOD tuning ─────────────────────────────────────────────────────────
//
// Objects within AOI_NEAR: broadcast every tick.
// Between near and mid: every LOD_MID_SKIP ticks.
// Between mid and far: every LOD_FAR_SKIP ticks.
// Beyond far: not broadcast at all.
//
// Spread over time using `(tick + obj_id) % N` so far objects don't arrive in
// one giant packet — different objects hit different phase offsets.

pub const AOI_NEAR_RADIUS: f32 = 50.0;
pub const AOI_MID_RADIUS: f32 = 150.0;
pub const AOI_FAR_RADIUS: f32 = 500.0;

const AOI_NEAR_SQ: f32 = AOI_NEAR_RADIUS * AOI_NEAR_RADIUS;
const AOI_MID_SQ: f32 = AOI_MID_RADIUS * AOI_MID_RADIUS;
const AOI_FAR_SQ: f32 = AOI_FAR_RADIUS * AOI_FAR_RADIUS;

const LOD_MID_SKIP: u32 = 3;
const LOD_FAR_SKIP: u32 = 10;

/// Build a 0x0B position+velocity message filtered to what this client needs
/// to see this tick. Returns None if nothing is eligible.
///
/// If `player_pos` is None, the client has not yet reported a position — send
/// everything so visuals aren't empty during the first few frames.
pub fn build_filtered_message(
    awake: &[DynamicSnapshot],
    just_slept: &[DynamicSnapshot],
    player_pos: Option<[f32; 3]>,
    tick: u32,
    seq: u16,
) -> Option<Vec<u8>> {
    if awake.is_empty() && just_slept.is_empty() {
        return None;
    }

    let mut buf = Vec::with_capacity(7 + (awake.len() + just_slept.len()) * 28);
    buf.push(MSG_POSITION_VELOCITY);
    push_u16(&mut buf, 0);
    push_u16(&mut buf, seq);
    push_u16(&mut buf, 0);

    let mut count: u16 = 0;

    // Awake bodies: standard AOI + LOD filtering.
    for entry in awake {
        if let Some(pp) = player_pos {
            let dx = entry.pos[0] - pp[0];
            let dy = entry.pos[1] - pp[1];
            let dz = entry.pos[2] - pp[2];
            let d_sq = dx * dx + dy * dy + dz * dz;

            if d_sq > AOI_FAR_SQ {
                continue;
            }

            let phase = tick.wrapping_add(entry.obj_id as u32);
            if d_sq > AOI_MID_SQ {
                if phase % LOD_FAR_SKIP != 0 {
                    continue;
                }
            } else if d_sq > AOI_NEAR_SQ {
                if phase % LOD_MID_SKIP != 0 {
                    continue;
                }
            }
        }

        push_entity(&mut buf, entry);
        count += 1;
    }

    // Sleep transitions: bypass AOI entirely so every client learns the
    // final resting state even if they were nowhere near when it settled.
    for entry in just_slept {
        push_entity(&mut buf, entry);
        count += 1;
    }

    if count == 0 {
        return None;
    }

    buf[5] = (count & 0xFF) as u8;
    buf[6] = (count >> 8) as u8;
    Some(buf)
}

#[inline]
fn push_entity(buf: &mut Vec<u8>, entry: &DynamicSnapshot) {
    push_u16(buf, entry.obj_id);
    push_f32(buf, entry.pos[0]);
    push_f32(buf, entry.pos[1]);
    push_f32(buf, entry.pos[2]);
    push_quat(buf, entry.quat);
    push_f16(buf, entry.vel[0]);
    push_f16(buf, entry.vel[1]);
    push_f16(buf, entry.vel[2]);
}

// ─── Binary encoding helpers ──────────────────────────────────────────────────

#[inline]
fn push_u16(buf: &mut Vec<u8>, v: u16) {
    buf.push((v & 0xFF) as u8);
    buf.push((v >> 8) as u8);
}

#[inline]
fn push_f16(buf: &mut Vec<u8>, v: f32) {
    let bits = f16::from_f32(v).to_bits();
    buf.push((bits & 0xFF) as u8);
    buf.push((bits >> 8) as u8);
}

#[inline]
fn push_f32(buf: &mut Vec<u8>, v: f32) {
    buf.extend_from_slice(&v.to_le_bytes());
}

/// Encode a unit quaternion as 4 × i16 quantized to [-1, 1].
/// Precision ≈ 3e-5 per component, free of Euler gimbal artifacts.
#[inline]
fn push_quat(buf: &mut Vec<u8>, q: [f32; 4]) {
    for &c in &q {
        let scaled = (c.clamp(-1.0, 1.0) * 32767.0).round() as i16;
        buf.extend_from_slice(&scaled.to_le_bytes());
    }
}

// ─── SCENE_INIT parser ────────────────────────────────────────────────────────

/// Parse a SCENE_INIT binary message into a list of `ObjectDesc`.
///
/// Format:
/// ```text
/// [0x10]              1 byte  message type
/// [u16 count]         2 bytes
/// Per object (60 bytes):
///   [u16 id]          2 bytes
///   [u8 body_type]    1 byte
///   [u8 shape_type]   1 byte
///   [f32 px,py,pz]   12 bytes
///   [f32 rx,ry,rz]   12 bytes
///   [f32 sx,sy,sz]   12 bytes  (shape params)
///   [f32 mass]        4 bytes
///   [f32 restitution] 4 bytes
///   [f32 friction]    4 bytes
///   [f32 lin_damp]    4 bytes
///   [f32 ang_damp]    4 bytes
/// ```
pub fn parse_scene_init(data: &[u8]) -> Option<Vec<ObjectDesc>> {
    const OBJECT_BYTES: usize = 60;
    const HEADER_BYTES: usize = 3; // [0x10][u16 count]

    if data.len() < HEADER_BYTES {
        warn!("[Physics] SCENE_INIT too short ({} bytes)", data.len());
        return None;
    }
    if data[0] != 0x10 {
        warn!("[Physics] SCENE_INIT has wrong type byte 0x{:02X}", data[0]);
        return None;
    }

    let count = u16::from_le_bytes([data[1], data[2]]) as usize;
    let expected = HEADER_BYTES + count * OBJECT_BYTES;
    if data.len() < expected {
        warn!(
            "[Physics] SCENE_INIT truncated: expected {} bytes for {} objects, got {}",
            expected, count, data.len()
        );
        return None;
    }

    let mut objects = Vec::with_capacity(count);
    let mut offset = HEADER_BYTES;

    for _ in 0..count {
        let id         = u16::from_le_bytes([data[offset], data[offset + 1]]);
        let body_type  = data[offset + 2];
        let shape_type = data[offset + 3];
        offset += 4;

        let px = read_f32(data, offset);     offset += 4;
        let py = read_f32(data, offset);     offset += 4;
        let pz = read_f32(data, offset);     offset += 4;

        let rx = read_f32(data, offset);     offset += 4;
        let ry = read_f32(data, offset);     offset += 4;
        let rz = read_f32(data, offset);     offset += 4;

        let sx = read_f32(data, offset);     offset += 4;
        let sy = read_f32(data, offset);     offset += 4;
        let sz = read_f32(data, offset);     offset += 4;

        let mass        = read_f32(data, offset); offset += 4;
        let restitution = read_f32(data, offset); offset += 4;
        let friction    = read_f32(data, offset); offset += 4;
        let lin_damp    = read_f32(data, offset); offset += 4;
        let ang_damp    = read_f32(data, offset); offset += 4;

        objects.push(ObjectDesc {
            id,
            body_type,
            shape_type,
            position: [px, py, pz],
            rotation_euler_deg: [rx, ry, rz],
            shape_params: [sx, sy, sz],
            mass,
            restitution,
            friction,
            linear_damping: lin_damp,
            angular_damping: ang_damp,
        });
    }

    Some(objects)
}

#[inline]
fn read_f32(data: &[u8], offset: usize) -> f32 {
    f32::from_le_bytes([data[offset], data[offset + 1], data[offset + 2], data[offset + 3]])
}
