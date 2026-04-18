//! HTTP dashboard for runtime metrics.
//!
//! Serves two routes on a separate port from the WebSocket listener:
//!   * `/`         — embedded HTML dashboard (no build step, zero JS deps).
//!   * `/metrics`  — JSON snapshot of counters, room state, physics timing,
//!                   and the in-memory log ring buffer.
//!
//! The dashboard is a standalone single file that polls `/metrics` every
//! second. Rates (bytes/sec, msgs/sec) are computed here per-request as
//! deltas from the previous sample — no rolling-window bookkeeping lives on
//! the hot path. Previous-sample state is scoped to each HTTP client's
//! session via a shared `Arc<Mutex<PrevSample>>` so multiple browsers don't
//! trample each other's rate math.

use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use axum::{
    extract::State,
    response::Html,
    routing::get,
    Json, Router,
};
use log::info;
use serde::Serialize;
use tokio::net::TcpListener;
use tokio::sync::Mutex as AsyncMutex;

use crate::room::RoomMap;
use crate::stats::Stats;

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
        Self { at: None, bytes_rx: 0, bytes_tx: 0, msgs_rx: 0, msgs_tx: 0 }
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
        .with_state(state);

    match TcpListener::bind(&addr).await {
        Ok(listener) => {
            info!("[Metrics] Dashboard listening on http://{}/", addr);
            if let Err(e) = axum::serve(listener, app).await {
                log::warn!("[Metrics] Serve error: {}", e);
            }
        }
        Err(e) => {
            log::warn!("[Metrics] Failed to bind {}: {}. Dashboard disabled.", addr, e);
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
    log: Vec<LogLineOut>,
}

#[derive(Serialize)]
struct RoomSnapshot {
    id: String,
    clients: usize,
    physics_ready: bool,
    object_count: u16,
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
    let (tick_p50, tick_p95, tick_p99) =
        percentiles_from_mutex(&stats.physics_tick_us);
    let (bc_p50, bc_p95, bc_p99) =
        percentiles_from_mutex(&stats.physics_broadcast_us);

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
                    object_count: r.next_runtime_id,
                    claimed_count: r.claimed_objects.len(),
                    scene_ready_ms: r.scene_ready_ms,
                });
            }
        }
    }

    // Log tail — collect under the sync lock, release, THEN build output.
    let log_lines: Vec<LogLineOut> = {
        match stats.log_lines.lock() {
            Ok(lines) => lines
                .iter()
                .map(|l| LogLineOut {
                    ts_unix_ms: l.ts_unix_ms,
                    level: l.level,
                    msg: l.msg.clone(),
                })
                .collect(),
            Err(_) => Vec::new(),
        }
    };

    let sampled_at_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);

    Json(MetricsSnapshot {
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
        log: log_lines,
    })
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
