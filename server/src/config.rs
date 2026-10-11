/// Server configuration.
///
/// Positional args (anything after the binary name is optional):
///   arg1  WebSocket listen address          default 0.0.0.0:9090
///   arg2  Metrics/dashboard listen address  default 127.0.0.1:9091
///
/// The dashboard binds a second HTTP port so you can bolt it behind a
/// private firewall / nginx basic-auth / bastion without exposing the
/// game socket to the same surface. It defaults to loopback — the
/// dashboard is unauthenticated and `/export` is a bandwidth amplifier,
/// so you have to opt in to exposing it (pass `0.0.0.0:9091` explicitly,
/// ideally behind a reverse proxy with auth).
pub struct ServerConfig {
    /// Listen address for the WebSocket game protocol.
    pub addr: String,
    /// Listen address for the HTTP metrics + dashboard endpoint.
    pub metrics_addr: String,
    /// Directory containing the SQLite database. Keep this on persistent storage.
    pub data_dir: String,
}

impl ServerConfig {
    pub fn from_args() -> Self {
        let mut args = std::env::args().skip(1);
        let first = args.next();
        if matches!(first.as_deref(), Some("--help" | "-h")) {
            println!("Usage: somnium-relay [WS_BIND] [METRICS_BIND]\n\nEnvironment:\n  SOMNISYNC_WS_BIND       default 0.0.0.0:9090\n  SOMNISYNC_METRICS_BIND  default 127.0.0.1:9091\n  SOMNISYNC_DATA_DIR      default ./data\n\nThe metrics endpoint has no authentication. Keep it on loopback or behind an authenticated proxy.");
            std::process::exit(0);
        }
        if matches!(first.as_deref(), Some("--version" | "-V")) {
            println!("somnium-relay {}", env!("CARGO_PKG_VERSION"));
            std::process::exit(0);
        }
        let addr = first.unwrap_or_else(|| env_or("SOMNISYNC_WS_BIND", "0.0.0.0:9090"));
        let metrics_addr = args
            .next()
            .unwrap_or_else(|| env_or("SOMNISYNC_METRICS_BIND", "127.0.0.1:9091"));
        if args.next().is_some() {
            eprintln!("Too many arguments. Run somnium-relay --help for usage.");
            std::process::exit(2);
        }
        let data_dir = env_or("SOMNISYNC_DATA_DIR", "./data");
        Self {
            addr,
            metrics_addr,
            data_dir,
        }
    }
}

fn env_or(key: &str, default: &str) -> String {
    std::env::var(key)
        .ok()
        .filter(|s| !s.trim().is_empty())
        .unwrap_or_else(|| default.to_string())
}
