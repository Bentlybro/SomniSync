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
        string clientId = GenerateClientId();
        
        _broadcaster.AddClient(socket);
        Console.WriteLine($"[ClientConnection] Client connected: {clientId} ({_broadcaster.ClientCount} total)");
        
        // Send client their ID
        await SendClientIdAsync(socket, clientId);
        
        // Send all existing entities to new client
        await SyncExistingEntitiesAsync(socket);
        
        // Receive and process messages
        await ReceiveMessagesAsync(socket, clientId);
        
        Console.WriteLine($"[ClientConnection] Client disconnected: {clientId} ({_broadcaster.ClientCount - 1} remaining)");
    }
    
    private static string GenerateClientId()
    {
        lock (_clientIdLock)
        {
            return $"client_{_nextClientId++}";
        }
    }
    
    private async Task SendClientIdAsync(WebSocket socket, string clientId)
    {
        var idMsg = new NetworkMessage
        {
            Type = "client_id",
            OwnerId = clientId
        };
        
        await _broadcaster.SendToClientAsync(socket, idMsg);
    }
    
    private async Task SyncExistingEntitiesAsync(WebSocket socket)
    {
        var entities = _entityManager.GetAllEntities();
        
        foreach (var entity in entities)
        {
            var msg = new NetworkMessage
            {
                Type = "spawn",
                EntityId = entity.Id,
                EntityType = entity.Type,
                Position = entity.Position,
                Rotation = entity.Rotation,
                OwnerId = _entityManager.GetOwner(entity.Id)
            };
            
            await _broadcaster.SendToClientAsync(socket, msg);
        }
    }
    
    private async Task ReceiveMessagesAsync(WebSocket socket, string clientId)
    {
        var buffer = new byte[65536]; // 64KB buffer
        
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
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await _messageHandler.HandleMessageAsync(json, clientId);
                }
            }
        }
        catch (WebSocketException)
        {
            // Normal disconnect, don't log
        }
        catch (Exception ex)
        {
            // Log unexpected errors only
            if (!ex.Message.Contains("internal WebSocket error"))
            {
                Console.WriteLine($"[ClientConnection] Error: {ex.Message}");
            }
        }
    }
}
