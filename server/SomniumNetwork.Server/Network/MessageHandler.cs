using System.Text.Json;

namespace SomniumNetwork.Server;

/// <summary>
/// Processes incoming network messages from clients.
/// </summary>
public class MessageHandler
{
    private readonly EntityManager _entityManager;
    private readonly NetworkBroadcaster _broadcaster;
    
    public MessageHandler(EntityManager entityManager, NetworkBroadcaster broadcaster)
    {
        _entityManager = entityManager;
        _broadcaster = broadcaster;
    }
    
    /// <summary>
    /// Handle a message from a client.
    /// </summary>
    public async Task HandleMessageAsync(string json, string clientId)
    {
        try
        {
            var message = JsonSerializer.Deserialize<NetworkMessage>(json);
            
            if (message == null)
                return;
            
            switch (message.Type)
            {
                case "client_spawn":
                    await HandleSpawnAsync(message, clientId);
                    break;
                
                case "client_despawn":
                    await HandleDespawnAsync(message, clientId);
                    break;
                
                case "client_update":
                    HandleUpdate(message, clientId);
                    break;
                
                default:
                    Console.WriteLine($"[MessageHandler] Unknown message type: {message.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MessageHandler] Error handling message: {ex.Message}");
        }
    }
    
    private async Task HandleSpawnAsync(NetworkMessage message, string clientId)
    {
        if (string.IsNullOrEmpty(message.EntityId) || message.Position == null)
            return;
        
        Console.WriteLine($"[MessageHandler] Client {clientId} spawned entity: {message.EntityId}");
        
        var entity = _entityManager.SpawnEntity(
            message.EntityId,
            message.EntityType ?? "entity",
            message.Position,
            message.Rotation ?? new Vector3(),
            clientId
        );
        
        // Broadcast spawn to all clients with ownership info
        var spawnMsg = new NetworkMessage
        {
            Type = "spawn",
            EntityId = entity.Id,
            EntityType = entity.Type,
            Position = entity.Position,
            Rotation = entity.Rotation,
            OwnerId = clientId
        };
        
        await _broadcaster.BroadcastMessageAsync(spawnMsg);
    }
    
    private async Task HandleDespawnAsync(NetworkMessage message, string clientId)
    {
        if (string.IsNullOrEmpty(message.EntityId))
            return;
        
        // Validate ownership
        if (!_entityManager.IsOwner(message.EntityId, clientId))
        {
            Console.WriteLine($"[MessageHandler] Client {clientId} tried to despawn entity {message.EntityId} but doesn't own it");
            return;
        }
        
        Console.WriteLine($"[MessageHandler] Client {clientId} despawned entity: {message.EntityId}");
        
        if (_entityManager.DespawnEntity(message.EntityId))
        {
            var despawnMsg = new NetworkMessage
            {
                Type = "despawn",
                EntityId = message.EntityId
            };
            
            await _broadcaster.BroadcastMessageAsync(despawnMsg);
        }
    }
    
    private void HandleUpdate(NetworkMessage message, string clientId)
    {
        if (string.IsNullOrEmpty(message.EntityId) || message.Position == null)
            return;
        
        Console.WriteLine($"[MessageHandler] client_update from {clientId}: entity={message.EntityId}, pos=({message.Position.X:F2}, {message.Position.Y:F2}, {message.Position.Z:F2})");
        
        // Update entity (validates ownership internally)
        bool updated = _entityManager.UpdateEntity(message.EntityId, clientId, message.Position, message.Rotation);
        
        if (!updated)
        {
            Console.WriteLine($"[MessageHandler] Failed to update entity {message.EntityId} - ownership check failed");
        }
    }
}
