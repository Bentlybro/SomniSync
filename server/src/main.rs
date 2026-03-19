use futures_util::{SinkExt, StreamExt};
use log::{error, info, warn};
use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{Mutex, RwLock};
use tokio_tungstenite::tungstenite::Message;

// --- Protocol constants ---
const MSG_POSITION_UPDATE: u8 = 0x01;
const MSG_SNAPSHOT: u8 = 0x02;
const MSG_INTERACTION_UPDATE: u8 = 0x03;
const CTRL_YOU_ARE_MASTER: u8 = 0x01;
const CTRL_YOU_ARE_SPECTATOR: u8 = 0x02;
const CTRL_SEND_SNAPSHOT: u8 = 0x03;
const CTRL_MASTER_CHANGED: u8 = 0x04;
const CTRL_CLAIM: u8 = 0x05;
const CTRL_RELEASE: u8 = 0x06;

type Tx = futures_util::stream::SplitSink<
    tokio_tungstenite::WebSocketStream<TcpStream>,
    Message,
>;

struct Client {
    tx: Mutex<Tx>,
    addr: SocketAddr,
}

struct Room {
    clients: HashMap<u16, Arc<Client>>,
    master_id: Option<u16>,
    next_id: u16,
    /// Cached latest snapshot from master so late-joiners get it instantly
    latest_snapshot: Option<Vec<u8>>,
    /// Object ownership: object_local_id → client_id (only for claimed objects)
    claimed_objects: HashMap<u16, u16>,
}

impl Room {
    fn new() -> Self {
        Self {
            clients: HashMap::new(),
            master_id: None,
            next_id: 1,
            latest_snapshot: None,
            claimed_objects: HashMap::new(),
        }
    }

    fn add_client(&mut self, client: Arc<Client>) -> (u16, bool) {
        let id = self.next_id;
        self.next_id += 1;
        let is_master = self.master_id.is_none();
        if is_master {
            self.master_id = Some(id);
        }
        self.clients.insert(id, client);
        (id, is_master)
    }

    fn remove_client(&mut self, id: u16) -> Option<u16> {
        self.clients.remove(&id);
        // Release all objects claimed by this client
        self.claimed_objects.retain(|_, owner| *owner != id);
        if self.master_id == Some(id) {
            self.master_id = None;
            self.latest_snapshot = None;
            // Promote next client
            if let Some(&new_master) = self.clients.keys().next() {
                self.master_id = Some(new_master);
                return Some(new_master);
            }
        }
        None
    }
}

/// Build a control message: [0xFF][ctrl_type][payload...]
fn ctrl_msg(ctrl_type: u8, payload: &[u8]) -> Message {
    let mut buf = Vec::with_capacity(2 + payload.len());
    buf.push(0xFF);
    buf.push(ctrl_type);
    buf.extend_from_slice(payload);
    Message::Binary(buf.into())
}

/// Build a control message with a u16 value
fn ctrl_msg_u16(ctrl_type: u8, value: u16) -> Message {
    ctrl_msg(ctrl_type, &value.to_le_bytes())
}

async fn send(client: &Client, msg: Message) {
    let mut tx = client.tx.lock().await;
    if let Err(e) = tx.send(msg).await {
        warn!("[Room] Failed to send to {}: {}", client.addr, e);
    }
}

