use bytes::Bytes;
use log::warn;
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::sync::{mpsc, watch, Notify};
use tokio_tungstenite::tungstenite::{error::ProtocolError, Error as WsError, Message};

use crate::stats;

/// Redact a peer address for log + dashboard output.
///
/// Leaking raw client IPs to the metrics dashboard (or the in-memory log
/// tail it exposes) is a user-privacy regression — anyone who can reach
/// the dashboard learns every connected player's public IP. We replace
/// the IP portion with a short hash of itself so admins can still
/// correlate "peer-a3f2 keeps disconnecting" across log lines without
/// learning the actual address. Port is preserved because it's an
/// ephemeral per-session value, not identifying.
pub fn redact_peer(addr: SocketAddr) -> String {
    let mut h = std::collections::hash_map::DefaultHasher::new();
    addr.ip().hash(&mut h);
    format!("peer-{:04x}:{}", h.finish() & 0xFFFF, addr.port())
}

pub type Tx = futures_util::stream::SplitSink<
    tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>,
    Message,
>;

/// Maximum pending dependable messages per client.
///
/// Control, events, variables, and animator updates must arrive in order. A
/// peer that cannot drain this queue is disconnected instead of silently
/// missing a world change. Physics positions use a separate latest-only slot.
const RELIABLE_BUFFER: usize = 64;

pub enum ClientMsg {
    Binary(Bytes),
    Pong(Vec<u8>),
}

pub struct Client {
    pub tx: mpsc::Sender<ClientMsg>,
    state_tx: watch::Sender<Bytes>,
    held_latest: Arc<Mutex<HashMap<u16, Bytes>>>,
    held_notify: Arc<Notify>,
    failed: Arc<AtomicBool>,
    compact_positions: AtomicBool,
    pub addr: SocketAddr,
}

impl Client {
    pub fn enable_compact_positions(&self) {
        self.compact_positions.store(true, Ordering::Release);
    }

    pub fn supports_compact_positions(&self) -> bool {
        self.compact_positions.load(Ordering::Acquire)
    }

    pub fn is_failed(&self) -> bool {
        self.failed.load(Ordering::Acquire) || self.tx.is_closed()
    }

    fn send_state(&self, data: Bytes) {
        if self.is_failed() {
            stats::bump_dropped();
            return;
        }
        self.state_tx.send_replace(data);
    }

    fn send_held(&self, sender_id: u16, data: Bytes) {
        if self.is_failed() {
            stats::bump_dropped();
            return;
        }
        self.held_latest.lock().unwrap().insert(sender_id, data);
        self.held_notify.notify_one();
    }

    fn clear_held_sender(&self, sender_id: u16) {
        self.held_latest.lock().unwrap().remove(&sender_id);
    }

    fn clear_state(&self) {
        self.state_tx.send_replace(Bytes::new());
    }

    fn send_reliable(&self, data: Bytes) -> bool {
        if self.is_failed() {
            stats::bump_dropped();
            return false;
        }
        match self.tx.try_send(ClientMsg::Binary(data)) {
            Ok(()) => true,
            Err(_) => {
                self.failed.store(true, Ordering::Release);
                stats::bump_dropped();
                false
            }
        }
    }
}

/// Create a new client and spawn its dedicated send task.
/// The send task owns the WebSocket sink and runs independently on the tokio thread pool,
/// so broadcasts to N clients are truly parallel rather than sequentially cooperative.
pub fn new_client(addr: SocketAddr, sink: Tx) -> Arc<Client> {
    let (tx, rx) = mpsc::channel(RELIABLE_BUFFER);
    let (state_tx, state_rx) = watch::channel(Bytes::new());
    let held_latest = Arc::new(Mutex::new(HashMap::new()));
    let held_notify = Arc::new(Notify::new());
    let failed = Arc::new(AtomicBool::new(false));
    let client = Arc::new(Client {
        tx,
        state_tx,
        held_latest: held_latest.clone(),
        held_notify: held_notify.clone(),
        failed: failed.clone(),
        compact_positions: AtomicBool::new(false),
        addr,
    });
    spawn_send_task(sink, rx, state_rx, held_latest, held_notify, failed, addr);
    client
}

/// Maximum time a single sink.send call is allowed to block before we
/// declare the client unreachable and tear the send task down.
///
/// Why this exists: if a peer's TCP receive buffer fills (Windows
/// Unity editor minimised, VR client frozen mid-GC, laptop lid closed),
/// kernel-level TCP just stops accepting our writes. `sink.send` awaits
/// forever. The mpsc channel between handler and send task fills up,
/// broadcast_* starts dropping 20 msg/sec into a full buffer, and
/// nothing in the pipeline notices because the previous zombie-client
/// detector (client.rs / handler.rs tx.is_closed() poll) fires on send
/// task *exit*, not send task *stall*. 31-second stalls observed in
/// the wild. Kernel TCP keepalive doesn't help — default is 2 hours.
///
/// Physics snapshots are coalesced while reliable messages use a bounded
/// queue. A single frame taking more than 5 seconds means the peer cannot
/// keep up with the room.
const SEND_TIMEOUT: Duration = Duration::from_secs(5);

