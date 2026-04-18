use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::{Mutex, RwLock};

use crate::client::Client;
use crate::physics::PhysicsWorld;

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

    /// Object ownership: object_local_id → client_id. Held objects ("grabs")
    /// are driven by that client; the server makes their physics body kinematic
    /// while claimed.
    pub claimed_objects: HashMap<u16, u16>,

    /// Stable player identity: client_id → Somnium player UUID (or fallback UUID)
    pub player_ids: HashMap<u16, String>,

    /// Latest known player head/body position per client. Used by server AOI to
    /// filter outgoing position broadcasts. Absent until the client reports one.
    pub player_positions: HashMap<u16, [f32; 3]>,

    /// True once the physics world has been initialized by a SCENE_INIT.
    pub physics_ready: bool,

    /// The authoritative physics world — present only after SCENE_INIT.
    pub physics_world: Option<Arc<Mutex<PhysicsWorld>>>,

    /// Next local_id to hand out for runtime-spawned NetworkObjects. Set by
    /// SCENE_INIT to the initial object count; increments on each CTRL_SPAWN.
    /// IDs are never reused even after despawn (no holes in the assignment
    /// sequence) — u16 gives 65k spawns per room, plenty in practice.
    pub next_runtime_id: u16,
}

impl Room {
    pub fn new() -> Self {
        Self {
            clients: HashMap::new(),
            next_id: 1,
            claimed_objects: HashMap::new(),
            player_ids: HashMap::new(),
            player_positions: HashMap::new(),
            physics_ready: false,
            physics_world: None,
            next_runtime_id: 0,
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
        id
    }

    /// Remove a client. Returns any object IDs that were released as a result
    /// so the caller can tell Rapier to switch them back to Dynamic.
    pub fn remove_client(&mut self, id: u16) -> Vec<u16> {
        self.clients.remove(&id);
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
        released
    }

    /// Number of connected clients.
    pub fn client_count(&self) -> usize {
        self.clients.len()
    }
}
