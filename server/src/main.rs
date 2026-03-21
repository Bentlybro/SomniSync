mod client;
mod config;
mod handler;
mod protocol;
mod room;

use log::info;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::net::TcpListener;
use tokio::sync::RwLock;

use config::ServerConfig;
use room::RoomMap;

#[tokio::main]
async fn main() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    let config = ServerConfig::from_args();

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
                let rooms = rooms.clone();
                tokio::spawn(handler::handle_connection(stream, addr, rooms));
            }
            _ = &mut shutdown => {
                info!("[Server] Shutting down...");
                std::process::exit(0);
            }
        }
    }
}
