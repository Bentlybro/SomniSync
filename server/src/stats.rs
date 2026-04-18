//! Server-wide metrics + in-memory log buffer for the `/metrics` + `/`
//! dashboard endpoints.
//!
//! Design goals:
//!   * Hot-path writes are lock-free atomics — counters on `Stats` cost a
//!     single `LOCK XADD` on x86, so bumping them from `broadcast_except`
//!     and `handle_binary` is free.
//!   * Rate computation (bytes/sec) is done at *read* time by the HTTP
//!     handler comparing the current sample against the previous — no
//!     rolling-window machinery in the hot path.
//!   * Physics tick + broadcast timings go into small `VecDeque`s under a
//!     `std::sync::Mutex`. The physics task already holds the world mutex,
//!     so one more tiny lock per tick is in the noise.
//!   * Log lines are intercepted by installing a `log::Log` implementation
//!     that both forwards to stdout (normal env_logger-style output) and
//!     pushes into a ring buffer the dashboard reads. Existing `info!()` /
//!     `warn!()` / etc. calls throughout the codebase work unchanged.

use std::collections::{HashMap, VecDeque};
use std::io::Write;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock, RwLock};
use std::sync::Arc;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

/// Cap on retained physics tick / broadcast timing samples. At 50Hz physics
/// this is ~6 seconds of rolling data — enough for meaningful p95/p99 without
/// letting the deque balloon.
const TIMING_SAMPLES: usize = 300;

/// Single log ring the buffer is kept at. The live dashboard fetches the
/// most recent `LOG_TAIL_LIVE` lines from it every refresh; `/export`
/// returns the full buffer. Sized to capture enough history to diagnose
/// a slow-building issue without bloating JSON on the 1Hz path.
const LOG_BUFFER: usize = 2000;

/// How many trailing log lines `/metrics` includes for the live dashboard.
/// Smaller than `LOG_BUFFER` so the 1Hz refresh payload stays lean.
pub const LOG_TAIL_LIVE: usize = 200;

/// Shared, read-mostly counters + small ring buffers. The whole struct
/// lives behind an `Arc` so both the WebSocket handler and the HTTP
/// dashboard see the same atomic counters without copies.
pub struct Stats {
    pub started_at: Instant,

    /// Process CPU %, scaled × 100 (so 143 = 1.43 %). Refreshed by a
    /// background task at 1 Hz — sysinfo::refresh_process is not free.
    pub cpu_percent_x100: AtomicU32,
    /// Resident-set size in bytes.
    pub rss_bytes: AtomicU64,

    /// Total websocket clients seen across the lifetime of the process.
    pub clients_total_connected: AtomicU64,
    /// Total disconnects. `connected - disconnected` ≈ live client count,
    /// but the authoritative number is obtained by summing room.client_count.
    pub clients_total_disconnected: AtomicU64,

    /// Incoming messages / bytes. Incremented once per `handle_binary` call
    /// (regardless of whether the server ultimately acted on the message).
    pub total_msgs_rx: AtomicU64,
    pub total_bytes_rx: AtomicU64,

    /// Outgoing messages / bytes. Incremented once per successful enqueue
    /// into a client's send channel. Failed enqueues (buffer-full drops) are
    /// counted separately as `total_msgs_dropped`.
    pub total_msgs_tx: AtomicU64,
    pub total_bytes_tx: AtomicU64,
    pub total_msgs_dropped: AtomicU64,

    /// Ring buffers of last-N microsecond durations. Physics tick = the
    /// rapier step() call. Broadcast = the snapshot build + send-queue push.
    pub physics_tick_us: Mutex<VecDeque<u64>>,
    pub physics_broadcast_us: Mutex<VecDeque<u64>>,

    /// Captured log lines (structured) for the dashboard.
    pub log_lines: Mutex<VecDeque<LogLine>>,

    /// Received-message counters bucketed by first byte (message type). 256
    /// slots in heap-allocated `Box<[AtomicU64]>`. Indexed directly by
    /// `data[0]`. Gives the dashboard a "what kind of traffic is hitting us"
    /// breakdown — player-pos vs interaction vs event, etc.
    pub msgs_rx_by_type: Box<[AtomicU64]>,

    /// For 0xFF (MSG_CONTROL) messages, also bucket by the control subtype
    /// byte. Empty (all zero) for messages that aren't control.
    pub msgs_rx_ctrl_subtype: Box<[AtomicU64]>,

