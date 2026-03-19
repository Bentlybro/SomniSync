use log::{debug, error, info, warn};
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::RwLock;
use tokio_tungstenite::tungstenite::Message;

use crate::client::{broadcast_except, send, Client};
use crate::protocol::*;
use crate::room::Room;

/// Handle a single client connection (runs in its own task).
pub async fn handle_connection(
    stream: tokio::net::TcpStream,
    addr: SocketAddr,
    room: Arc<RwLock<Room>>,
) {
    let ws = match tokio_tungstenite::accept_async(stream).await {
        Ok(ws) => ws,
        Err(e) => {
            error!("[Server] WebSocket handshake failed for {}: {}", addr, e);
            return;
        }
    };

    let (tx, mut rx) = futures_util::StreamExt::split(ws);
    let client = Arc::new(Client {
        tx: tokio::sync::Mutex::new(tx),
        addr,
    });

    // Register client
    let (client_id, is_master) = {
        let mut room = room.write().await;
        let (id, is_master) = room.add_client(client.clone());
        info!(
            "[Room] Client {} connected: client_{} ({} total) [{}]",
            addr,
            id,
            room.client_count(),
            if is_master { "MASTER" } else { "SPECTATOR" }
        );
        (id, is_master)
    };

    // Send role assignment
    if is_master {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_MASTER, client_id)).await;
    } else {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_SPECTATOR, client_id)).await;
        send_cached_snapshot(&room, &client, client_id).await;
    }

    // Message loop
    while let Some(msg) = futures_util::StreamExt::next(&mut rx).await {
        let msg = match msg {
            Ok(m) => m,
            Err(e) => {
                warn!("[Room] Error from client_{}: {}", client_id, e);
                break;
            }
        };

        match msg {
            Message::Binary(data) => {
                if data.is_empty() {
                    continue;
                }
                handle_binary(&room, client_id, &data).await;
            }
            Message::Close(_) => break,
            Message::Ping(d) => {
                send(&client, Message::Pong(d).into_data()).await;
            }
            _ => {} // Ignore text messages
        }
    }

    // Client disconnected
    handle_disconnect(&room, client_id).await;
}

/// Send cached snapshot to a newly connected spectator + request fresh from master.
async fn send_cached_snapshot(room: &Arc<RwLock<Room>>, client: &Client, client_id: u16) {
    let room = room.read().await;
    if let Some(ref snapshot) = room.latest_snapshot {
        info!(
            "[Room] Sending cached snapshot to client_{} ({} bytes)",
            client_id,
            snapshot.len()
        );
        send(client, snapshot.clone()).await;
    }
    // Request fresh snapshot from master
    if let Some(master_id) = room.master_id {
        if let Some(master) = room.clients.get(&master_id) {
            info!(
                "[Room] Requesting fresh snapshot from master (client_{}) for client_{}",
                master_id, client_id
            );
            send(master, ctrl_msg_u16(CTRL_SEND_SNAPSHOT, client_id)).await;
        }
    }
}

/// Handle a binary message from a client.
async fn handle_binary(room: &Arc<RwLock<Room>>, client_id: u16, data: &[u8]) {
    let msg_type = data[0];

    match msg_type {
        // Position updates from master → broadcast to all spectators
        MSG_POSITION_UPDATE => {
            let room = room.read().await;
            if room.master_id != Some(client_id) {
                return;
            }
            broadcast_except(&room.clients, client_id, data).await;
        }

        // Snapshot from master → cache + forward to all spectators
        MSG_SNAPSHOT => {
            let mut room = room.write().await;
            if room.master_id != Some(client_id) {
                return;
            }
            info!(
                "[Room] Received snapshot from master ({} bytes), caching",
                data.len()
            );
            room.latest_snapshot = Some(data.to_vec());
            broadcast_except(&room.clients, client_id, data).await;
        }

        // Interaction, variable, event, animator, rigidbody → broadcast to all others
        MSG_INTERACTION_UPDATE | MSG_VARIABLE_UPDATE | MSG_EVENT | MSG_ANIMATOR | MSG_RIGIDBODY => {
            let room = room.read().await;
            broadcast_except(&room.clients, client_id, data).await;
        }

        // Control messages from clients (claim/release)
        0xFF => {
            handle_control(room, client_id, data).await;
        }

        _ => {
            warn!(
                "[Room] Unknown message type 0x{:02X} from client_{}",
                msg_type, client_id
            );
        }
    }
}

/// Handle control messages (0xFF) — claim/release.
async fn handle_control(room: &Arc<RwLock<Room>>, client_id: u16, data: &[u8]) {
    if data.len() < 6 {
        return;
    }
    let ctrl = data[1];
    let obj_id = u16::from_le_bytes([data[2], data[3]]);
    let sender_id = u16::from_le_bytes([data[4], data[5]]);

    match ctrl {
        CTRL_CLAIM => {
            let mut room = room.write().await;
            room.claimed_objects.insert(obj_id, sender_id);
            debug!("[Room] Client_{} claimed object {}", sender_id, obj_id);
            broadcast_except(&room.clients, client_id, data).await;
        }
        CTRL_RELEASE => {
            let mut room = room.write().await;
            room.claimed_objects.remove(&obj_id);
            debug!("[Room] Client_{} released object {}", sender_id, obj_id);
            broadcast_except(&room.clients, client_id, data).await;
        }
        _ => {} // Ignore other control from clients
    }
}

/// Handle client disconnect — cleanup and promote new master if needed.
async fn handle_disconnect(room: &Arc<RwLock<Room>>, client_id: u16) {
    let new_master = {
        let mut room = room.write().await;
        let new_master = room.remove_client(client_id);
        info!(
            "[Room] Client_{} disconnected ({} remaining)",
            client_id,
            room.client_count()
        );
        new_master
    };

    if let Some(new_master_id) = new_master {
        let room = room.read().await;
        if let Some(new_master_client) = room.clients.get(&new_master_id) {
            info!("[Room] Promoting client_{} to master", new_master_id);
            send(
                new_master_client,
                ctrl_msg(CTRL_MASTER_CHANGED, &new_master_id.to_le_bytes()),
            )
            .await;
        }
    }
}
