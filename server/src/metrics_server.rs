//! HTTP dashboard for runtime metrics.
//!
//! Serves two routes on a separate port from the WebSocket listener:
//!   * `/`         — embedded HTML dashboard (no build step, zero JS deps).
//!   * `/metrics`  — JSON snapshot of counters, room state, physics timing,
//!     and the in-memory log ring buffer.
//!
//! The dashboard is a standalone single file that polls `/metrics` every
//! second. Rates (bytes/sec, msgs/sec) are computed here per-request as
//! deltas from the previous sample — no rolling-window bookkeeping lives on
//! the hot path. Previous-sample state is scoped to each HTTP client's
//! session via a shared `Arc<Mutex<PrevSample>>` so multiple browsers don't
//! trample each other's rate math.

use std::collections::BTreeMap;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use axum::{extract::State, response::Html, routing::get, Json, Router};
use log::info;
use serde::Serialize;
use tokio::net::TcpListener;
use tokio::sync::Mutex as AsyncMutex;

use crate::room::RoomMap;
use crate::stats::{Stats, LOG_TAIL_LIVE};

const DASHBOARD_HTML: &str = include_str!("dashboard.html");

#[derive(Clone)]
pub struct AppState {
    pub stats: Arc<Stats>,
    pub rooms: RoomMap,
    pub prev: Arc<AsyncMutex<PrevSample>>,
}

pub struct PrevSample {
    at: Option<Instant>,
    bytes_rx: u64,
    bytes_tx: u64,
    msgs_rx: u64,
    msgs_tx: u64,
}

impl PrevSample {
    fn new() -> Self {
        Self {
            at: None,
            bytes_rx: 0,
            bytes_tx: 0,
            msgs_rx: 0,
            msgs_tx: 0,
        }
    }
}

pub async fn serve(addr: String, stats: Arc<Stats>, rooms: RoomMap) {
    let state = AppState {
        stats,
        rooms,
        prev: Arc::new(AsyncMutex::new(PrevSample::new())),
    };

    let app = Router::new()
        .route("/", get(index))
        .route("/metrics", get(metrics))
        .route("/export", get(export))
        .with_state(state);

    match TcpListener::bind(&addr).await {
        Ok(listener) => {
            info!("[Metrics] Dashboard listening on http://{}/", addr);
            if let Err(e) = axum::serve(listener, app).await {
                log::warn!("[Metrics] Serve error: {}", e);
            }
        }
        Err(e) => {
            log::warn!(
                "[Metrics] Failed to bind {}: {}. Dashboard disabled.",
                addr,
                e
            );
        }
    }
}

async fn index() -> Html<&'static str> {
    Html(DASHBOARD_HTML)
}

// ── JSON response shape ────────────────────────────────────────────────────
#[derive(Serialize)]
struct MetricsSnapshot {
    sampled_at_ms: u64,
    uptime_sec: f64,

    cpu_percent: f32,
    rss_bytes: u64,

    clients_current: usize,
    clients_total_connected: u64,
    clients_total_disconnected: u64,

    total_msgs_rx: u64,
    total_msgs_tx: u64,
    total_bytes_rx: u64,
    total_bytes_tx: u64,
    total_msgs_dropped: u64,

    // Computed as delta from the previous /metrics call.
    rx_bytes_per_sec: f64,
    tx_bytes_per_sec: f64,
    rx_msgs_per_sec: f64,
    tx_msgs_per_sec: f64,

    physics_tick_p50_us: u64,
    physics_tick_p95_us: u64,
    physics_tick_p99_us: u64,
    physics_broadcast_p50_us: u64,
    physics_broadcast_p95_us: u64,
    physics_broadcast_p99_us: u64,

    rooms: Vec<RoomSnapshot>,
    clients: Vec<ClientSnapshot>,
    msgs_rx_by_type: BTreeMap<String, u64>,
    msgs_rx_ctrl_subtype: BTreeMap<String, u64>,
    log: Vec<LogLineOut>,
}

