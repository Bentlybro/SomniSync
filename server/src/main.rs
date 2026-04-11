mod client;
mod config;
mod handler;
mod persistence;
mod physics;
mod protocol;
mod room;

use log::info;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::net::TcpListener;
use tokio::sync::RwLock;

use config::ServerConfig;
use persistence::Db;
use room::RoomMap;

#[tokio::main]
async fn main() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    let config = ServerConfig::from_args();

    // Ensure data directory exists before opening SQLite
    std::fs::create_dir_all("./data").expect("Failed to create ./data directory");
    let db = Db::open("./data/persistence.db").expect("Failed to open persistence database");
    info!("[Server] Persistence database ready at ./data/persistence.db");

    let listener = TcpListener::bind(&config.addr)
        .await
        .expect("Failed to bind");

    info!("[Server] SomniSync Relay v2.2 started on ws://{}/", config.addr);
    info!("[Server] Multi-room — connect to ws://server/{{room_id}}");
    info!("[Server] Ready for connections...");

    let rooms: RoomMap = Arc::new(RwLock::new(HashMap::new()));

    let shutdown = tokio::signal::ctrl_c();
    tokio::pin!(shutdown);

    loop {
        tokio::select! {
            Ok((stream, addr)) = listener.accept() => {
                // Disable Nagle's algorithm — game updates are small and latency-sensitive.
                // Without this, the kernel may buffer small packets for up to 40 ms.
                if let Err(e) = stream.set_nodelay(true) {
                    log::warn!("[Server] TCP_NODELAY failed for {}: {}", addr, e);
                }
                let rooms = rooms.clone();
                let db = db.clone();
                tokio::spawn(handler::handle_connection(stream, addr, rooms, db));
            }
            _ = &mut shutdown => {
                info!("[Server] Shutting down...");
                std::process::exit(0);
            }
        }
    }
}
