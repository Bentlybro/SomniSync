using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SomniumNetwork.Server;

/// <summary>
/// Handles broadcasting network messages to all connected clients.
/// Tracks client sockets by ID for targeted messaging and host migration.
/// </summary>
public class NetworkBroadcaster
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();
    private string? _masterClientId = null;
    private readonly object _masterLock = new();
    
    /// <summary>
    /// Register a new client connection with its ID.
    /// </summary>
    public void AddClient(string clientId, WebSocket socket)
    {
        _clients[clientId] = socket;
        _sendLocks[clientId] = new SemaphoreSlim(1, 1);
    }
    
    /// <summary>
    /// Remove a disconnected client and handle host migration if needed.
    /// Returns the new master client ID if migration occurred, null otherwise.
    /// </summary>
    public string? RemoveClient(string clientId)
    {
        _clients.TryRemove(clientId, out _);
        _sendLocks.TryRemove(clientId, out _);
        
        lock (_masterLock)
        {
            // If the master disconnected, promote the next client
            if (_masterClientId == clientId)
            {
                _masterClientId = null;
                
                // Find next connected client to promote
                foreach (var kvp in _clients)
                {
                    if (kvp.Value.State == WebSocketState.Open)
                    {
                        _masterClientId = kvp.Key;
                        Console.WriteLine($"[NetworkBroadcaster] Host migration: {clientId} -> {_masterClientId}");
                        return _masterClientId;
                    }
                }
                
                Console.WriteLine($"[NetworkBroadcaster] Master disconnected, no clients to promote");
                return null;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Set the current master client.
    /// </summary>
    public void SetMaster(string clientId)
    {
        lock (_masterLock)
        {
            _masterClientId = clientId;
        }
    }
    
    /// <summary>
    /// Get the current master client ID.
    /// </summary>
    public string? GetMasterId()
    {
        lock (_masterLock)
        {
            return _masterClientId;
        }
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
        var segment = new ArraySegment<byte>(bytes);
        
        var tasks = new List<Task>();
        
        foreach (var kvp in _clients)
        {
            if (kvp.Value.State == WebSocketState.Open)
            {
                tasks.Add(SendWithLockAsync(kvp.Key, kvp.Value, segment));
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
    
    private async Task SendWithLockAsync(string clientId, WebSocket socket, ArraySegment<byte> data)
    {
        if (!_sendLocks.TryGetValue(clientId, out var semaphore))
            return;
        
        await semaphore.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NetworkBroadcaster] Send failed to {clientId}: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    /// <summary>
    /// Send a message to a specific client by ID.
    /// </summary>
    public async Task SendToClientAsync(string clientId, NetworkMessage message)
    {
        if (!_clients.TryGetValue(clientId, out var socket) || socket.State != WebSocketState.Open)
            return;
        
        await SendToSocketAsync(socket, message);
    }
    
    /// <summary>
    /// Send a message to a specific socket.
    /// </summary>
    public async Task SendToSocketAsync(WebSocket socket, NetworkMessage message)
    {
        if (socket.State != WebSocketState.Open)
            return;
        
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        if (message.Type == "client_id" || message.Type == "master_changed")
        {
            Console.WriteLine($"[NetworkBroadcaster] Sending JSON: {json}");
        }
        
        // Find the lock for this socket
        foreach (var kvp in _clients)
        {
            if (kvp.Value == socket)
            {
                await SendWithLockAsync(kvp.Key, socket, new ArraySegment<byte>(bytes));
                return;
            }
        }
        
        // Fallback: no lock found (e.g. during initial setup), send directly
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
