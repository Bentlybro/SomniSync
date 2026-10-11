use bytes::Bytes;
use log::warn;
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, watch};
use tokio_tungstenite::tungstenite::Message;

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

/// How many outbound messages to buffer per client before dropping.
///
/// Keep this small. Game position/velocity data is real-time — queuing old frames
/// creates latency spikes rather than smoothness. If a client falls behind by more
/// than SEND_BUFFER messages, we drop the newest arrival (they're too lagged for
/// stale frames to be useful anyway). Smaller = lower worst-case latency.
///
/// At 20 Hz send rate: 8 slots ≈ 0.4s of buffer before dropping.
/// At 60 Hz send rate: 8 slots ≈ 0.13s of buffer before dropping.
/// Reduced from 32: the old value let slow clients accumulate 1.6s of stale
/// position frames that all arrived at once when congestion cleared, causing
/// a sudden flood of outdated positions that spectators applied as jumps.
const RELIABLE_BUFFER: usize = 64;

pub enum ClientMsg {
    Binary(Bytes),
    Pong(Vec<u8>),
}

pub struct Client {
    pub tx: mpsc::Sender<ClientMsg>,
    state_tx: watch::Sender<Bytes>,
    failed: Arc<AtomicBool>,
    pub addr: SocketAddr,
}

impl Client {
    pub fn is_failed(&self) -> bool {
        self.failed.load(Ordering::Acquire) || self.tx.is_closed()
    }

    fn send_state(&self, data: Bytes) {
        if self.is_failed() {
            stats::bump_dropped();
            return;
        }
        let len = data.len();
        self.state_tx.send_replace(data);
        stats::bump_tx(len);
    }

    fn send_reliable(&self, data: Bytes) -> bool {
        if self.is_failed() {
            stats::bump_dropped();
            return false;
        }
        let len = data.len();
        match self.tx.try_send(ClientMsg::Binary(data)) {
            Ok(()) => {
                stats::bump_tx(len);
                true
            }
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
    let failed = Arc::new(AtomicBool::new(false));
    let client = Arc::new(Client { tx, state_tx, failed: failed.clone(), addr });
    spawn_send_task(sink, rx, state_rx, failed, addr);
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
/// 5s is aggressive but physically reasonable: our broadcast rate is
/// 20Hz and our per-client send buffer is 8 slots, so a healthy client
/// drains a single message in <50ms. Anything taking >5s to write one
/// ~2KB WebSocket frame is dead.
const SEND_TIMEOUT: Duration = Duration::from_secs(5);

fn spawn_send_task(
    mut sink: Tx,
    mut rx: mpsc::Receiver<ClientMsg>,
    mut state_rx: watch::Receiver<Bytes>,
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
                    ClientMsg::Pong(d) => Message::Pong(d.into()),
                },
                changed = state_rx.changed() => {
                    if changed.is_err() { break; }
                    Message::Binary(state_rx.borrow_and_update().clone().into())
                }
            };
            match tokio::time::timeout(SEND_TIMEOUT, sink.send(ws_msg)).await {
                Ok(Ok(())) => {}
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
    let len = data.len();
    if client.tx.send(ClientMsg::Binary(Bytes::from(data))).await.is_err() {
        warn!("[Room] Client {} disconnected, dropping send", redact_peer(client.addr));
        stats::bump_dropped();
    } else {
        stats::bump_tx(len);
    }
}

/// Non-blocking send to a single client. Drops the message if the buffer is
/// full. Use for per-tick broadcasts (position updates) where stale data is
/// worse than no data.
pub fn send_try(client: &Client, data: Bytes) {
    let len = data.len();
    if client.tx.try_send(ClientMsg::Binary(data)).is_ok() {
        stats::bump_tx(len);
    } else {
        stats::bump_dropped();
    }
}

/// Broadcast a binary message to every client in `clients`, optionally
/// excluding a single sender.
///
/// Uses `try_send` (non-blocking) — if a client's buffer is full the message
/// is dropped for that client only. This is correct for real-time game data:
/// a full buffer means the client is already behind and stale frames should
/// be skipped.
///
/// Zero-copy: one `Bytes::copy_from_slice` produces an Arc'd buffer; each
/// recipient clone is O(1). Each client's send task runs on the tokio thread
/// pool so all N sends are truly parallel.
pub fn broadcast(
    clients: &HashMap<u16, Arc<Client>>,
    exclude: Option<u16>,
    data: &[u8],
) {
    let bytes = Bytes::copy_from_slice(data);
    let len = data.len();
    let mut delivered: u64 = 0;
    let mut dropped: u64 = 0;
    for (&id, client) in clients {
        if Some(id) == exclude {
            continue;
        }
        // O(1) Arc reference-count bump — no memory copy per recipient.
        if client.tx.try_send(ClientMsg::Binary(bytes.clone())).is_ok() {
            delivered += 1;
        } else {
            dropped += 1;
        }
    }
    stats::bump_tx_batch(len, delivered);
    for _ in 0..dropped { stats::bump_dropped(); }
}

/// Broadcast to every client except the sender. Thin wrapper kept for
/// readability at call sites where the exclusion is the important fact.
pub fn broadcast_except(
    clients: &HashMap<u16, Arc<Client>>,
    sender_id: u16,
    data: &[u8],
) {
    broadcast(clients, Some(sender_id), data);
}

/// Broadcast to ALL clients — e.g. server-generated control messages where
/// the sender should also re-anchor from the authoritative payload.
pub fn broadcast_all(clients: &HashMap<u16, Arc<Client>>, data: &[u8]) {
    broadcast(clients, None, data);
}
