use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::RwLock;

use crate::client::Client;

/// All rooms keyed by room_id string.
pub type RoomMap = Arc<RwLock<HashMap<String, Arc<RwLock<Room>>>>>;

/// Room state — tracks all connected clients, master, ownership, and snapshots.
pub struct Room {
    pub clients: HashMap<u16, Arc<Client>>,
    pub master_id: Option<u16>,
    next_id: u16,

    /// Cached latest snapshot from master (for late-joiner instant sync)
    pub latest_snapshot: Option<Vec<u8>>,

    /// Object ownership: object_local_id → client_id (only for claimed objects)
    pub claimed_objects: HashMap<u16, u16>,

    /// Stable player identity: client_id → Somnium player UUID (or fallback UUID)
    pub player_ids: HashMap<u16, String>,
}

impl Room {
    pub fn new() -> Self {
        Self {
            clients: HashMap::new(),
            master_id: None,
            next_id: 1,
            latest_snapshot: None,
            claimed_objects: HashMap::new(),
            player_ids: HashMap::new(),
        }
    }

    /// Add a client to the room. Returns (client_id, is_master).
    pub fn add_client(&mut self, client: Arc<Client>) -> (u16, bool) {
        let id = self.next_id;
        self.next_id += 1;
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
