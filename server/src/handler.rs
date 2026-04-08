use log::{debug, error, info, warn};
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::RwLock;
use tokio_tungstenite::tungstenite::Message;

use crate::client::{broadcast_except, send, Client};
use crate::persistence::PersistenceDb;
use crate::protocol::{
    ctrl_msg, ctrl_msg_u16, CTRL_AUTHORITY_ASSIGN, CTRL_CLAIM, CTRL_MASTER_CHANGED,
    CTRL_PERSIST_DATA, CTRL_PERSIST_GET, CTRL_PERSIST_SET, CTRL_PLAYER_IDENT,
    CTRL_PLAYER_POSITION, CTRL_RELEASE, CTRL_SEND_SNAPSHOT, CTRL_YOU_ARE_MASTER,
    CTRL_YOU_ARE_SPECTATOR, MSG_ANIMATOR, MSG_EVENT, MSG_INTERACTION_UPDATE,
    MSG_POSITION_UPDATE, MSG_POSITION_UPDATE_SCALED, MSG_POSITION_VELOCITY, MSG_RIGIDBODY,
    MSG_SCALED_INTERACTION, MSG_SNAPSHOT, MSG_SNAPSHOT_SCALED, MSG_VARIABLE_UPDATE,
};
use crate::room::Room;
use crate::room::RoomMap;

/// Handle a single client connection (runs in its own task).
pub async fn handle_connection(
    stream: tokio::net::TcpStream,
    addr: SocketAddr,
    rooms: RoomMap,
    db: PersistenceDb,
) {
    let room_id = Arc::new(tokio::sync::Mutex::new(String::from("default")));
    let room_id_clone = room_id.clone();

    let callback = move |req: &tokio_tungstenite::tungstenite::handshake::server::Request,
                         resp: tokio_tungstenite::tungstenite::handshake::server::Response|
        -> Result<tokio_tungstenite::tungstenite::handshake::server::Response, tokio_tungstenite::tungstenite::http::Response<Option<String>>> {
        let path = req.uri().path().trim_start_matches('/').to_string();
        if !path.is_empty() {
            if let Ok(mut id) = room_id_clone.try_lock() {
                *id = path;
            }
        }
        Ok(resp)
    };

    let ws = match tokio_tungstenite::accept_hdr_async(stream, callback).await {
        Ok(ws) => ws,
        Err(e) => {
            error!("[Server] WebSocket handshake failed for {}: {}", addr, e);
            return;
        }
    };

    let room_id = room_id.lock().await.clone();

    let room = {
        let mut all_rooms = rooms.write().await;
        all_rooms
            .entry(room_id.clone())
            .or_insert_with(|| {
                info!("[Rooms] Created room '{}'", room_id);
                Arc::new(RwLock::new(Room::new()))
            })
            .clone()
    };

    let (tx, mut rx) = futures_util::StreamExt::split(ws);
    let client = Arc::new(Client {
        tx: tokio::sync::Mutex::new(tx),
        addr,
    });

    let (client_id, is_master) = {
        let mut r = room.write().await;
        let (id, is_master) = r.add_client(client.clone());
        info!(
            "[Room:{}] Client {} connected: client_{} ({} total) [{}]",
            room_id, addr, id, r.client_count(),
            if is_master { "MASTER" } else { "SPECTATOR" }
        );
        (id, is_master)
    };

    if is_master {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_MASTER, client_id)).await;
    } else {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_SPECTATOR, client_id)).await;
        send_cached_snapshot(&room, &client, client_id, &room_id).await;
    }

    while let Some(msg) = futures_util::StreamExt::next(&mut rx).await {
        let msg = match msg {
            Ok(m) => m,
            Err(e) => {
                warn!("[Room:{}] Error from client_{}: {}", room_id, client_id, e);
                break;
            }
        };

        match msg {
            Message::Binary(data) => {
                if data.is_empty() {
                    continue;
                }
                handle_binary(&room, &client, client_id, &data, &room_id, &db).await;
            }
            Message::Close(_) => break,
            Message::Ping(d) => {
                use futures_util::SinkExt;
                let mut tx = client.tx.lock().await;
                let _ = tx.send(Message::Pong(d)).await;
            }
            _ => {}
        }
    }

    handle_disconnect(&room, client_id, &room_id).await;

    {
        let r = room.read().await;
        if r.client_count() == 0 {
            drop(r);
            let mut all_rooms = rooms.write().await;
            if let Some(r) = all_rooms.get(&room_id) {
                if r.read().await.client_count() == 0 {
                    all_rooms.remove(&room_id);
                    info!("[Rooms] Destroyed empty room '{}'", room_id);
                }
            }
        }
    }
}

