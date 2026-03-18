using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SomniumNetwork.Server;

public class GameServer
{
    private readonly Dictionary<string, Entity> _entities = new();
    private readonly Dictionary<string, string> _entityOwners = new(); // EntityId -> ClientId
    private readonly Dictionary<string, WebSocket> _clientSockets = new(); // ClientId -> WebSocket
    private readonly ConcurrentBag<WebSocket> _clients = new();
    private readonly object _entityLock = new();
    private float _time = 0f;
    private int _nextClientId = 1;
    
    public void SpawnEntity(string id, string type, float x, float y, float z, string ownerId)
    {
        lock (_entityLock)
        {
            var entity = new Entity
            {
                Id = id,
                Type = type,
                Position = new Vector3 { X = x, Y = y, Z = z },
                Rotation = new Vector3 { X = 0, Y = 0, Z = 0 }
            };
            
            _entities[id] = entity;
            _entityOwners[id] = ownerId; // Track ownership
            
            if (_entities.Count % 100 == 0)
            {
                Console.WriteLine($"[Server] Total entities: {_entities.Count}");
            }
            
            // Send spawn message to all clients with ownership info
            var msg = new NetworkMessage
            {
                Type = "spawn",
                EntityId = id,
                EntityType = type,
                Position = entity.Position,
                Rotation = entity.Rotation,
                OwnerId = ownerId // Tell clients who owns this
            };
            
            _ = BroadcastMessageAsync(msg);
        }
    }
    
    public void DespawnEntity(string id)
    {
        lock (_entityLock)
        {
            if (_entities.Remove(id))
            {
                var msg = new NetworkMessage
                {
                    Type = "despawn",
                    EntityId = id
                };
                
                _ = BroadcastMessageAsync(msg);
            }
        }
    }
    
