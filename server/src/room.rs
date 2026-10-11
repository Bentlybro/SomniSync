use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::{Mutex, RwLock};

use crate::client::Client;
use crate::physics::PhysicsWorld;
use crate::variable_state::VariableState;

/// All rooms keyed by room_id string.
pub type RoomMap = Arc<RwLock<HashMap<String, Arc<RwLock<Room>>>>>;

/// Room state — tracks connected clients, ownership of grabbed objects, player
/// positions (for AOI), and the authoritative physics world.
///
/// There is no master/spectator distinction: the server is always the physics
/// authority. The first client to send a SCENE_INIT bootstraps the Rapier world;
/// every client after that just receives updates.
pub struct Room {
    pub clients: HashMap<u16, Arc<Client>>,
    next_id: u16,

    /// True while the first SCENE_INIT is being parsed/built. Prevents two
    /// near-simultaneous first clients from both creating physics worlds and
    /// spawning duplicate tick tasks before `physics_ready` flips true.
    pub scene_initializing: bool,

    /// Object ownership: object_local_id → client_id. Held objects ("grabs")
    /// are driven by that client; the server makes their physics body kinematic
    /// while claimed.
    pub claimed_objects: HashMap<u16, u16>,
    /// One elected writer for component state on unclaimed objects.
    pub state_writer_id: u16,
    /// Fingerprint of the Unity scene layout supplied during the WebSocket
    /// handshake. Clients with a different layout cannot join this room.
    pub scene_fingerprint: u64,
    pub variable_state: VariableState,
    /// Changes whenever a world transition can invalidate an unsent snapshot.
    pub state_epoch: u64,

    /// Stable player identity: client_id → Somnium player UUID (or fallback UUID)
    pub player_ids: HashMap<u16, String>,

    /// Latest known player head/body position per client. Used by server AOI to
    /// filter outgoing position broadcasts. Absent until the client reports one.
    pub player_positions: HashMap<u16, [f32; 3]>,

    /// True once the physics world has been initialized by a SCENE_INIT.
    pub physics_ready: bool,

    /// The authoritative physics world — present only after SCENE_INIT.
    pub physics_world: Option<Arc<Mutex<PhysicsWorld>>>,

    /// Number of baked scene objects from the initial SCENE_INIT. Runtime
    /// despawn requests are only accepted for ids in `runtime_spawns`, never
    /// for baked ids below this boundary.
    pub scene_object_count: u16,

    /// Next runtime ID, held in u32 so exhaustion cannot wrap into baked IDs.
    /// Set to one past the highest scene object ID, which can differ from the
    /// number of scene objects when some Unity objects have no collider.
    pub next_runtime_id: u32,

    /// Count of currently-live runtime spawns. Kept separate from object IDs so
    /// the runtime spawn cap does not depend on how many baked scene objects
    /// the room started with.
    pub runtime_spawn_count: u16,

    /// Runtime spawn replay for late joiners. Keyed by object local_id, value is
    /// the full CTRL_SPAWN broadcast frame originally sent to clients.
    pub runtime_spawns: HashMap<u16, Vec<u8>>,

    /// Runtime spawn owner. Client despawn is authorized only for the original
    /// spawner; server-side teardown can bypass this by mutating state directly.
    pub runtime_spawn_owners: HashMap<u16, u16>,

    /// Unix ms at the moment physics first went live (the first SCENE_INIT
    /// landed). Shared with every client via CTRL_WELCOME + CTRL_PHYSICS_READY
    /// so deterministic sub-systems (NetworkNPC wander, NetworkWaypoint paths)
    /// can anchor to a single scene-wide timestamp every client agrees on,
    /// rather than each client stamping its own local join time. 0 until the
    /// first SCENE_INIT.
    pub scene_ready_ms: u64,

    /// Per-object record of the most recent CTRL_RELEASE broadcast bytes.
    /// Replayed to late joiners right after CTRL_PHYSICS_READY so their
    /// deterministic subsystems (NetworkNPC wander epoch) land on the same
    /// authoritative anchor existing clients are already using. Without this,
    /// a late joiner computes wander from scene_ready_ms while the others
    /// are anchored to LastReleaseServerMs — and NPCs appear at different
    /// positions on their screen until the next chase cycle re-syncs them.
    ///
    /// Keyed by object local_id, value is the full 34-byte CTRL_RELEASE
    /// frame so replay just calls `send(new_client, bytes)` — the client
    /// already has the handler for it, no new protocol surface.
    pub last_release_bytes: HashMap<u16, Vec<u8>>,
}

impl Room {
    pub const MAX_CLIENTS: usize = 64;
    pub fn new() -> Self {
        Self {
            clients: HashMap::new(),
            next_id: 1,
            scene_initializing: false,
            claimed_objects: HashMap::new(),
            state_writer_id: 0,
            scene_fingerprint: 0,
            variable_state: VariableState::default(),
            state_epoch: 0,
            player_ids: HashMap::new(),
            player_positions: HashMap::new(),
            physics_ready: false,
            physics_world: None,
            scene_object_count: 0,
            next_runtime_id: 0,
            runtime_spawn_count: 0,
            runtime_spawns: HashMap::new(),
            runtime_spawn_owners: HashMap::new(),
            scene_ready_ms: 0,
            last_release_bytes: HashMap::new(),
        }
    }

    /// Add a client to the room. Returns the client_id.
    /// IDs are assigned from 1..=65535 with wraparound, skipping any ID still in use.
    pub fn add_client(&mut self, client: Arc<Client>) -> u16 {
        let start = self.next_id;
        loop {
            self.next_id = self.next_id.wrapping_add(1);
            if self.next_id == 0 {
                self.next_id = 1; // skip 0 — reserved as "server"
            }
            if !self.clients.contains_key(&self.next_id) {
                break;
            }
            if self.next_id == start {
                // All 65535 slots occupied — practically impossible.
                break;
            }
        }
        let id = self.next_id;
        self.clients.insert(id, client);
        if self.state_writer_id == 0 {
            self.state_writer_id = id;
        }
        id
    }

    /// Remove a client. Returns any object IDs that were released as a result
    /// so the caller can tell Rapier to switch them back to Dynamic.
    pub fn remove_client(&mut self, id: u16) -> Vec<u16> {
        self.clients.remove(&id);
        if self.state_writer_id == id {
            self.state_writer_id = self.clients.keys().copied().min().unwrap_or(0);
        }
        self.player_ids.remove(&id);
        self.player_positions.remove(&id);

        let released: Vec<u16> = self
            .claimed_objects
            .iter()
            .filter_map(|(&obj, &owner)| if owner == id { Some(obj) } else { None })
            .collect();
        for obj in &released {
            self.claimed_objects.remove(obj);
        }
        if !released.is_empty() {
            self.state_epoch = self.state_epoch.wrapping_add(1);
        }
        released
    }

    /// Number of connected clients.
    pub fn client_count(&self) -> usize {
        self.clients.len()
    }
}