fn spawn_send_task(
    mut sink: Tx,
    mut rx: mpsc::Receiver<ClientMsg>,
    mut state_rx: watch::Receiver<Bytes>,
    held_latest: Arc<Mutex<HashMap<u16, Bytes>>>,
    held_notify: Arc<Notify>,
    failed: Arc<AtomicBool>,
    addr: SocketAddr,
) {
    tokio::spawn(async move {
        use futures_util::SinkExt;
        loop {
            if failed.load(Ordering::Acquire) {
                break;
            }
            let ws_msg = tokio::select! {
                biased;
                Some(msg) = rx.recv() => match msg {
                    ClientMsg::Binary(b) => Message::Binary(b.into()),
                    ClientMsg::Pong(d) => Message::Pong(d),
                },
                changed = state_rx.changed() => {
                    if changed.is_err() { break; }
                    let latest = state_rx.borrow_and_update().clone();
                    if latest.is_empty() { continue; }
                    Message::Binary(latest.into())
                },
                _ = held_notify.notified() => {
                    let mut pending = held_latest.lock().unwrap();
                    let next = pending.keys().next().copied()
                        .and_then(|id| pending.remove(&id));
                    if !pending.is_empty() { held_notify.notify_one(); }
                    match next {
                        Some(bytes) => Message::Binary(bytes.into()),
                        None => continue,
                    }
                }
            };
            let wire_bytes = match &ws_msg {
                Message::Binary(bytes) => bytes.len(),
                Message::Pong(bytes) => bytes.len(),
                _ => 0,
            };
            match tokio::time::timeout(SEND_TIMEOUT, sink.send(ws_msg)).await {
                Ok(Ok(())) => stats::bump_tx(wire_bytes),
                // A peer can complete its close handshake while a queued
                // snapshot is being sent. That is a normal disconnect.
                Ok(Err(WsError::Protocol(ProtocolError::SendAfterClosing))) => break,
                Ok(Err(e)) => {
                    warn!("[Send] Error to {}: {}", redact_peer(addr), e);
                    break;
                }
                Err(_) => {
                    warn!(
                        "[Send] Timeout (>{}s) sending to {} — peer TCP stalled, killing client",
                        SEND_TIMEOUT.as_secs(),
                        redact_peer(addr)
                    );
                    break;
                }
            }
        }
        // Loop exit — sink is dropped here, closing the WS. The receive side
        // mpsc rx is also dropped, which flips `client.tx.is_closed()` to
        // true, which the handler's 2-second poll detects and uses as its
        // cue to tear down the rest of the connection cleanly.
    });
}

/// Send a reliable message to a single client (control messages, role assignment, etc.).
/// Awaits if the send buffer is full — for control messages we never want to drop.
pub async fn send(client: &Client, data: Vec<u8>) {
    if client.is_failed() {
        stats::bump_dropped();
        return;
    }
    if client
        .tx
        .send(ClientMsg::Binary(Bytes::from(data)))
        .await
        .is_err()
    {
        warn!(
            "[Room] Client {} disconnected, dropping send",
            redact_peer(client.addr)
        );
        stats::bump_dropped();
    }
}

/// Replace this client's previous unsent physics snapshot. Never queue stale
/// positions behind dependable world changes.
pub fn send_try(client: &Client, data: Bytes) {
    client.send_state(data);
}

/// Relay only the newest unsent held-object packet from each owning client.
pub fn broadcast_held(clients: &HashMap<u16, Arc<Client>>, sender_id: u16, data: &[u8]) {
    let bytes = Bytes::copy_from_slice(data);
    for (&id, client) in clients {
        if id != sender_id {
            client.send_held(sender_id, bytes.clone());
        }
    }
}

/// Broadcast a binary message to every client in `clients`, optionally
/// excluding a single sender.
///
/// Dependable broadcast. If a peer's bounded queue fills, mark it failed so
/// its connection is torn down rather than leaving it with missing world state.
///
/// Zero-copy: one `Bytes::copy_from_slice` produces an Arc'd buffer; each
/// recipient clone is O(1). Each client's send task runs on the tokio thread
/// pool so all N sends are truly parallel.
pub fn broadcast(clients: &HashMap<u16, Arc<Client>>, exclude: Option<u16>, data: &[u8]) {
    let bytes = Bytes::copy_from_slice(data);
    for (&id, client) in clients {
        if Some(id) == exclude {
            continue;
        }
        if data.len() >= 2 && data[0] == 0xFF {
            if matches!(
                data[1],
                crate::protocol::CTRL_CLAIM
                    | crate::protocol::CTRL_RELEASE
                    | crate::protocol::CTRL_SPAWN
                    | crate::protocol::CTRL_DESPAWN
            ) {
                client.clear_state();
            }
            if data.len() >= 6 && data[1] == crate::protocol::CTRL_RELEASE {
                client.clear_held_sender(u16::from_le_bytes([data[4], data[5]]));
            }
        }
        client.send_reliable(bytes.clone());
    }
}

/// Broadcast to every client except the sender. Thin wrapper kept for
/// readability at call sites where the exclusion is the important fact.
pub fn broadcast_except(clients: &HashMap<u16, Arc<Client>>, sender_id: u16, data: &[u8]) {
    broadcast(clients, Some(sender_id), data);
}

/// Broadcast to ALL clients — e.g. server-generated control messages where
/// the sender should also re-anchor from the authoritative payload.
pub fn broadcast_all(clients: &HashMap<u16, Arc<Client>>, data: &[u8]) {
    broadcast(clients, None, data);
}