    public async Task RunGameLoopAsync(CancellationToken cancellationToken = default)
    {
        var tickRate = 60; // 60 Hz for smooth updates
        var deltaTime = 1f / tickRate;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var startTime = DateTime.UtcNow;
            
            // Update entities
            lock (_entityLock)
            {
                _time += deltaTime;
                
                // Server doesn't move entities by default
                // Entities with client authority (physics objects) will send their own updates
                // Entities with server authority (NPCs) would have AI logic here
            }
            
            // Send updates to all clients
            await BroadcastUpdatesAsync();
            
            // Sleep to maintain tick rate
            var elapsed = DateTime.UtcNow - startTime;
            var sleepTime = TimeSpan.FromSeconds(deltaTime) - elapsed;
            if (sleepTime > TimeSpan.Zero)
            {
                await Task.Delay(sleepTime);
            }
        }
    }
    
    private async Task BroadcastUpdatesAsync()
    {
        var updates = new List<EntityUpdate>();
        
        lock (_entityLock)
        {
            // Only send entities that actually moved (delta compression)
            foreach (var entity in _entities.Values)
            {
                if (entity.HasMoved())
                {
                    updates.Add(new EntityUpdate
                    {
                        Id = entity.Id,
                        Position = entity.Position,
                        Rotation = entity.Rotation
                    });
                    
                    entity.MarkAsSent();
                }
            }
        }
        
        // Only send message if we have updates
        if (updates.Count > 0)
        {
            var msg = new NetworkMessage
            {
                Type = "update",
                Updates = updates
            };
            
            await BroadcastMessageAsync(msg);
        }
    }
    
    private async Task BroadcastMessageAsync(NetworkMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        // Log message size for debugging
        if (bytes.Length > 50000)
        {
            Console.WriteLine($"[Server] Large message: {bytes.Length / 1024}KB, {message.Updates?.Count ?? 0} updates");
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
                    Console.WriteLine($"[Server] Send failed: {ex.Message}");
                }
            }
        }
        
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] Broadcast error: {ex.Message}");
        }
    }
    
    public async Task HandleClientAsync(WebSocket socket)
    {
        // Assign client ID
        string clientId;
        lock (_entityLock)
        {
            clientId = $"client_{_nextClientId++}";
            _clientSockets[clientId] = socket;
        }
        
        _clients.Add(socket);
        Console.WriteLine($"[Server] Client connected: {clientId} ({_clients.Count} total)");
        
        // Send client their ID
        var idMsg = new NetworkMessage
        {
            Type = "client_id",
            OwnerId = clientId
        };
        var idJson = JsonSerializer.Serialize(idMsg);
        var idBytes = Encoding.UTF8.GetBytes(idJson);
        await socket.SendAsync(
            new ArraySegment<byte>(idBytes),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );
        
        // Send all existing entities to new client
        lock (_entityLock)
        {
            foreach (var entity in _entities.Values)
            {
                var msg = new NetworkMessage
                {
                    Type = "spawn",
                    EntityId = entity.Id,
                    EntityType = entity.Type,
                    Position = entity.Position,
                    Rotation = entity.Rotation
                };
                
                var json = JsonSerializer.Serialize(msg);
                var bytes = Encoding.UTF8.GetBytes(json);
                
                _ = socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
            }
        }
        
        // Keep connection alive and receive messages
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
                    await HandleClientMessageAsync(json, clientId);
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
                Console.WriteLine($"[Server] Client error: {ex.Message}");
            }
        }
        
        // Clean up on disconnect
        lock (_entityLock)
        {
            _clientSockets.Remove(clientId);
        }
        
        Console.WriteLine($"[Server] Client disconnected: {clientId} ({_clients.Count - 1} remaining)");
    }
    
    private Task HandleClientMessageAsync(string json, string clientId)
    {
        try
        {
            var message = JsonSerializer.Deserialize<NetworkMessage>(json);
            
            if (message == null)
                return Task.CompletedTask;
            
            switch (message.Type)
            {
                case "client_spawn":
                    if (!string.IsNullOrEmpty(message.EntityId) && message.Position != null)
                    {
                        Console.WriteLine($"[Server] Client {clientId} spawned entity: {message.EntityId}");
                        SpawnEntity(
                            message.EntityId,
                            message.EntityType ?? "entity",
                            message.Position.X,
                            message.Position.Y,
                            message.Position.Z,
                            clientId // Assign ownership to spawning client
                        );
                    }
                    break;
                
                case "client_despawn":
                    if (!string.IsNullOrEmpty(message.EntityId))
                    {
                        // Validate ownership
                        lock (_entityLock)
                        {
                            if (_entityOwners.TryGetValue(message.EntityId, out var ownerId) && ownerId == clientId)
                            {
                                Console.WriteLine($"[Server] Client {clientId} despawned entity: {message.EntityId}");
                                DespawnEntity(message.EntityId);
                            }
                            else
                            {
                                Console.WriteLine($"[Server] Client {clientId} tried to despawn entity {message.EntityId} but doesn't own it");
                            }
                        }
                    }
                    break;
                
                case "client_update":
                    // Handle physics state updates from owning client
                    if (!string.IsNullOrEmpty(message.EntityId) && message.Position != null)
                    {
                        lock (_entityLock)
                        {
                            // Validate ownership
                            if (_entityOwners.TryGetValue(message.EntityId, out var ownerId) && ownerId == clientId)
                            {
                                // Update entity state
                                if (_entities.TryGetValue(message.EntityId, out var entity))
                                {
                                    entity.Position = message.Position;
                                    if (message.Rotation != null)
                                    {
                                        entity.Rotation = message.Rotation;
                                    }
                                    entity.MarkAsDirty(); // Force send on next update
                                }
                            }
                        }
                    }
                    break;
                
                default:
                    Console.WriteLine($"[Server] Unknown message type: {message.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] Error handling client message: {ex.Message}");
        }
        
        return Task.CompletedTask;
    }
}
