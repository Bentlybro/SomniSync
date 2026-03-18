using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SomniumNetwork.Server;

/// <summary>
/// Handles an individual client WebSocket connection.
/// </summary>
public class ClientConnection
{
    private readonly EntityManager _entityManager;
    private readonly NetworkBroadcaster _broadcaster;
    private readonly MessageHandler _messageHandler;
    
    private static int _nextClientId = 1;
    private static readonly object _clientIdLock = new();
    
    public ClientConnection(EntityManager entityManager, NetworkBroadcaster broadcaster, MessageHandler messageHandler)
    {
        _entityManager = entityManager;
        _broadcaster = broadcaster;
        _messageHandler = messageHandler;
    }
    
    /// <summary>
    /// Handle a client connection from start to finish.
    /// </summary>
    public async Task HandleAsync(WebSocket socket)
    {
        // Assign unique client ID
        string clientId;
        
        lock (_clientIdLock)
        {
            clientId = $"client_{_nextClientId++}";
        }
        
        // First client becomes master, or if no master exists
        bool isMasterClient = (_broadcaster.GetMasterId() == null);
        
        _broadcaster.AddClient(clientId, socket);
        
        if (isMasterClient)
        {
            _broadcaster.SetMaster(clientId);
        }
        
        Console.WriteLine($"[ClientConnection] Client connected: {clientId} ({_broadcaster.ClientCount} total) {(isMasterClient ? "[MASTER]" : "")}");
        
        // Send client their ID and master status
        await SendClientIdAsync(socket, clientId, isMasterClient);
        
        // Send all existing entities to new client (pauses updates until done)
        await SyncExistingEntitiesAsync(socket, clientId);
        
        // Receive and process messages
        await ReceiveMessagesAsync(socket, clientId);
        
        // Client disconnected — handle cleanup and host migration
        await HandleDisconnectAsync(clientId);
    }
    
    private async Task SendClientIdAsync(WebSocket socket, string clientId, bool isMasterClient)
    {
        var idMsg = new NetworkMessage
        {
            Type = "client_id",
            OwnerId = clientId,
            IsMasterClient = isMasterClient
        };
        
        Console.WriteLine($"[ClientConnection] Sending to {clientId}: IsMasterClient={isMasterClient}");
        
        await _broadcaster.SendToSocketAsync(socket, idMsg);
    }
    
    private async Task SyncExistingEntitiesAsync(WebSocket socket, string clientId)
    {
        var entities = _entityManager.GetAllEntities();
        
        if (entities.Count == 0)
        {
            _broadcaster.SetClientReady(clientId);
            return;
        }
        
        // Send ONE batch_sync message with all entities (not 500 individual spawns)
        var spawns = entities.Select(entity => new EntitySpawnData
        {
            EntityId = entity.Id,
            EntityType = entity.Type,
            Position = entity.Position,
            Rotation = entity.Rotation,
            OwnerId = _entityManager.GetOwner(entity.Id)
        }).ToList();
        
        var batchMsg = new NetworkMessage
        {
            Type = "batch_sync",
            Spawns = spawns
        };
        
        await _broadcaster.SendToSocketAsync(socket, batchMsg);
        
        Console.WriteLine($"[ClientConnection] Sent batch_sync with {entities.Count} entities to {clientId}");
        
        // Now mark client ready for real-time updates
        _broadcaster.SetClientReady(clientId);
    }
    
    private async Task HandleDisconnectAsync(string clientId)
    {
        Console.WriteLine($"[ClientConnection] Client disconnected: {clientId} ({_broadcaster.ClientCount - 1} remaining)");
        
        // Remove client and check if host migration is needed
        string? newMasterId = _broadcaster.RemoveClient(clientId);
        
        if (newMasterId != null)
        {
            // Transfer entity ownership from old master to new master
            var transferredEntities = _entityManager.TransferOwnership(clientId, newMasterId);
            
            // Notify the new master
            var masterMsg = new NetworkMessage
            {
                Type = "master_changed",
                OwnerId = newMasterId,
                IsMasterClient = true
            };
            
            // Send to all clients so everyone knows who the new master is
            await _broadcaster.BroadcastMessageAsync(masterMsg);
            
            Console.WriteLine($"[ClientConnection] Host migration complete: {newMasterId} is now master with {transferredEntities.Count} entities");
        }
    }
    
    private async Task ReceiveMessagesAsync(WebSocket socket, string clientId)
    {
        var buffer = new byte[65536]; // 64KB buffer
        var messageBuilder = new StringBuilder();
        
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None
                );
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                {
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    
                    // Only process when we have the complete message
                    if (result.EndOfMessage)
                    {
                        string json = messageBuilder.ToString();
                        messageBuilder.Clear();
                        await _messageHandler.HandleMessageAsync(json, clientId);
                    }
                }
            }
        }
        catch (WebSocketException)
        {
            // Normal disconnect
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("internal WebSocket error"))
            {
                Console.WriteLine($"[ClientConnection] Error: {ex.Message}");
            }
        }
    }
}