    /// Per-client tracking: address, room, connect time, bytes/msgs seen.
    /// RwLock read-mostly — insert on connect, remove on disconnect, else
    /// only atomic counter reads. Metrics snapshot takes a brief read lock.
    pub clients: RwLock<HashMap<u16, Arc<ClientInfo>>>,
}

#[derive(Clone)]
pub struct LogLine {
    pub ts_unix_ms: u64,
    pub level: &'static str,
    pub msg: String,
}

/// Per-client stats. Wrapped in `Arc` so the metrics endpoint can snapshot
/// the map + drop its read lock, then consult each client's atomic counters
/// at its leisure.
pub struct ClientInfo {
    pub cid: u16,
    pub addr: String,
    pub room: String,
    pub connected_at_ms: u64,
    pub msgs_rx: AtomicU64,
    pub bytes_rx: AtomicU64,
}

impl Stats {
    pub fn new() -> Arc<Self> {
        let by_type: Vec<AtomicU64> = (0..256).map(|_| AtomicU64::new(0)).collect();
        let by_ctrl: Vec<AtomicU64> = (0..256).map(|_| AtomicU64::new(0)).collect();
        Arc::new(Self {
            started_at: Instant::now(),
            cpu_percent_x100: AtomicU32::new(0),
            rss_bytes: AtomicU64::new(0),
            clients_total_connected: AtomicU64::new(0),
            clients_total_disconnected: AtomicU64::new(0),
            total_msgs_rx: AtomicU64::new(0),
            total_bytes_rx: AtomicU64::new(0),
            total_msgs_tx: AtomicU64::new(0),
            total_bytes_tx: AtomicU64::new(0),
            total_msgs_dropped: AtomicU64::new(0),
            physics_tick_us: Mutex::new(VecDeque::with_capacity(TIMING_SAMPLES)),
            physics_broadcast_us: Mutex::new(VecDeque::with_capacity(TIMING_SAMPLES)),
            log_lines: Mutex::new(VecDeque::with_capacity(LOG_BUFFER)),
            msgs_rx_by_type: by_type.into_boxed_slice(),
            msgs_rx_ctrl_subtype: by_ctrl.into_boxed_slice(),
            clients: RwLock::new(HashMap::new()),
        })
    }

    /// Record a physics step timing. Cheap — push-with-cap on a small deque.
    pub fn record_physics_tick(&self, us: u64) {
        if let Ok(mut v) = self.physics_tick_us.lock() {
            if v.len() >= TIMING_SAMPLES { v.pop_front(); }
            v.push_back(us);
        }
    }

    pub fn record_physics_broadcast(&self, us: u64) {
        if let Ok(mut v) = self.physics_broadcast_us.lock() {
            if v.len() >= TIMING_SAMPLES { v.pop_front(); }
            v.push_back(us);
        }
    }
}

static STATS: OnceLock<Arc<Stats>> = OnceLock::new();

/// Install the global Stats singleton. Call once at startup, before any
/// hot-path code might try to reach it.
pub fn init(stats: Arc<Stats>) {
    let _ = STATS.set(stats);
}

/// Access the global Stats. Returns `None` if `init` hasn't been called —
/// the hot-path helpers below guard on that so nothing panics if stats
/// wiring breaks mid-refactor.
#[inline]
pub fn get() -> Option<&'static Arc<Stats>> {
    STATS.get()
}

// ── Hot-path helpers (cheap, always-on) ────────────────────────────────────
#[inline]
pub fn bump_rx(bytes: usize) {
    if let Some(s) = STATS.get() {
        s.total_msgs_rx.fetch_add(1, Ordering::Relaxed);
        s.total_bytes_rx.fetch_add(bytes as u64, Ordering::Relaxed);
    }
}

/// Record per-type RX buckets. For 0xFF control messages, also bumps the
/// subtype counter. Cheap — two atomic adds at most.
#[inline]
pub fn bump_rx_type(data: &[u8]) {
    if data.is_empty() { return; }
    if let Some(s) = STATS.get() {
        s.msgs_rx_by_type[data[0] as usize].fetch_add(1, Ordering::Relaxed);
        if data[0] == 0xFF && data.len() >= 2 {
            s.msgs_rx_ctrl_subtype[data[1] as usize].fetch_add(1, Ordering::Relaxed);
        }
    }
}

