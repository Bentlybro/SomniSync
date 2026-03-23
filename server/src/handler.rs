use log::{debug, error, info, warn};
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::RwLock;
use tokio_tungstenite::tungstenite::Message;

use crate::client::{broadcast_except, send, Client};
use crate::room::RoomMap;
use crate::protocol::{
    ctrl_msg, ctrl_msg_u16, CTRL_CLAIM, CTRL_MASTER_CHANGED, CTRL_PLAYER_POSITION,
    CTRL_RELEASE, CTRL_SEND_SNAPSHOT, CTRL_YOU_ARE_MASTER, CTRL_YOU_ARE_SPECTATOR,
    MSG_ANIMATOR, MSG_EVENT, MSG_INTERACTION_UPDATE, MSG_POSITION_UPDATE,
    MSG_POSITION_UPDATE_SCALED, MSG_RIGIDBODY, MSG_SCALED_INTERACTION, MSG_SNAPSHOT,
    MSG_VARIABLE_UPDATE,
};
use crate::room::Room;

/// Handle a single client connection (runs in its own task).
pub async fn handle_connection(
    stream: tokio::net::TcpStream,
    addr: SocketAddr,
    rooms: RoomMap,
) {
    // Extract room_id from the WebSocket request URI path via callback
    let room_id = Arc::new(tokio::sync::Mutex::new(String::from("default")));
    let room_id_clone = room_id.clone();

    let callback = move |req: &tokio_tungstenite::tungstenite::handshake::server::Request,
                         resp: tokio_tungstenite::tungstenite::handshake::server::Response|
        -> Result<tokio_tungstenite::tungstenite::handshake::server::Response, tokio_tungstenite::tungstenite::http::Response<Option<String>>> {
        let path = req.uri().path().trim_start_matches('/').to_string();
        if !path.is_empty() {
            // Use try_lock since we're in a sync callback
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

    // Get or create room
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

    // Register client
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

    // Send role assignment
    if is_master {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_MASTER, client_id)).await;
    } else {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_SPECTATOR, client_id)).await;
        send_cached_snapshot(&room, &client, client_id, &room_id).await;
    }

    // Message loop
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
                handle_binary(&room, client_id, &data, &room_id).await;
            }
            Message::Close(_) => break,
            Message::Ping(d) => {
                send(&client, Message::Pong(d).into_data()).await;
            }
            _ => {}
        }
    }

    // Client disconnected
    handle_disconnect(&room, client_id, &room_id).await;

    // Cleanup empty room
    {
        let r = room.read().await;
        if r.client_count() == 0 {
            drop(r);
            let mut all_rooms = rooms.write().await;
            // Double-check after acquiring write lock
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
async fn handle_binary(room: &Arc<RwLock<Room>>, client_id: u16, data: &[u8], room_id: &str) {
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

        MSG_INTERACTION_UPDATE | MSG_VARIABLE_UPDATE | MSG_POSITION_UPDATE_SCALED
        | MSG_EVENT | MSG_ANIMATOR | MSG_RIGIDBODY | MSG_SCALED_INTERACTION => {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data).await;
        }

        0xFF => {
            handle_control(room, client_id, data, room_id).await;
        }

        _ => {
            warn!(
                "[Room:{}] Unknown message type 0x{:02X} from client_{}",
                room_id, msg_type, client_id
            );
        }
    }
}

/// Handle control messages (0xFF) — claim/release.
async fn handle_control(room: &Arc<RwLock<Room>>, client_id: u16, data: &[u8], room_id: &str) {
    if data.len() < 2 {
        return;
    }
    let ctrl = data[1];

    // Player position only needs 2 byte header check, not 6
    if ctrl == CTRL_PLAYER_POSITION {
        if data.len() >= 10 {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data).await;
        }
        return;
    }

    if data.len() < 6 {
        return;
    }
    let obj_id = u16::from_le_bytes([data[2], data[3]]);
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