/// Send cached snapshot to a newly connected spectator + request fresh from master.
async fn send_cached_snapshot(room: &Arc<RwLock<Room>>, client: &Client, client_id: u16, room_id: &str) {
    let room = room.read().await;
    if let Some(ref snapshot) = room.latest_snapshot {
        info!(
            "[Room:{}] Sending cached snapshot to client_{} ({} bytes)",
            room_id, client_id, snapshot.len()
        );
        send(client, snapshot.clone()).await;
    }
    if let Some(master_id) = room.master_id {
        if let Some(master) = room.clients.get(&master_id) {
            info!(
                "[Room:{}] Requesting fresh snapshot from master (client_{}) for client_{}",
                room_id, master_id, client_id
            );
            send(master, ctrl_msg_u16(CTRL_SEND_SNAPSHOT, client_id)).await;
        }
    }
}

/// Handle a binary message from a client.
async fn handle_binary(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    let msg_type = data[0];

    match msg_type {
        MSG_POSITION_UPDATE => {
            let room = room.read().await;
            if room.master_id != Some(client_id) {
                return;
            }
            broadcast_except(&room.clients, client_id, data).await;
        }

        MSG_SNAPSHOT => {
            let mut room = room.write().await;
            if room.master_id != Some(client_id) {
                return;
            }
            info!(
                "[Room:{}] Received snapshot from master ({} bytes), caching",
                room_id, data.len()
            );
            room.latest_snapshot = Some(data.to_vec());
            broadcast_except(&room.clients, client_id, data).await;
        }

        MSG_POSITION_VELOCITY => {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data).await;
        }

        MSG_INTERACTION_UPDATE | MSG_VARIABLE_UPDATE | MSG_POSITION_UPDATE_SCALED
        | MSG_SNAPSHOT_SCALED | MSG_EVENT | MSG_ANIMATOR | MSG_RIGIDBODY
        | MSG_SCALED_INTERACTION => {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data).await;
        }

        0xFF => {
            handle_control(room, client, client_id, data, room_id, db).await;
        }

        _ => {
            warn!(
                "[Room:{}] Unknown message type 0x{:02X} from client_{}",
                room_id, msg_type, client_id
            );
        }
    }
}

/// Handle control messages (0xFF).
async fn handle_control(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    if data.len() < 2 {
        return;
    }
    let ctrl = data[1];

    // --- Player position: broadcast as-is ---
    if ctrl == CTRL_PLAYER_POSITION {
        if data.len() >= 10 {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data).await;
        }
        return;
    }

    // --- Authority assignment: broadcast as-is ---
    if ctrl == CTRL_AUTHORITY_ASSIGN {
        if data.len() >= 6 {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data).await;
        }
        return;
    }

    // --- Persistence: player identity registration (once per connection only) ---
    if ctrl == CTRL_PLAYER_IDENT {
        // [0xFF][0x20][u8 len][utf8 uuid...]
        {
            let room_r = room.read().await;
            if room_r.player_ids.contains_key(&client_id) {
                warn!("[Room:{}] client_{} tried to re-register identity — ignored", room_id, client_id);
                return;
            }
        }
        if data.len() >= 4 {
            let len = data[2] as usize;
            const MAX_ID_LEN: usize = 64;
            if len > MAX_ID_LEN { return; }
            if data.len() >= 3 + len {
                if let Ok(uuid) = std::str::from_utf8(&data[3..3 + len]) {
                    let uuid = uuid.to_string();
                    let mut room = room.write().await;
                    room.player_ids.insert(client_id, uuid.clone());
                    info!("[Room:{}] client_{} identified as player '{}'", room_id, client_id, uuid);
                }
            }
        }
        return;
    }

    // --- Persistence: write key/value ---
    if ctrl == CTRL_PERSIST_SET {
        // [0xFF][0x21][u8 ns_len][ns...][u8 key_len][key...][u16 val_len][val...]
        handle_persist_set(room, client_id, data, room_id, db).await;
        return;
    }

    // --- Persistence: read key ---
    if ctrl == CTRL_PERSIST_GET {
        // [0xFF][0x22][u16 req_id][u8 ns_len][ns...][u8 key_len][key...]
        handle_persist_get(room, client, client_id, data, room_id, db).await;
        return;
    }

    // Remaining messages need at least 6 bytes (obj_id + sender_id)
    if data.len() < 6 {
        return;
    }
    let obj_id    = u16::from_le_bytes([data[2], data[3]]);
    let sender_id = u16::from_le_bytes([data[4], data[5]]);

    match ctrl {
        CTRL_CLAIM => {
            let mut room = room.write().await;
            room.claimed_objects.insert(obj_id, sender_id);
            debug!("[Room:{}] Client_{} claimed object {}", room_id, sender_id, obj_id);
            broadcast_except(&room.clients, client_id, data).await;
        }
        CTRL_RELEASE => {
            let mut room = room.write().await;
            room.claimed_objects.remove(&obj_id);
            debug!("[Room:{}] Client_{} released object {}", room_id, sender_id, obj_id);
            broadcast_except(&room.clients, client_id, data).await;
        }
        _ => {}
    }
}

