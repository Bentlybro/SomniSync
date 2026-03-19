mod client;
mod config;
mod handler;
mod protocol;
mod room;

use log::info;
use std::sync::Arc;
use tokio::net::TcpListener;
use tokio::sync::RwLock;

use config::ServerConfig;
use room::Room;

#[tokio::main]
async fn main() {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    let config = ServerConfig::from_args();

    let listener = TcpListener::bind(&config.addr)
        .await
        .expect("Failed to bind");

    info!("[Server] Somnium Relay v2.1 started on ws://{}/", config.addr);
    info!("[Server] Persistence: {}", if config.persistence_enabled { "enabled" } else { "disabled" });
    info!("[Server] Ready for connections...");

    let room = Arc::new(RwLock::new(Room::new()));

    let shutdown = tokio::signal::ctrl_c();
    tokio::pin!(shutdown);

    loop {
        tokio::select! {
            Ok((stream, addr)) = listener.accept() => {
                let room = room.clone();
                tokio::spawn(handler::handle_connection(stream, addr, room));
            }
            _ = &mut shutdown => {
                info!("[Server] Shutting down...");
                std::process::exit(0);
            }
        }
    }
}
