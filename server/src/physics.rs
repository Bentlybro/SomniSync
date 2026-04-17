use std::collections::HashMap;

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
    /// Claimed (kinematic) objects and their last known linear velocity
    pub claimed: HashMap<u16, [f32; 3]>,
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
        }
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

    /// Make the body for `obj_id` kinematic (a player has grabbed it).
    /// Stores it in `claimed` with zero initial velocity.
    pub fn claim(&mut self, obj_id: u16) {
        if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                rb.set_body_type(RigidBodyType::KinematicPositionBased, true);
                self.claimed.insert(obj_id, [0.0, 0.0, 0.0]);
            }
        }
    }

    /// Restore a claimed body to dynamic, applying the last known velocity.
    pub fn release(&mut self, obj_id: u16) {
        let last_vel = self.claimed.remove(&obj_id).unwrap_or([0.0, 0.0, 0.0]);

        if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                rb.set_body_type(RigidBodyType::Dynamic, true);
                rb.set_linvel(vector![last_vel[0], last_vel[1], last_vel[2]], true);
            }
        }
    }

    /// Update the kinematic body position and cache its velocity for the next `release`.
    #[allow(clippy::too_many_arguments)]
    pub fn update_claimed(
        &mut self,
        obj_id: u16,
        px: f32, py: f32, pz: f32,
        rx_deg: f32, ry_deg: f32, rz_deg: f32,
        vx: f32, vy: f32, vz: f32,
    ) {
        if let Some(&handle) = self.object_to_body.get(&obj_id) {
            if let Some(rb) = self.rigid_bodies.get_mut(handle) {
                rb.set_next_kinematic_translation(vector![px, py, pz]);
                let rot = nalgebra::UnitQuaternion::from_euler_angles(
                    rx_deg.to_radians(),
                    ry_deg.to_radians(),
                    rz_deg.to_radians(),
                );
                rb.set_next_kinematic_rotation(rot);
            }
        }
        // Always update cached velocity even if body lookup failed (for late claims)
        if self.claimed.contains_key(&obj_id) {
            self.claimed.insert(obj_id, [vx, vy, vz]);
        }
    }

    /// Snapshot every dynamic, awake body that could be broadcast this tick.
    /// Taken once per tick and reused to build per-client filtered messages.
    pub fn snapshot_dynamic(&self) -> Vec<DynamicSnapshot> {
        let mut out = Vec::with_capacity(self.object_to_body.len());
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
            if rb.is_sleeping() {
                continue;
            }
            let pos = *rb.translation();
            let rot = rb.rotation();
            let vel = *rb.linvel();
            let (roll, pitch, yaw) = rot.euler_angles();
            out.push(DynamicSnapshot {
                obj_id,
                pos: [pos.x, pos.y, pos.z],
                rot_deg: [roll.to_degrees(), pitch.to_degrees(), yaw.to_degrees()],
                vel: [vel.x, vel.y, vel.z],
            });
        }
        out
    }
}

/// Per-tick snapshot of one dynamic body.
#[derive(Clone)]
pub struct DynamicSnapshot {
    pub obj_id: u16,
    pub pos: [f32; 3],
    pub rot_deg: [f32; 3],
    pub vel: [f32; 3],
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

pub const AOI_NEAR_RADIUS: f32 = 25.0;
pub const AOI_MID_RADIUS: f32 = 60.0;
pub const AOI_FAR_RADIUS: f32 = 150.0;

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
    snapshot: &[DynamicSnapshot],
    player_pos: Option<[f32; 3]>,
    tick: u32,
    seq: u16,
) -> Option<Vec<u8>> {
    if snapshot.is_empty() {
        return None;
    }

    let mut buf = Vec::with_capacity(7 + snapshot.len() * 20);
    buf.push(MSG_POSITION_VELOCITY);
    push_u16(&mut buf, 0);
    push_u16(&mut buf, seq);
    push_u16(&mut buf, 0);

    let mut count: u16 = 0;

    for entry in snapshot {
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

        push_u16(&mut buf, entry.obj_id);
        push_f16(&mut buf, entry.pos[0]);
        push_f16(&mut buf, entry.pos[1]);
        push_f16(&mut buf, entry.pos[2]);
        push_rot_u16(&mut buf, entry.rot_deg[0]);
        push_rot_u16(&mut buf, entry.rot_deg[1]);
        push_rot_u16(&mut buf, entry.rot_deg[2]);
        push_f16(&mut buf, entry.vel[0]);
        push_f16(&mut buf, entry.vel[1]);
        push_f16(&mut buf, entry.vel[2]);
        count += 1;
    }

    if count == 0 {
        return None;
    }

    buf[5] = (count & 0xFF) as u8;
    buf[6] = (count >> 8) as u8;
    Some(buf)
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

/// Encode a rotation in degrees as a u16 in range [0, 65535] mapping to [0°, 360°).
#[inline]
fn push_rot_u16(buf: &mut Vec<u8>, degrees: f32) {
    let mut d = degrees % 360.0;
    if d < 0.0 {
        d += 360.0;
    }
    let encoded = (d * (65535.0 / 360.0)) as u16;
    push_u16(buf, encoded);
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