#[derive(Serialize)]
struct ClientSnapshot {
    cid: u16,
    addr: String,
    room: String,
    connected_at_ms: u64,
    uptime_sec: f64,
    msgs_rx: u64,
    bytes_rx: u64,
}

#[derive(Serialize)]
struct ExportSnapshot {
    /// Everything in /metrics, plus:
    metrics: MetricsSnapshot,
    /// Raw physics tick samples in microseconds (most recent first trimmed
    /// is oldest → newest). Useful for plotting the actual distribution.
    physics_tick_us_raw: Vec<u64>,
    physics_broadcast_us_raw: Vec<u64>,
    /// Server identification so diff'd exports can be matched up.
    server_version: &'static str,
    exported_at_unix_ms: u64,
}

#[derive(Serialize)]
struct RoomSnapshot {
    id: String,
    clients: usize,
    physics_ready: bool,
    object_count: usize,
    claimed_count: usize,
    scene_ready_ms: u64,
}

#[derive(Serialize)]
struct LogLineOut {
    ts_unix_ms: u64,
    level: &'static str,
    msg: String,
}

async fn metrics(State(state): State<AppState>) -> Json<MetricsSnapshot> {
    Json(build_snapshot(&state, /* full_log */ false).await)
}

async fn export(State(state): State<AppState>) -> Json<ExportSnapshot> {
    let stats = state.stats.clone();
    let metrics = build_snapshot(&state, /* full_log */ true).await;

    let physics_tick_us_raw: Vec<u64> = stats
        .physics_tick_us
        .lock()
        .map(|v| v.iter().copied().collect())
        .unwrap_or_default();
    let physics_broadcast_us_raw: Vec<u64> = stats
        .physics_broadcast_us
        .lock()
        .map(|v| v.iter().copied().collect())
        .unwrap_or_default();

    let exported_at_unix_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);

    Json(ExportSnapshot {
        metrics,
        physics_tick_us_raw,
        physics_broadcast_us_raw,
        server_version: env!("CARGO_PKG_VERSION"),
        exported_at_unix_ms,
    })
}

