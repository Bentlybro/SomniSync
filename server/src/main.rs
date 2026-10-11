mod client;
mod config;
mod handler;
mod metrics_server;
mod persist_rpc;
mod persistence;
mod physics;
mod protocol;
mod room;
mod scene;
mod spawn;
mod stats;
mod variable_state;

use log::info;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::net::TcpListener;
use tokio::sync::{RwLock, Semaphore};

use config::ServerConfig;
use persistence::Db;
use room::RoomMap;

#[tokio::main]
async fn main() {
    // Stats lifetime is the whole process — set up before the logger so the
    // tee-logger has a ring buffer to push into from the very first log line.
    let stats = stats::Stats::new();
    stats::init(stats.clone());
    stats::install_logger(stats.clone());

    let config = ServerConfig::from_args();

    // Keep persistence independent of the process working directory in service installs.
    std::fs::create_dir_all(&config.data_dir).expect("Failed to create data directory");
    let db_path = std::path::Path::new(&config.data_dir).join("persistence.db");
    let db = Db::open(db_path.to_str().expect("Data path is not UTF-8"))
        .expect("Failed to open persistence database");
    info!(
        "[Server] Persistence database ready at {}",
        db_path.display()
    );

    let listener = TcpListener::bind(&config.addr)
        .await
        .expect("Failed to bind");

    info!(
        "[Server] SomniSync Relay v2.2 started on ws://{}/",
        config.addr
    );
    info!("[Server] Multi-room — connect to ws://server/{{room_id}}");
    info!("[Server] Ready for connections...");

    let rooms: RoomMap = Arc::new(RwLock::new(HashMap::new()));
    // Bound concurrent handshakes as well as established WebSockets.
    let connection_slots = Arc::new(Semaphore::new(512));

    // Background: refresh CPU% / RSS at 1 Hz for the dashboard.
    tokio::spawn(stats::sysinfo_loop(stats.clone()));

    // Background: HTTP dashboard on a second port. If it fails to bind we
    // log and keep running — game traffic shouldn't die because the
    // dashboard port was taken.
    {
        let metrics_stats = stats.clone();
        let metrics_rooms = rooms.clone();
        let metrics_addr = config.metrics_addr.clone();
        tokio::spawn(async move {
            metrics_server::serve(metrics_addr, metrics_stats, metrics_rooms).await;
        });
    }

    let shutdown = tokio::signal::ctrl_c();
    tokio::pin!(shutdown);

    loop {
        tokio::select! {
            Ok((stream, addr)) = listener.accept() => {
                let Ok(permit) = connection_slots.clone().try_acquire_owned() else {
                    log::warn!("[Server] Connection limit reached; rejecting {}", client::redact_peer(addr));
                    continue;
                };
                // Disable Nagle's algorithm — game updates are small and latency-sensitive.
                // Without this, the kernel may buffer small packets for up to 40 ms.
                if let Err(e) = stream.set_nodelay(true) {
                    log::warn!("[Server] TCP_NODELAY failed for {}: {}", client::redact_peer(addr), e);
                }
                let rooms = rooms.clone();
                let db = db.clone();
                tokio::spawn(async move {
                    let _permit = permit;
                    handler::handle_connection(stream, addr, rooms, db).await;
                });
            }
            _ = &mut shutdown => {
                info!("[Server] Shutting down...");
                std::process::exit(0);
            }
        }
    }
}