/// Handle CTRL_PERSIST_SET — write a value to the database.
async fn handle_persist_set(
    room: &Arc<RwLock<Room>>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    // [0xFF][0x21][u8 ns_len][ns...][u8 key_len][key...][u16 val_len][val...]
    let mut offset = 2usize;
    if data.len() < offset + 1 { return; }

    let ns_len = data[offset] as usize; offset += 1;
    if data.len() < offset + ns_len { return; }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 { return; }
    let key_len = data[offset] as usize; offset += 1;
    if data.len() < offset + key_len { return; }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };
    offset += key_len;

    if data.len() < offset + 2 { return; }
    let val_len = u16::from_le_bytes([data[offset], data[offset + 1]]) as usize; offset += 2;
    const MAX_VALUE_LEN: usize = 4096;
    if val_len > MAX_VALUE_LEN {
        warn!("[Room:{}] Persist SET from client_{} exceeded max value size ({} bytes)", room_id, client_id, val_len);
        return;
    }
    if data.len() < offset + val_len { return; }
    let value = match std::str::from_utf8(&data[offset..offset + val_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    if let Some(pid) = player_id {
        debug!("[Room:{}] Persist SET player='{}' ns='{}' key='{}'", room_id, pid, namespace, key);
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.set(&pid, &namespace, &key, &value)).await.ok();
    } else {
        warn!("[Room:{}] Persist SET from client_{} with no registered player ID", room_id, client_id);
    }
}

/// Handle CTRL_PERSIST_GET — read a value and send it back to the requesting client.
async fn handle_persist_get(
    room: &Arc<RwLock<Room>>,
    client: &Arc<Client>,
    client_id: u16,
    data: &[u8],
    room_id: &str,
    db: &PersistenceDb,
) {
    // [0xFF][0x22][u16 req_id][u8 ns_len][ns...][u8 key_len][key...]
    let mut offset = 2usize;
    if data.len() < offset + 2 { return; }
    let req_id = u16::from_le_bytes([data[offset], data[offset + 1]]); offset += 2;

    if data.len() < offset + 1 { return; }
    let ns_len = data[offset] as usize; offset += 1;
    if data.len() < offset + ns_len { return; }
    let namespace = match std::str::from_utf8(&data[offset..offset + ns_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };
    offset += ns_len;

    if data.len() < offset + 1 { return; }
    let key_len = data[offset] as usize; offset += 1;
    if data.len() < offset + key_len { return; }
    let key = match std::str::from_utf8(&data[offset..offset + key_len]) {
        Ok(s) => s.to_string(), Err(_) => return,
    };

    let player_id = {
        let room = room.read().await;
        room.player_ids.get(&client_id).cloned()
    };

    let value = if let Some(pid) = player_id {
        debug!("[Room:{}] Persist GET player='{}' ns='{}' key='{}'", room_id, pid, namespace, key);
        let db = db.clone();
        tokio::task::spawn_blocking(move || db.get(&pid, &namespace, &key))
            .await
            .unwrap_or(None)
    } else {
        warn!("[Room:{}] Persist GET from client_{} with no registered player ID", room_id, client_id);
        None
    };

    // Build response: [0xFF][0x23][u16 req_id][u8 found][u16 val_len][val...]
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

/// Handle client disconnect — cleanup and promote new master if needed.
async fn handle_disconnect(room: &Arc<RwLock<Room>>, client_id: u16, room_id: &str) {
    let new_master = {
        let mut room = room.write().await;
        let new_master = room.remove_client(client_id);
        info!(
            "[Room:{}] Client_{} disconnected ({} remaining)",
            room_id, client_id, room.client_count()
        );
        new_master
    };

    if let Some(new_master_id) = new_master {
        let room = room.read().await;
        if let Some(new_master_client) = room.clients.get(&new_master_id) {
            info!("[Room:{}] Promoting client_{} to master", room_id, new_master_id);
            send(
                new_master_client,
                ctrl_msg(CTRL_MASTER_CHANGED, &new_master_id.to_le_bytes()),
            )
            .await;
        }
    }
}