async fn build_snapshot(state: &AppState, full_log: bool) -> MetricsSnapshot {
    let stats = state.stats.clone();

    // Atomic counters — read once here so the subsequent rate computation
    // lines up with the numbers we emit.
    let now = Instant::now();
    let bytes_rx = stats.total_bytes_rx.load(Ordering::Relaxed);
    let bytes_tx = stats.total_bytes_tx.load(Ordering::Relaxed);
    let msgs_rx = stats.total_msgs_rx.load(Ordering::Relaxed);
    let msgs_tx = stats.total_msgs_tx.load(Ordering::Relaxed);

    let (rx_bps, tx_bps, rx_mps, tx_mps) = {
        let mut prev = state.prev.lock().await;
        let rates = match prev.at {
            Some(at) => {
                let dt = now.saturating_duration_since(at).as_secs_f64().max(0.001);
                (
                    (bytes_rx.saturating_sub(prev.bytes_rx)) as f64 / dt,
                    (bytes_tx.saturating_sub(prev.bytes_tx)) as f64 / dt,
                    (msgs_rx.saturating_sub(prev.msgs_rx)) as f64 / dt,
                    (msgs_tx.saturating_sub(prev.msgs_tx)) as f64 / dt,
                )
            }
            None => (0.0, 0.0, 0.0, 0.0),
        };
        prev.at = Some(now);
        prev.bytes_rx = bytes_rx;
        prev.bytes_tx = bytes_tx;
        prev.msgs_rx = msgs_rx;
        prev.msgs_tx = msgs_tx;
        rates
    };

    // Physics timing histograms.
    let (tick_p50, tick_p95, tick_p99) = percentiles_from_mutex(&stats.physics_tick_us);
    let (bc_p50, bc_p95, bc_p99) = percentiles_from_mutex(&stats.physics_broadcast_us);

    // Room snapshots. We read the room map + each room briefly — no
    // long-held locks, no hot-path blocking.
    let mut room_snapshots: Vec<RoomSnapshot> = Vec::new();
    let mut clients_current = 0usize;
    {
        let rooms_guard = state.rooms.read().await;
        // Sort room IDs for stable rendering between refreshes.
        let mut ids: Vec<&String> = rooms_guard.keys().collect();
        ids.sort();
        for id in ids {
            if let Some(room_arc) = rooms_guard.get(id) {
                let r = room_arc.read().await;
                let cc = r.client_count();
                clients_current += cc;
                room_snapshots.push(RoomSnapshot {
                    id: id.clone(),
                    clients: cc,
                    physics_ready: r.physics_ready,
                    object_count: r.scene_object_count as usize + r.runtime_spawn_count as usize,
                    claimed_count: r.claimed_objects.len(),
                    scene_ready_ms: r.scene_ready_ms,
                });
            }
        }
    }

    // Client list — snapshot the map under a brief read lock, then release
    // before reading the per-client atomics. No async work while locked.
    let mut client_snapshots: Vec<ClientSnapshot> = Vec::new();
    if let Ok(clients) = stats.clients.read() {
        let snapshot: Vec<_> = clients.values().cloned().collect();
        drop(clients);
        let now_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0);
        for info in snapshot {
            client_snapshots.push(ClientSnapshot {
                cid: info.cid,
                addr: info.addr.clone(),
                room: info.room.clone(),
                connected_at_ms: info.connected_at_ms,
                uptime_sec: now_ms.saturating_sub(info.connected_at_ms) as f64 / 1000.0,
                msgs_rx: info.msgs_rx.load(Ordering::Relaxed),
                bytes_rx: info.bytes_rx.load(Ordering::Relaxed),
            });
        }
        client_snapshots.sort_by(|a, b| a.room.cmp(&b.room).then(a.cid.cmp(&b.cid)));
    }

    // Message-type histograms — only include non-zero buckets so the JSON
    // doesn't carry 256 empty slots per request.
    let mut msgs_rx_by_type: BTreeMap<String, u64> = BTreeMap::new();
    for (i, slot) in stats.msgs_rx_by_type.iter().enumerate() {
        let v = slot.load(Ordering::Relaxed);
        if v > 0 {
            msgs_rx_by_type.insert(msg_type_name(i as u8), v);
        }
    }
    let mut msgs_rx_ctrl_subtype: BTreeMap<String, u64> = BTreeMap::new();
    for (i, slot) in stats.msgs_rx_ctrl_subtype.iter().enumerate() {
        let v = slot.load(Ordering::Relaxed);
        if v > 0 {
            msgs_rx_ctrl_subtype.insert(ctrl_subtype_name(i as u8), v);
        }
    }

    // Log tail — collect under the sync lock, release, THEN build output.
    // `/metrics` gets the most recent LOG_TAIL_LIVE lines to keep the 1Hz
    // dashboard refresh lean; `/export` gets the full ring.
    let log_lines: Vec<LogLineOut> = {
        match stats.log_lines.lock() {
            Ok(lines) => {
                let iter = lines.iter();
                let collected: Vec<LogLineOut> = if full_log || lines.len() <= LOG_TAIL_LIVE {
                    iter.map(|l| LogLineOut {
                        ts_unix_ms: l.ts_unix_ms,
                        level: l.level,
                        msg: l.msg.clone(),
                    })
                    .collect()
                } else {
                    lines
                        .iter()
                        .skip(lines.len() - LOG_TAIL_LIVE)
                        .map(|l| LogLineOut {
                            ts_unix_ms: l.ts_unix_ms,
                            level: l.level,
                            msg: l.msg.clone(),
                        })
                        .collect()
                };
                collected
            }
            Err(_) => Vec::new(),
        }
    };

    let sampled_at_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);

    MetricsSnapshot {
        sampled_at_ms,
        uptime_sec: stats.started_at.elapsed().as_secs_f64(),

        cpu_percent: stats.cpu_percent_x100.load(Ordering::Relaxed) as f32 / 100.0,
        rss_bytes: stats.rss_bytes.load(Ordering::Relaxed),

        clients_current,
        clients_total_connected: stats.clients_total_connected.load(Ordering::Relaxed),
        clients_total_disconnected: stats.clients_total_disconnected.load(Ordering::Relaxed),

        total_msgs_rx: msgs_rx,
        total_msgs_tx: msgs_tx,
        total_bytes_rx: bytes_rx,
        total_bytes_tx: bytes_tx,
        total_msgs_dropped: stats.total_msgs_dropped.load(Ordering::Relaxed),

        rx_bytes_per_sec: rx_bps,
        tx_bytes_per_sec: tx_bps,
        rx_msgs_per_sec: rx_mps,
        tx_msgs_per_sec: tx_mps,

        physics_tick_p50_us: tick_p50,
        physics_tick_p95_us: tick_p95,
        physics_tick_p99_us: tick_p99,
        physics_broadcast_p50_us: bc_p50,
        physics_broadcast_p95_us: bc_p95,
        physics_broadcast_p99_us: bc_p99,

        rooms: room_snapshots,
        clients: client_snapshots,
        msgs_rx_by_type,
        msgs_rx_ctrl_subtype,
        log: log_lines,
    }
}

