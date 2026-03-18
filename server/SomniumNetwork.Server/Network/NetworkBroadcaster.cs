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
    private readonly ConcurrentDictionary<string, bool> _clientReady = new(); // false = syncing initial state
    private string? _masterClientId = null;
    private readonly object _masterLock = new();
    
    /// <summary>
    /// Register a new client connection with its ID.
    /// </summary>
    public void AddClient(string clientId, WebSocket socket)
    {
        _clients[clientId] = socket;
        _sendLocks[clientId] = new SemaphoreSlim(1, 1);
        _clientReady[clientId] = false; // Not ready until initial sync complete
    }
    
    public void SetClientReady(string clientId)
    {
        _clientReady[clientId] = true;
        Console.WriteLine($"[NetworkBroadcaster] Client {clientId} ready for updates");
    }
    
    /// <summary>
    /// Remove a disconnected client and handle host migration if needed.
    /// Returns the new master client ID if migration occurred, null otherwise.
    /// </summary>
    public string? RemoveClient(string clientId)
    {
        _clients.TryRemove(clientId, out _);
        _sendLocks.TryRemove(clientId, out _);
        _clientReady.TryRemove(clientId, out _);
        
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
    /// Broadcast entity updates using binary protocol.
    /// Format: [1 byte type=0x01][2 bytes count][per entity: 16 bytes GUID + 24 bytes floats]
    /// 40 bytes per entity vs ~120 bytes JSON = 3x smaller + zero parse overhead
    /// </summary>
    public async Task BroadcastUpdatesAsync(List<Entity> movedEntities)
    {
        if (movedEntities.Count == 0)
            return;
        
        int count = movedEntities.Count;
        int bufferSize = 3 + (count * 40); // header + entities
        byte[] buffer = new byte[bufferSize];
        
        // Header
        buffer[0] = 0x01; // Binary update message type
        BitConverter.TryWriteBytes(buffer.AsSpan(1, 2), (ushort)count);
        
        // Pack entities
        int offset = 3;
        for (int i = 0; i < count; i++)
        {
            var entity = movedEntities[i];
            
            // 16 bytes GUID
            entity.BinaryId.TryWriteBytes(buffer.AsSpan(offset, 16));
            offset += 16;
            
            // Position (12 bytes)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Position.X);
            offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Position.Y);
            offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Position.Z);
            offset += 4;
            
            // Rotation (12 bytes)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Rotation.X);
            offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Rotation.Y);
            offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Rotation.Z);
            offset += 4;
        }
        
        var segment = new ArraySegment<byte>(buffer, 0, offset);
        
        // Send as binary WebSocket message (skip clients still syncing)
        var tasks = new List<Task>();
        int readyCount = 0;
        int skippedCount = 0;
        foreach (var kvp in _clients)
        {
            if (kvp.Value.State != WebSocketState.Open)
                continue;
            
            bool isReady = _clientReady.TryGetValue(kvp.Key, out var ready) && ready;
            if (isReady)
            {
                tasks.Add(SendBinaryWithLockAsync(kvp.Key, kvp.Value, segment));
                readyCount++;
            }
            else
            {
                skippedCount++;
            }
        }
        
        // Log once per second if clients are being skipped
        if (skippedCount > 0 && count > 0 && DateTime.UtcNow.Second % 5 == 0)
        {
            Console.WriteLine($"[NetworkBroadcaster] Binary update: {count} entities, sent to {readyCount} ready clients, skipped {skippedCount} not-ready clients. Ready states: {string.Join(", ", _clientReady.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }
        
        try { await Task.WhenAll(tasks); }
        catch { /* errors handled per-client in SendBinaryWithLockAsync */ }
    }
    
    private async Task SendBinaryWithLockAsync(string clientId, WebSocket socket, ArraySegment<byte> data)
    {
        if (!_sendLocks.TryGetValue(clientId, out var semaphore))
            return;
        
        await semaphore.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NetworkBroadcaster] Binary send failed to {clientId}: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }
}
