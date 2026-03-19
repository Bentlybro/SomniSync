use futures_util::{SinkExt, StreamExt};
use log::{debug, error, info, warn};
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
const MSG_VARIABLE_UPDATE: u8 = 0x04;
const MSG_PLAYER_POSITION: u8 = 0x05;
const CTRL_YOU_ARE_MASTER: u8 = 0x01;
const CTRL_YOU_ARE_SPECTATOR: u8 = 0x02;
const CTRL_SEND_SNAPSHOT: u8 = 0x03;
const CTRL_MASTER_CHANGED: u8 = 0x04;
const CTRL_CLAIM: u8 = 0x05;
const CTRL_RELEASE: u8 = 0x06;

// --- AOI constants ---
const AOI_NEAR: f32 = 50.0;     // Full rate
const AOI_MID: f32 = 150.0;     // Every 3rd tick
const AOI_FAR: f32 = 300.0;     // Every 10th tick
// Beyond AOI_FAR: no updates (culled)

type Tx = futures_util::stream::SplitSink<
    tokio_tungstenite::WebSocketStream<TcpStream>,
    Message,
>;

#[derive(Clone, Copy, Default)]
struct Vec3 {
    x: f32,
    y: f32,
    z: f32,
}

impl Vec3 {
    fn dist_sq(&self, other: &Vec3) -> f32 {
        let dx = self.x - other.x;
        let dy = self.y - other.y;
        let dz = self.z - other.z;
        dx * dx + dy * dy + dz * dz
    }
}

fn read_f32(data: &[u8], offset: usize) -> f32 {
    f32::from_le_bytes([data[offset], data[offset + 1], data[offset + 2], data[offset + 3]])
}

fn read_u16(data: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes([data[offset], data[offset + 1]])
}

fn write_u16(buf: &mut Vec<u8>, value: u16) {
    buf.extend_from_slice(&value.to_le_bytes());
}

fn write_f32(buf: &mut Vec<u8>, value: f32) {
    buf.extend_from_slice(&value.to_le_bytes());
}

struct Client {
    tx: Mutex<Tx>,
    addr: SocketAddr,
    position: Mutex<Vec3>,
    /// Tick counter per client for distance-based rate limiting
    tick: Mutex<u32>,
}

struct Room {
    clients: HashMap<u16, Arc<Client>>,
    master_id: Option<u16>,
    next_id: u16,
    latest_snapshot: Option<Vec<u8>>,
    claimed_objects: HashMap<u16, u16>,
    /// Server-side object position cache (updated from 0x01/0x02/0x03)
    object_positions: HashMap<u16, Vec3>,
    /// AOI enabled (can be toggled)
    aoi_enabled: bool,
}

