using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SomniumNetwork.Server;

public class GameServer
{
    private readonly Dictionary<string, Entity> _entities = new();
    private readonly ConcurrentBag<WebSocket> _clients = new();
    private readonly object _entityLock = new();
    private float _time = 0f;
    
    public void SpawnEntity(string id, string type, float x, float y, float z)
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
            
            // Send spawn message to all clients
            var msg = new NetworkMessage
            {
                Type = "spawn",
                EntityId = id,
                EntityType = type,
                Position = entity.Position,
                Rotation = entity.Rotation
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
    
    public async Task RunGameLoopAsync()
    {
        var tickRate = 20; // 20 Hz
        var deltaTime = 1f / tickRate;
        
        while (true)
        {
            var startTime = DateTime.UtcNow;
            
            // Update entities
            lock (_entityLock)
            {
                _time += deltaTime;
                
                foreach (var entity in _entities.Values)
                {
                    // Move entities in circles (test movement)
                    float angle = _time * 0.5f;
                    float radius = 2f;
                    float centerX = entity.Position.X;
                    float centerZ = entity.Position.Z;
                    
                    entity.Position.X = centerX + (float)Math.Sin(angle) * radius;
                    entity.Position.Z = centerZ + (float)Math.Cos(angle) * radius;
                    entity.Rotation.Y = angle * 57.3f; // radians to degrees
                }
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
            foreach (var entity in _entities.Values)
            {
                updates.Add(new EntityUpdate
                {
                    Id = entity.Id,
                    Position = entity.Position,
                    Rotation = entity.Rotation
                });
            }
        }
        
        var msg = new NetworkMessage
        {
            Type = "update",
            Updates = updates
        };
        
        await BroadcastMessageAsync(msg);
    }
    
    private async Task BroadcastMessageAsync(NetworkMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        var tasks = new List<Task>();
        
        foreach (var client in _clients)
        {
            if (client.State == WebSocketState.Open)
            {
                tasks.Add(client.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                ));
            }
        }
        
        await Task.WhenAll(tasks);
    }
    
    public async Task HandleClientAsync(WebSocket socket)
    {
        _clients.Add(socket);
        Console.WriteLine($"[Server] Client connected ({_clients.Count} total)");
        
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
        
        // Keep connection alive
        var buffer = new byte[1024];
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
                
                // TODO: Handle client messages (player actions, etc.)
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] Client error: {ex.Message}");
        }
        
        Console.WriteLine($"[Server] Client disconnected");
    }
}
