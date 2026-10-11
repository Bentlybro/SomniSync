//! On-wire persistence protocol handlers — parse the CTRL_PLAYER_IDENT /
//! CTRL_PERSIST_SET / CTRL_PERSIST_GET control messages, apply size/UTF-8
//! validation, then delegate storage to `crate::persistence::PersistenceDb`.
//!
//! Split out of handler.rs so the persistence wire format lives next to
//! the DB module conceptually, even though the two files are separate.
//! (`persistence.rs` is the DB wrapper; `persist_rpc.rs` is the message
//! protocol on top of it.)

use std::sync::Arc;

use log::{debug, info, warn};
use tokio::sync::RwLock;

use crate::client::{send, Client};
use crate::persistence::PersistenceDb;
use crate::protocol::{ctrl_msg, read_u16, CTRL_ERROR, CTRL_PERSIST_DATA};
use crate::room::Room;

pub(crate) fn unauth_persistence_allowed() -> bool {
    matches!(
        std::env::var("SOMNISYNC_ALLOW_UNAUTH_PERSISTENCE")
            .unwrap_or_default()
            .to_ascii_lowercase()
            .as_str(),
        "1" | "true" | "yes"
    )
}

pub(crate) async fn handle_player_ident(
    room: &Arc<RwLock<Room>>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
) {
    if !unauth_persistence_allowed() {
        warn!(
            "[Room:{}] client_{} attempted unauthenticated persistence identity registration; set SOMNISYNC_ALLOW_UNAUTH_PERSISTENCE=1 only for trusted/dev relays",
            room_id, client_id
        );
        return;
    }

    {
        let room_r = room.read().await;
        if room_r.player_ids.contains_key(&client_id) {
            warn!(
                "[Room:{}] client_{} tried to re-register identity — ignored",
                room_id, client_id
            );
            return;
        }
    }
    if data.len() < 4 {
        return;
    }
    let len = data[2] as usize;
    const MAX_ID_LEN: usize = 64;
    if len > MAX_ID_LEN || data.len() < 3 + len {
        return;
    }
    if let Ok(uuid) = std::str::from_utf8(&data[3..3 + len]) {
        let uuid = uuid.to_string();
        let mut room = room.write().await;
        room.player_ids.insert(client_id, uuid.clone());
        info!(
            "[Room:{}] client_{} registered persistence identity",
            room_id, client_id
        );
    }
}

pub(crate) async fn handle_persist_set(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    if !unauth_persistence_allowed() {
        warn!(
            "[Room:{}] Persist SET from client_{} rejected: unauthenticated persistence disabled",
            room_id, client_id
        );
        send(client, ctrl_msg(CTRL_ERROR, b"persistence_auth_required")).await;
        return;
    }

    let mut offset = 2usize;
    if data.len() < offset + 1 {
        return;
    }

    const MAX_NS_LEN: usize = 64;
    const MAX_KEY_LEN: usize = 128;
    const MAX_VALUE_LEN: usize = 4096;

    let ns_len = data[offset] as usize;
    offset += 1;
    if ns_len > MAX_NS_LEN || data.len() < offset + ns_len {
        return;
    }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 {
        return;
    }
    let key_len = data[offset] as usize;
    offset += 1;
    if key_len > MAX_KEY_LEN || data.len() < offset + key_len {
        return;
    }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };
    offset += key_len;

    if data.len() < offset + 2 {
        return;
    }
    let val_len = read_u16(data, offset) as usize;
    offset += 2;
    if val_len > MAX_VALUE_LEN {
        warn!(
            "[Room:{}] Persist SET from client_{} exceeded max value size ({} bytes)",
            room_id, client_id, val_len
        );
        send(client, ctrl_msg(CTRL_ERROR, b"persist_value_too_large")).await;
        return;
    }
    if data.len() < offset + val_len {
        return;
    }
    let value = match std::str::from_utf8(&data[offset..offset + val_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    if let Some(pid) = player_id {
        debug!(
            "[Room:{}] Persist SET from client_{} (ns={}B key={}B)",
            room_id,
            client_id,
            namespace.len(),
            key.len()
        );
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.set(&pid, &namespace, &key, &value))
            .await
            .ok();
    } else {
        warn!(
            "[Room:{}] Persist SET from client_{} with no registered player ID",
            room_id, client_id
        );
    }
}

pub(crate) async fn handle_persist_get(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    if !unauth_persistence_allowed() {
        warn!(
            "[Room:{}] Persist GET from client_{} rejected: unauthenticated persistence disabled",
            room_id, client_id
        );
        send(client, ctrl_msg(CTRL_ERROR, b"persistence_auth_required")).await;
        return;
    }

    let mut offset = 2usize;
    if data.len() < offset + 2 {
        return;
    }
    let req_id = read_u16(data, offset);
    offset += 2;

    const MAX_NS_LEN: usize = 64;
    const MAX_KEY_LEN: usize = 128;

    if data.len() < offset + 1 {
        return;
    }
    let ns_len = data[offset] as usize;
    offset += 1;
    if ns_len > MAX_NS_LEN || data.len() < offset + ns_len {
        return;
    }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 {
        return;
    }
    let key_len = data[offset] as usize;
    offset += 1;
    if key_len > MAX_KEY_LEN || data.len() < offset + key_len {
        return;
    }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(),
        Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    let value = if let Some(pid) = player_id {
        debug!(
            "[Room:{}] Persist GET from client_{} (ns={}B key={}B)",
            room_id,
            client_id,
            namespace.len(),
            key.len()
        );
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.get(&pid, &namespace, &key))
            .await
            .unwrap_or(None)
    } else {
        warn!(
            "[Room:{}] Persist GET from client_{} with no registered player ID",
            room_id, client_id
        );
        None
    };

    let found = value.is_some();
    let val_bytes = value.as_deref().unwrap_or("").as_bytes();
    let mut resp = Vec::with_capacity(7 + val_bytes.len());
    resp.push(0xFF);
    resp.push(CTRL_PERSIST_DATA);
    resp.extend_from_slice(&req_id.to_le_bytes());
    resp.push(if found { 1 } else { 0 });
    resp.extend_from_slice(&(val_bytes.len() as u16).to_le_bytes());
    resp.extend_from_slice(val_bytes);

    send(client, resp).await;
}
