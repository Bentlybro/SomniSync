use bytes::Bytes;
use log::warn;
use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::mpsc;
use tokio_tungstenite::tungstenite::Message;

pub type Tx = futures_util::stream::SplitSink<
    tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>,
    Message,
>;

/// How many outbound messages to buffer per client before dropping.
/// At 60 Hz with 50 entities, a message is ~420 bytes. 256 slots = ~4 seconds of buffer.
/// If a client's buffer is full they're too lagged to receive real-time data anyway.
const SEND_BUFFER: usize = 256;

pub enum ClientMsg {
    Binary(Bytes),
    Pong(Vec<u8>),
}

pub struct Client {
    pub tx: mpsc::Sender<ClientMsg>,
    pub addr: SocketAddr,
}

/// Create a new client and spawn its dedicated send task.
/// The send task owns the WebSocket sink and runs independently on the tokio thread pool,
/// so broadcasts to N clients are truly parallel rather than sequentially cooperative.
pub fn new_client(addr: SocketAddr, sink: Tx) -> Arc<Client> {
    let (tx, rx) = mpsc::channel(SEND_BUFFER);
    let client = Arc::new(Client { tx, addr });
    spawn_send_task(sink, rx, addr);
    client
}

fn spawn_send_task(mut sink: Tx, mut rx: mpsc::Receiver<ClientMsg>, addr: SocketAddr) {
    tokio::spawn(async move {
        use futures_util::SinkExt;
        while let Some(msg) = rx.recv().await {
            let ws_msg = match msg {
                ClientMsg::Binary(b) => Message::Binary(b.into()),
                ClientMsg::Pong(d)   => Message::Pong(d.into()),
            };
            if let Err(e) = sink.send(ws_msg).await {
                warn!("[Send] Error to {}: {}", addr, e);
                break;
            }
        }
        // Channel closed (client disconnected) — sink is dropped here, closing the WS.
    });
}

/// Send a reliable message to a single client (control messages, role assignment, etc.).
/// Awaits if the send buffer is full — for control messages we never want to drop.
pub async fn send(client: &Client, data: Vec<u8>) {
    if client.tx.send(ClientMsg::Binary(Bytes::from(data))).await.is_err() {
        warn!("[Room] Client {} disconnected, dropping send", client.addr);
    }
}

/// Broadcast a binary message to all clients except the sender.
///
/// Uses `try_send` (non-blocking) — if a client's buffer is full the message is dropped
/// for that client only. This is correct for real-time game data: a full buffer means the
/// client is already behind and stale frames should be skipped.
///
/// Zero-copy: one `Bytes::copy_from_slice` produces an Arc'd buffer; each clone is O(1).
/// Each client's send task runs on the tokio thread pool so all N sends are truly parallel.
pub fn broadcast_except(
    clients: &HashMap<u16, Arc<Client>>,
    sender_id: u16,
    data: &[u8],
) {
    let bytes = Bytes::copy_from_slice(data);
    for (&id, client) in clients {
        if id != sender_id {
            // O(1) Arc reference-count bump — no memory copy per recipient.
            if client.tx.try_send(ClientMsg::Binary(bytes.clone())).is_err() {
                // Buffer full — client is lagged, drop this frame silently.
            }
        }
    }
}
