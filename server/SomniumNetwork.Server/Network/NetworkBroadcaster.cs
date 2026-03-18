using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SomniumNetwork.Server;

/// <summary>
/// Handles broadcasting network messages to all connected clients.
/// </summary>
public class NetworkBroadcaster
{
    private readonly ConcurrentBag<WebSocket> _clients = new();
    
    /// <summary>
    /// Register a new client connection.
    /// </summary>
    public void AddClient(WebSocket socket)
    {
        _clients.Add(socket);
    }
    
    /// <summary>
    /// Get the total number of connected clients.
    /// </summary>
    public int ClientCount => _clients.Count;
    
    /// <summary>
    /// Broadcast a message to all connected clients.
    /// </summary>
    public async Task BroadcastMessageAsync(NetworkMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        // Log large messages for debugging
        if (bytes.Length > 50000)
        {
            Console.WriteLine($"[NetworkBroadcaster] Large message: {bytes.Length / 1024}KB, {message.Updates?.Count ?? 0} updates");
        }
        
        var tasks = new List<Task>();
        
        foreach (var client in _clients)
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    tasks.Add(client.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None
                    ));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NetworkBroadcaster] Send failed: {ex.Message}");
                }
            }
        }
        
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NetworkBroadcaster] Broadcast error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Send a message to a specific client.
    /// </summary>
    public async Task SendToClientAsync(WebSocket socket, NetworkMessage message)
    {
        if (socket.State != WebSocketState.Open)
            return;
        
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NetworkBroadcaster] Send to client failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Broadcast entity updates (delta compression).
    /// </summary>
    public async Task BroadcastUpdatesAsync(List<Entity> movedEntities)
    {
        if (movedEntities.Count == 0)
            return;
        
        var updates = movedEntities.Select(e => new EntityUpdate
        {
            Id = e.Id,
            Position = e.Position,
            Rotation = e.Rotation
        }).ToList();
        
        var message = new NetworkMessage
        {
            Type = "update",
            Updates = updates
        };
        
        await BroadcastMessageAsync(message);
    }
}
