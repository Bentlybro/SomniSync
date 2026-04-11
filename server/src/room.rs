use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::{Mutex, RwLock};

use crate::client::Client;
use crate::physics::PhysicsWorld;

/// All rooms keyed by room_id string.
pub type RoomMap = Arc<RwLock<HashMap<String, Arc<RwLock<Room>>>>>;

/// Room state — tracks all connected clients, master, ownership, and snapshots.
pub struct Room {
    pub clients: HashMap<u16, Arc<Client>>,
    pub master_id: Option<u16>,
    next_id: u16,

    /// Cached latest snapshot from master (for late-joiner instant sync)
    pub latest_snapshot: Option<Vec<u8>>,

    /// Cached latest scaled-object snapshot (MSG_SNAPSHOT_SCALED 0x0A)
    pub latest_scaled_snapshot: Option<Vec<u8>>,

    /// Object ownership: object_local_id → client_id (only for claimed objects)
    pub claimed_objects: HashMap<u16, u16>,

    /// Stable player identity: client_id → Somnium player UUID (or fallback UUID)
    pub player_ids: HashMap<u16, String>,

    /// True when the server is running Rapier3D physics (SCENE_INIT received).
    pub physics_mode: bool,

    /// Last known player positions for AOI filtering in physics mode.
    /// client_id → [x, y, z]
    pub player_positions: HashMap<u16, [f32; 3]>,

    /// The authoritative physics world — present only in physics_mode.
    pub physics_world: Option<Arc<Mutex<PhysicsWorld>>>,
}

impl Room {
    pub fn new() -> Self {
        Self {
            clients: HashMap::new(),
            master_id: None,
            next_id: 1,
            latest_snapshot: None,
            latest_scaled_snapshot: None,
            claimed_objects: HashMap::new(),
            player_ids: HashMap::new(),
            physics_mode: false,
            player_positions: HashMap::new(),
            physics_world: None,
        }
    }

    /// Add a client to the room. Returns (client_id, is_master).
    /// IDs are assigned from 1..=65535 with wraparound, skipping any ID still in use.
    pub fn add_client(&mut self, client: Arc<Client>) -> (u16, bool) {
        let start = self.next_id;
        loop {
            self.next_id = self.next_id.wrapping_add(1);
            if self.next_id == 0 {
                self.next_id = 1; // skip 0 — reserved as "no client"
            }
            if !self.clients.contains_key(&self.next_id) {
                break;
            }
            if self.next_id == start {
                // All 65535 slots occupied — practically impossible, but don't spin forever.
                break;
            }
        }
        let id = self.next_id;
        let is_master = self.master_id.is_none();
        if is_master {
            self.master_id = Some(id);
        }
        self.clients.insert(id, client);
        (id, is_master)
    }

    /// Remove a client. Returns the new master id if promotion happened.
    pub fn remove_client(&mut self, id: u16) -> Option<u16> {
        self.clients.remove(&id);
        self.claimed_objects.retain(|_, owner| *owner != id);
        self.player_ids.remove(&id);

        if self.master_id == Some(id) {
            self.master_id = None;
            self.latest_snapshot = None;
            self.latest_scaled_snapshot = None;
            if let Some(&new_master) = self.clients.keys().next() {
                self.master_id = Some(new_master);
                return Some(new_master);
            }
        }
        None
    }

    /// Number of connected clients.
    pub fn client_count(&self) -> usize {
        self.clients.len()
    }
}