/// Pretty-name the top-level message byte for the dashboard table.
fn msg_type_name(b: u8) -> String {
    match b {
        0x03 => "MSG_INTERACTION_UPDATE (0x03)",
        0x04 => "MSG_VARIABLE_UPDATE (0x04)",
        0x06 => "MSG_EVENT (0x06)",
        0x07 => "MSG_ANIMATOR (0x07)",
        0x09 => "MSG_SCALED_INTERACTION (0x09)",
        0x0B => "MSG_POSITION_VELOCITY (0x0B)",
        0x0C => "MSG_PLAYER_POS (0x0C)",
        0x0D => "MSG_PUSH_IMPULSE (0x0D)",
        0x0E => "MSG_TIME_PING (0x0E)",
        0x0F => "MSG_POSITION_COMPACT (0x0F)",
        0x10 => "MSG_SCENE_INIT (0x10)",
        0x12 => "MSG_VARIABLE_SNAPSHOT (0x12)",
        0xFF => "MSG_CONTROL (0xFF)",
        _ => return format!("unknown (0x{:02X})", b),
    }
    .to_string()
}

fn ctrl_subtype_name(b: u8) -> String {
    match b {
        0x01 => "CTRL_WELCOME (0x01)",
        0x02 => "CTRL_CLIENT_CAPS (0x02)",
        0x03 => "CTRL_STATE_WRITER (0x03)",
        0x05 => "CTRL_CLAIM (0x05)",
        0x06 => "CTRL_RELEASE (0x06)",
        0x07 => "CTRL_CLAIM_REJECTED (0x07)",
        0x08 => "CTRL_PHYSICS_READY (0x08)",
        0x0B => "CTRL_SPAWN_REQUEST (0x0B)",
        0x0C => "CTRL_SPAWN (0x0C)",
        0x0D => "CTRL_DESPAWN (0x0D)",
        0x0E => "CTRL_TIME_PONG (0x0E)",
        0x20 => "CTRL_PLAYER_IDENT (0x20)",
        0x21 => "CTRL_PERSIST_SET (0x21)",
        0x22 => "CTRL_PERSIST_GET (0x22)",
        0x23 => "CTRL_PERSIST_DATA (0x23)",
        _ => return format!("unknown ctrl (0x{:02X})", b),
    }
    .to_string()
}

fn percentiles_from_mutex(
    m: &std::sync::Mutex<std::collections::VecDeque<u64>>,
) -> (u64, u64, u64) {
    let samples: Vec<u64> = match m.lock() {
        Ok(v) => v.iter().copied().collect(),
        Err(_) => return (0, 0, 0),
    };
    if samples.is_empty() {
        return (0, 0, 0);
    }
    let mut s = samples;
    s.sort_unstable();
    let n = s.len();
    let at = |pct: usize| s[((pct * (n - 1)) / 100).min(n - 1)];
    (at(50), at(95), at(99))
}
