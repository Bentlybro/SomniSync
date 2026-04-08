use log::warn;
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::sync::Mutex;
use tokio_tungstenite::tungstenite::Message;

pub type Tx = futures_util::stream::SplitSink<
    tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>,
    Message,
>;

pub struct Client {
    pub tx: Mutex<Tx>,
    pub addr: SocketAddr,
}

/// Send a binary message to a client.
pub async fn send(client: &Client, data: Vec<u8>) {
    use futures_util::SinkExt;
    let mut tx = client.tx.lock().await;
    if let Err(e) = tx.send(Message::Binary(data.into())).await {
        warn!("[Room] Failed to send to {}: {}", client.addr, e);
    }
}

/// Broadcast a binary message to all clients except the sender — concurrent.
pub async fn broadcast_except(
    clients: &std::collections::HashMap<u16, Arc<Client>>,
    sender_id: u16,
    data: &[u8],
) {
    let msg = Message::Binary(data.to_vec().into());

    let futs: Vec<_> = clients
        .iter()
        .filter(|(&id, _)| id != sender_id)
        .map(|(_, c)| {
            let c = c.clone();
            let msg = msg.clone();
            async move {
                use futures_util::SinkExt;
                let mut tx = c.tx.lock().await;
                let _ = tx.send(msg).await;
            }
        })
        .collect();

    futures_util::future::join_all(futs).await;
}