impl Room {
    fn new() -> Self {
        Self {
            clients: HashMap::new(),
            master_id: None,
            next_id: 1,
            latest_snapshot: None,
            claimed_objects: HashMap::new(),
            object_positions: HashMap::new(),
            aoi_enabled: true,
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
        self.claimed_objects.retain(|_, owner| *owner != id);
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

    /// Update object positions from a position/interaction/snapshot message
    fn update_object_positions(&mut self, data: &[u8]) {
        if data.len() < 3 {
            return;
        }
        let count = read_u16(data, 1) as usize;
        let mut offset = 3;
        for _ in 0..count {
            if offset + 26 > data.len() {
                break;
            }
            let obj_id = read_u16(data, offset);
            let pos = Vec3 {
                x: read_f32(data, offset + 2),
                y: read_f32(data, offset + 6),
                z: read_f32(data, offset + 10),
            };
            self.object_positions.insert(obj_id, pos);
            offset += 26;
        }
    }

    /// Filter a position/interaction update for a specific client based on AOI.
    /// Returns None if no entities are in range, or a filtered message.
    fn filter_for_client(
        &self,
        data: &[u8],
        client_pos: &Vec3,
        tick: u32,
    ) -> Option<Vec<u8>> {
        if data.len() < 3 {
            return None;
        }
        let msg_type = data[0];
        let count = read_u16(data, 1) as usize;

        let near_sq = AOI_NEAR * AOI_NEAR;
        let mid_sq = AOI_MID * AOI_MID;
        let far_sq = AOI_FAR * AOI_FAR;

        // First pass: count entities that pass the AOI filter
        let mut filtered: Vec<(usize, u16)> = Vec::new(); // (byte offset, obj_id)
        let mut offset = 3;
        for _ in 0..count {
            if offset + 26 > data.len() {
                break;
            }
            let obj_id = read_u16(data, offset);
            let obj_pos = Vec3 {
                x: read_f32(data, offset + 2),
                y: read_f32(data, offset + 6),
                z: read_f32(data, offset + 10),
            };

            let dist_sq = client_pos.dist_sq(&obj_pos);

            let include = if dist_sq <= near_sq {
                true // Every tick
            } else if dist_sq <= mid_sq {
                tick % 3 == 0 // Every 3rd tick
            } else if dist_sq <= far_sq {
                tick % 10 == 0 // Every 10th tick
            } else {
                false // Culled
            };

            if include {
                filtered.push((offset, obj_id));
            }
            offset += 26;
        }

        if filtered.is_empty() {
            return None;
        }

        // If all entities pass, just send the original message
        if filtered.len() == count {
            return Some(data.to_vec());
        }

        // Build filtered message
        let mut buf = Vec::with_capacity(3 + filtered.len() * 26);
        buf.push(msg_type);
        write_u16(&mut buf, filtered.len() as u16);

        for (byte_offset, _) in &filtered {
            buf.extend_from_slice(&data[*byte_offset..*byte_offset + 26]);
        }

        Some(buf)
    }
}

fn ctrl_msg(ctrl_type: u8, payload: &[u8]) -> Message {
    let mut buf = Vec::with_capacity(2 + payload.len());
    buf.push(0xFF);
    buf.push(ctrl_type);
    buf.extend_from_slice(payload);
    Message::Binary(buf.into())
}

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
        position: Mutex::new(Vec3::default()),
        tick: Mutex::new(0),
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

        let room = room.read().await;
        if let Some(ref snapshot) = room.latest_snapshot {
            info!(
                "[Room] Sending cached snapshot to client_{} ({} bytes)",
                client_id,
                snapshot.len()
            );
            send(&client, Message::Binary(snapshot.clone().into())).await;
        }
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
                    // Player position report (0x05): [0x05][f32 x][f32 y][f32 z]
                    MSG_PLAYER_POSITION => {
                        if data.len() >= 13 {
                            let pos = Vec3 {
                                x: read_f32(&data, 1),
                                y: read_f32(&data, 5),
                                z: read_f32(&data, 9),
                            };
                            *client.position.lock().await = pos;
                        }
                    }

                    // Position updates from master → AOI-filtered broadcast
                    MSG_POSITION_UPDATE => {
                        let mut room = room.write().await;
                        if room.master_id != Some(client_id) {
                            continue;
                        }

                        // Update server's object position cache
                        room.update_object_positions(&data);

                        if !room.aoi_enabled {
                            // No AOI: broadcast to all
                            let msg = Message::Binary(data);
                            for (&id, c) in &room.clients {
                                if id != client_id {
                                    let mut tx = c.tx.lock().await;
                                    let _ = tx.send(msg.clone()).await;
                                }
                            }
                        } else {
                            // AOI: filter per client
                            // Collect client data first to avoid holding locks
                            let mut targets: Vec<(u16, Vec3, u32, Arc<Client>)> = Vec::new();
                            for (&id, c) in &room.clients {
                                if id != client_id {
                                    let pos = *c.position.lock().await;
                                    let mut tick = c.tick.lock().await;
                                    *tick = tick.wrapping_add(1);
                                    targets.push((id, pos, *tick, c.clone()));
                                }
                            }

                            for (_, client_pos, tick, target) in &targets {
                                if let Some(filtered) =
                                    room.filter_for_client(&data, client_pos, *tick)
                                {
                                    let mut tx = target.tx.lock().await;
                                    let _ = tx.send(Message::Binary(filtered.into())).await;
                                }
                            }
                        }
                    }

                    // Snapshot from master → cache + forward to ALL (no AOI on snapshots)
                    MSG_SNAPSHOT => {
                        let mut room = room.write().await;
                        if room.master_id != Some(client_id) {
                            continue;
                        }
                        info!(
                            "[Room] Received snapshot from master ({} bytes), caching",
                            data.len()
                        );
                        // Update object positions from snapshot
                        room.update_object_positions(&data);
                        room.latest_snapshot = Some(data.to_vec());
                        let msg = Message::Binary(data);
                        for (&id, c) in &room.clients {
                            if id != client_id {
                                let mut tx = c.tx.lock().await;
                                let _ = tx.send(msg.clone()).await;
                            }
                        }
                    }

                    // Interaction updates → AOI-filtered broadcast
                    MSG_INTERACTION_UPDATE => {
                        let mut room = room.write().await;
                        room.update_object_positions(&data);

                        if !room.aoi_enabled {
                            let msg = Message::Binary(data);
                            for (&id, c) in &room.clients {
                                if id != client_id {
                                    let mut tx = c.tx.lock().await;
                                    let _ = tx.send(msg.clone()).await;
                                }
                            }
                        } else {
                            let mut targets: Vec<(u16, Vec3, u32, Arc<Client>)> = Vec::new();
                            for (&id, c) in &room.clients {
                                if id != client_id {
                                    let pos = *c.position.lock().await;
                                    let tick = *c.tick.lock().await;
                                    targets.push((id, pos, tick, c.clone()));
                                }
                            }

                            for (_, client_pos, tick, target) in &targets {
                                if let Some(filtered) =
                                    room.filter_for_client(&data, client_pos, *tick)
                                {
                                    let mut tx = target.tx.lock().await;
                                    let _ = tx.send(Message::Binary(filtered.into())).await;
                                }
                            }
                        }
                    }

                    // Variable updates → broadcast to all (no AOI, small messages)
                    MSG_VARIABLE_UPDATE => {
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
                                    debug!("[Room] Client_{} claimed object {}", sender_id, obj_id);
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
                                    debug!(
                                        "[Room] Client_{} released object {}",
                                        sender_id, obj_id
                                    );
                                    let msg = Message::Binary(data);
                                    for (&id, c) in &room.clients {
                                        if id != client_id {
                                            let mut tx = c.tx.lock().await;
                                            let _ = tx.send(msg.clone()).await;
                                        }
                                    }
                                }
                                _ => {}
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
            _ => {}
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

#[tokio::main]
async fn main() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    let addr = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "0.0.0.0:9090".to_string());

    let listener = TcpListener::bind(&addr).await.expect("Failed to bind");
    info!("[Server] Somnium Relay v2.1 started on ws://{}/", addr);
    info!(
        "[Server] AOI enabled: near={}m mid={}m far={}m",
        AOI_NEAR, AOI_MID, AOI_FAR
    );
    info!("[Server] Ready for connections...");

    let room = Arc::new(RwLock::new(Room::new()));

    let shutdown = tokio::signal::ctrl_c();
    tokio::pin!(shutdown);

    loop {
        tokio::select! {
            Ok((stream, addr)) = listener.accept() => {
                let room = room.clone();
                tokio::spawn(handle_connection(stream, addr, room));
            }
            _ = &mut shutdown => {
                info!("[Server] Shutting down...");
                std::process::exit(0);
            }
        }
    }
}