/// Register a newly-connected client. Called once from the handler after
/// a successful handshake. Cheap: one RwLock write + one HashMap insert.
pub fn register_client(cid: u16, addr: SocketAddr, room: &str) {
    if let Some(s) = STATS.get() {
        let connected_at_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0);
        let info = Arc::new(ClientInfo {
            cid,
            addr: addr.to_string(),
            room: room.to_string(),
            connected_at_ms,
            msgs_rx: AtomicU64::new(0),
            bytes_rx: AtomicU64::new(0),
        });
        if let Ok(mut map) = s.clients.write() {
            map.insert(cid, info);
        }
    }
}

/// Remove a client on disconnect.
pub fn unregister_client(cid: u16) {
    if let Some(s) = STATS.get() {
        if let Ok(mut map) = s.clients.write() {
            map.remove(&cid);
        }
    }
}

/// Per-client RX counter bump. Reads the map under a read lock (no write
/// contention with other clients sending), then increments atomics on the
/// Arc'd ClientInfo. Map read is O(1), entirely lock-free once the read
/// lock is held.
#[inline]
pub fn bump_client_rx(cid: u16, bytes: usize) {
    if let Some(s) = STATS.get() {
        if let Ok(map) = s.clients.read() {
            if let Some(info) = map.get(&cid) {
                info.msgs_rx.fetch_add(1, Ordering::Relaxed);
                info.bytes_rx.fetch_add(bytes as u64, Ordering::Relaxed);
            }
        }
    }
}

#[inline]
pub fn bump_tx(bytes: usize) {
    if let Some(s) = STATS.get() {
        s.total_msgs_tx.fetch_add(1, Ordering::Relaxed);
        s.total_bytes_tx.fetch_add(bytes as u64, Ordering::Relaxed);
    }
}

#[inline]
pub fn bump_tx_batch(bytes: usize, count: u64) {
    if count == 0 { return; }
    if let Some(s) = STATS.get() {
        s.total_msgs_tx.fetch_add(count, Ordering::Relaxed);
        s.total_bytes_tx.fetch_add(bytes as u64 * count, Ordering::Relaxed);
    }
}

#[inline]
pub fn bump_dropped() {
    if let Some(s) = STATS.get() {
        s.total_msgs_dropped.fetch_add(1, Ordering::Relaxed);
    }
}

#[inline]
pub fn bump_client_connected() {
    if let Some(s) = STATS.get() {
        s.clients_total_connected.fetch_add(1, Ordering::Relaxed);
    }
}

#[inline]
pub fn bump_client_disconnected() {
    if let Some(s) = STATS.get() {
        s.clients_total_disconnected.fetch_add(1, Ordering::Relaxed);
    }
}

// ── Sysinfo refresh loop ────────────────────────────────────────────────────
/// Run forever, refreshing CPU% + RSS at 1 Hz. Spawn once from main().
///
/// Two refresh calls per tick:
///   1. `refresh_cpu_usage()` — rebuilds global CPU time counters. Without
///      this every tick, `Process::cpu_usage()` returns 0 forever because
///      it needs a recent global sample to compute its delta against.
///   2. `refresh_processes_specifics(...)` — updates our own process's
///      CPU/mem snapshot.
///
/// Sysinfo also requires at least one full sample *before* cpu_usage()
/// returns non-zero (it needs two samples to differentiate), which is why
/// the first reading after startup will briefly read 0 — it recovers
/// after one interval.
pub async fn sysinfo_loop(stats: Arc<Stats>) {
    use sysinfo::{ProcessRefreshKind, System};

    let pid = sysinfo::get_current_pid().ok();
    let mut sys = System::new();

    // Initial refresh — seeds the global CPU time counter so the very next
    // refresh_cpu_usage() call has a baseline to diff against.
    sys.refresh_cpu_usage();
    if let Some(pid) = pid {
        sys.refresh_processes_specifics(
            sysinfo::ProcessesToUpdate::Some(&[pid]),
            true,
            ProcessRefreshKind::everything().with_cpu(),
        );
    }

    let mut interval = tokio::time::interval(std::time::Duration::from_secs(1));
    interval.tick().await; // immediate first tick
    loop {
        interval.tick().await;
        if let Some(pid) = pid {
            // Global CPU first, then process — if we skip the global call,
            // Process::cpu_usage() has nothing to compare against and
            // silently returns 0. That was the whole-dashboard-says-0.00%
            // bug reported in the first export.
            sys.refresh_cpu_usage();
            sys.refresh_processes_specifics(
                sysinfo::ProcessesToUpdate::Some(&[pid]),
                true,
                ProcessRefreshKind::everything().with_cpu(),
            );
            if let Some(proc) = sys.process(pid) {
                let cpu = proc.cpu_usage(); // already in percent (per-core scale)
                stats.cpu_percent_x100.store((cpu * 100.0) as u32, Ordering::Relaxed);
                stats.rss_bytes.store(proc.memory(), Ordering::Relaxed);
            }
        }
    }
}

