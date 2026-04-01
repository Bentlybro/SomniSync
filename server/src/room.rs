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

    /// Cached latest scaled snapshot from master (for late-joiner instant sync)
    pub latest_scaled_snapshot: Option<Vec<u8>>,

    /// Object ownership: object_local_id → client_id (only for claimed objects)
    pub claimed_objects: HashMap<u16, u16>,
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

    /// Remove a client. Returns (new_master_id, released_object_ids).
    /// new_master_id is Some if the removed client was master and a successor was promoted.
    /// released_object_ids lists every object that was claimed by the removed client so the
    /// caller can broadcast CTRL_RELEASE to remaining peers.
    pub fn remove_client(&mut self, id: u16) -> (Option<u16>, Vec<u16>) {
        self.clients.remove(&id);

        // Collect claimed objects BEFORE clearing them — caller needs the list to notify peers.
        let released: Vec<u16> = self.claimed_objects.iter()
            .filter(|(_, &owner)| owner == id)
            .map(|(obj_id, _)| *obj_id)
            .collect();
        self.claimed_objects.retain(|_, owner| *owner != id);

        let new_master = if self.master_id == Some(id) {
            self.master_id = None;
            self.latest_snapshot = None;
            self.latest_scaled_snapshot = None;
            // Promote next client
            if let Some(&new_master) = self.clients.keys().next() {
                self.master_id = Some(new_master);
                Some(new_master)
            } else {
                None
            }
        } else {
            None
        };

        (new_master, released)
    }

    /// Number of connected clients.
    pub fn client_count(&self) -> usize {
        self.clients.len()
    }
}
