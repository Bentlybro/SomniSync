/// Server configuration.
///
/// Currently compile-time defaults. Future: load from CLI args, env vars, or config file.
pub struct ServerConfig {
    /// Listen address
    pub addr: String,

    /// Enable persistence (future: SQLite key-value store)
    pub persistence_enabled: bool,
}

impl ServerConfig {
    pub fn from_args() -> Self {
        let addr = std::env::args()
            .nth(1)
            .unwrap_or_else(|| "0.0.0.0:9090".to_string());

        Self {
            addr,
            persistence_enabled: false,
        }
    }
}