// ── log::Log implementation that tees stdout + ring buffer ──────────────────
/// Custom logger. Replaces env_logger. Writes the same line format to
/// stdout (so the server's terminal looks identical) and also pushes it
/// into the Stats ring buffer for the dashboard's `/metrics` → log tail.
struct TeeLogger {
    stats: Arc<Stats>,
    filter: log::LevelFilter,
}

impl log::Log for TeeLogger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= self.filter
    }

    fn log(&self, record: &log::Record) {
        if !self.enabled(record.metadata()) { return; }

        let ts_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0);
        let msg = format!("{}", record.args());

        // stdout — matches env_logger's default formatting closely enough.
        let mut stdout = std::io::stdout().lock();
        let _ = writeln!(
            stdout,
            "[{} {} {}] {}",
            format_ts_hms(ts_ms),
            record.level(),
            record.target(),
            msg,
        );

        // ring buffer for dashboard
        if let Ok(mut ring) = self.stats.log_lines.lock() {
            if ring.len() >= LOG_BUFFER { ring.pop_front(); }
            ring.push_back(LogLine {
                ts_unix_ms: ts_ms,
                level: level_str(record.level()),
                msg,
            });
        }
    }

    fn flush(&self) {
        let _ = std::io::stdout().flush();
    }
}

fn level_str(l: log::Level) -> &'static str {
    match l {
        log::Level::Error => "ERROR",
        log::Level::Warn  => "WARN",
        log::Level::Info  => "INFO",
        log::Level::Debug => "DEBUG",
        log::Level::Trace => "TRACE",
    }
}

fn format_ts_hms(ms: u64) -> String {
    // Cheap HH:MM:SS.mmm formatter to avoid pulling chrono.
    let secs_of_day = (ms / 1000) % 86_400;
    let h = secs_of_day / 3600;
    let m = (secs_of_day % 3600) / 60;
    let s = secs_of_day % 60;
    let ms_part = ms % 1000;
    format!("{:02}:{:02}:{:02}.{:03}", h, m, s, ms_part)
}

/// Install the tee logger. Pass in the shared `Stats` so log records land
/// in the dashboard ring. Filter respects the `RUST_LOG` env var (falls
/// back to `info` — matches the old env_logger default).
pub fn install_logger(stats: Arc<Stats>) {
    let filter = std::env::var("RUST_LOG")
        .ok()
        .and_then(|v| parse_level(&v))
        .unwrap_or(log::LevelFilter::Info);

    let logger = TeeLogger { stats, filter };
    log::set_max_level(filter);
    // Box::leak keeps the logger alive for the whole process lifetime —
    // log::set_logger requires a &'static dyn Log. One intentional leak,
    // once per process start.
    let _ = log::set_logger(Box::leak(Box::new(logger)));
}

fn parse_level(s: &str) -> Option<log::LevelFilter> {
    // Accept either a bare level (`info`, `debug`) or one of the comma-
    // separated forms env_logger supports, in which case we just take the
    // last token's level and ignore module filters — this isn't a full
    // env_logger replacement, just a sensible default.
    let last = s.split(',').last().unwrap_or(s).trim();
    let lvl = last.rsplit('=').next().unwrap_or(last).trim().to_ascii_lowercase();
    match lvl.as_str() {
        "off"   => Some(log::LevelFilter::Off),
        "error" => Some(log::LevelFilter::Error),
        "warn"  => Some(log::LevelFilter::Warn),
        "info"  => Some(log::LevelFilter::Info),
        "debug" => Some(log::LevelFilter::Debug),
        "trace" => Some(log::LevelFilter::Trace),
        _ => None,
    }
}
