/// Server configuration.
///
/// Positional args (anything after the binary name is optional):
///   arg1  WebSocket listen address          default 0.0.0.0:9090
///   arg2  Metrics/dashboard listen address  default 0.0.0.0:9091
///
/// The dashboard binds a second HTTP port so you can bolt it behind a
/// private firewall / nginx basic-auth / bastion without exposing the
/// game socket to the same surface.
pub struct ServerConfig {
    /// Listen address for the WebSocket game protocol.
    pub addr: String,
    /// Listen address for the HTTP metrics + dashboard endpoint.
    pub metrics_addr: String,
}

impl ServerConfig {
    pub fn from_args() -> Self {
        let mut args = std::env::args().skip(1);
        let addr = args.next().unwrap_or_else(|| "0.0.0.0:9090".to_string());
        let metrics_addr = args.next().unwrap_or_else(|| "0.0.0.0:9091".to_string());
        Self { addr, metrics_addr }
    }
}