async fn handle_connection(stream: TcpStream, addr: SocketAddr, room: Arc<RwLock<Room>>) {
    let ws = match tokio_tungstenite::accept_async(stream).await {
        Ok(ws) => ws,
        Err(e) => {
            error!("[Server] WebSocket handshake failed for {}: {}", addr, e);
            return;
        }
    };

    let (tx, mut rx) = ws.split();
    let client = Arc::new(Client {
        tx: Mutex::new(tx),
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
            room.clients.len(),
            if is_master { "MASTER" } else { "SPECTATOR" }
        );
        (id, is_master)
    };

    // Tell client their role
    if is_master {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_MASTER, client_id)).await;
    } else {
        send(&client, ctrl_msg_u16(CTRL_YOU_ARE_SPECTATOR, client_id)).await;

        // Send cached snapshot immediately (so client gets SOMETHING fast)
        // then also request a fresh one from master (cached may be stale)
        let room = room.read().await;
        if let Some(ref snapshot) = room.latest_snapshot {
            info!(
                "[Room] Sending cached snapshot to client_{} ({} bytes)",
                client_id,
                snapshot.len()
            );
            send(&client, Message::Binary(snapshot.clone().into())).await;
        }
        // Always request fresh snapshot from master
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

    // Message loop
    while let Some(msg) = rx.next().await {
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

                let msg_type = data[0];

                match msg_type {
                    // Position updates from master → broadcast to all spectators
                    MSG_POSITION_UPDATE => {
                        let room = room.read().await;
                        if room.master_id != Some(client_id) {
                            continue; // Only master can send updates
                        }
                        let msg = Message::Binary(data);
                        for (&id, c) in &room.clients {
                            if id != client_id {
                                let mut tx = c.tx.lock().await;
                                let _ = tx.send(msg.clone()).await;
                            }
                        }
                    }

                    // Snapshot from master → cache + forward to requesting client
                    MSG_SNAPSHOT => {
                        let mut room = room.write().await;
                        if room.master_id != Some(client_id) {
                            continue;
                        }
                        info!(
                            "[Room] Received snapshot from master ({} bytes), caching",
                            data.len()
                        );
                        // Cache for future joiners
                        room.latest_snapshot = Some(data.to_vec());
                        // Forward to all spectators (they'll use it for initial sync)
                        let msg = Message::Binary(data);
                        for (&id, c) in &room.clients {
                            if id != client_id {
                                let mut tx = c.tx.lock().await;
                                let _ = tx.send(msg.clone()).await;
                            }
                        }
                    }

                    // Interaction updates from any client → broadcast to all others
                    MSG_INTERACTION_UPDATE => {
                        let room = room.read().await;
                        let msg = Message::Binary(data);
                        for (&id, c) in &room.clients {
                            if id != client_id {
                                let mut tx = c.tx.lock().await;
                                let _ = tx.send(msg.clone()).await;
                            }
                        }
                    }

                    // Control messages from clients (claim/release)
                    0xFF => {
                        if data.len() >= 6 {
                            let ctrl = data[1];
                            let obj_id = u16::from_le_bytes([data[2], data[3]]);
                            let sender_id = u16::from_le_bytes([data[4], data[5]]);

                            match ctrl {
                                CTRL_CLAIM => {
                                    let mut room = room.write().await;
                                    room.claimed_objects.insert(obj_id, sender_id);
                                    info!("[Room] Client_{} claimed object {}", sender_id, obj_id);
                                    // Broadcast to all (including master)
                                    let msg = Message::Binary(data);
                                    for (&id, c) in &room.clients {
                                        if id != client_id {
                                            let mut tx = c.tx.lock().await;
                                            let _ = tx.send(msg.clone()).await;
                                        }
                                    }
                                }
                                CTRL_RELEASE => {
                                    let mut room = room.write().await;
                                    room.claimed_objects.remove(&obj_id);
                                    info!("[Room] Client_{} released object {}", sender_id, obj_id);
                                    // Broadcast to all
                                    let msg = Message::Binary(data);
                                    for (&id, c) in &room.clients {
                                        if id != client_id {
                                            let mut tx = c.tx.lock().await;
                                            let _ = tx.send(msg.clone()).await;
                                        }
                                    }
                                }
                                _ => {} // Ignore other control from clients
                            }
                        }
                    }

                    _ => {
                        warn!(
                            "[Room] Unknown message type 0x{:02X} from client_{}",
                            msg_type, client_id
                        );
                    }
                }
            }

            Message::Close(_) => break,
            Message::Ping(d) => {
                send(&client, Message::Pong(d)).await;
            }
            _ => {} // Ignore text messages
        }
    }

    // Client disconnected
    let new_master = {
        let mut room = room.write().await;
        let new_master = room.remove_client(client_id);
        info!(
            "[Room] Client_{} disconnected ({} remaining)",
            client_id,
            room.clients.len()
        );
        new_master
    };

    // Notify new master
    if let Some(new_master_id) = new_master {
        let room = room.read().await;
        if let Some(new_master_client) = room.clients.get(&new_master_id) {
            info!(
                "[Room] Promoting client_{} to master",
                new_master_id
            );
            send(
                new_master_client,
                ctrl_msg(CTRL_MASTER_CHANGED, &new_master_id.to_le_bytes()),
            )
            .await;
        }
    }
}

#[tokio::main]
async fn main() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    let addr = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "0.0.0.0:9090".to_string());

    let listener = TcpListener::bind(&addr).await.expect("Failed to bind");
    info!("[Server] Somnium Relay v2.0 started on ws://{}/", addr);
    info!("[Server] Ready for connections...");

    let room = Arc::new(RwLock::new(Room::new()));

    while let Ok((stream, addr)) = listener.accept().await {
        let room = room.clone();
        tokio::spawn(handle_connection(stream, addr, room));
    }
}
