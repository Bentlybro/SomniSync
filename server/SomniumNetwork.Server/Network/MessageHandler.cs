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
                
                case "client_batch_update":
                    HandleBatchUpdate(message, clientId);
                    break;
                
                case "request_sync":
                    await HandleRequestSyncAsync(clientId);
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
        
        // Only log every 50th spawn to avoid spam during bulk registration
        var count = _entityManager.EntityCount;
        if (count <= 5 || count % 50 == 0)
            Console.WriteLine($"[MessageHandler] Client {clientId} spawned entity: {message.EntityId} ({count + 1} total)");
        
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
    
    private void HandleBatchUpdate(NetworkMessage message, string clientId)
    {
        if (message.Updates == null || message.Updates.Count == 0)
            return;
        
        foreach (var update in message.Updates)
        {
            if (string.IsNullOrEmpty(update.Id) || update.Position == null)
                continue;
            
            _entityManager.UpdateEntity(update.Id, clientId, update.Position, update.Rotation);
        }
    }
    
    private void HandleUpdate(NetworkMessage message, string clientId)
    {
        if (string.IsNullOrEmpty(message.EntityId) || message.Position == null)
            return;
        
        // Update entity (validates ownership internally)
        bool updated = _entityManager.UpdateEntity(message.EntityId, clientId, message.Position, message.Rotation);
        
        if (!updated)
        {
            // Silently ignore — common during reconnects when client has stale entity IDs
        }
    }
    
    private async Task HandleRequestSyncAsync(string clientId)
    {
        var entities = _entityManager.GetAllEntities();
        
        if (entities.Count == 0)
            return;
        
        // Build type table (deduplicate entity type strings)
        var typeToIndex = new Dictionary<string, byte>();
        var typeNames = new List<string>();
        foreach (var entity in entities)
        {
            if (!typeToIndex.ContainsKey(entity.Type))
            {
                typeToIndex[entity.Type] = (byte)typeNames.Count;
                typeNames.Add(entity.Type);
            }
        }
        
        // Binary format:
        // [0x02][ushort count][byte type_count]
        // [type table: byte name_len + N bytes name × type_count]
        // [per entity: 16 bytes GUID + byte type_index + 12 bytes pos + 12 bytes rot = 41 bytes]
        int typeTableSize = 0;
        foreach (var name in typeNames)
            typeTableSize += 1 + System.Text.Encoding.UTF8.GetByteCount(name);
        
        int headerSize = 1 + 2 + 1; // type byte + count + type_count
        int perEntity = 16 + 1 + 12 + 12; // GUID + type_index + pos + rot = 41
        int totalSize = headerSize + typeTableSize + (entities.Count * perEntity);
        
        var buffer = new byte[totalSize];
        int offset = 0;
        
        // Header
        buffer[offset++] = 0x02; // Binary batch sync
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), (ushort)entities.Count);
        offset += 2;
        buffer[offset++] = (byte)typeNames.Count;
        
        // Type table
        foreach (var name in typeNames)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            buffer[offset++] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, buffer, offset, nameBytes.Length);
            offset += nameBytes.Length;
        }
        
        // Entities
        foreach (var entity in entities)
        {
            entity.BinaryId.TryWriteBytes(buffer.AsSpan(offset, 16));
            offset += 16;
            
            buffer[offset++] = typeToIndex[entity.Type];
            
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Position.X); offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Position.Y); offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Position.Z); offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Rotation.X); offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Rotation.Y); offset += 4;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), entity.Rotation.Z); offset += 4;
        }
        
        await _broadcaster.SendBinaryToClientAsync(clientId, buffer, offset);
        _broadcaster.SetClientReady(clientId);
        
        Console.WriteLine($"[MessageHandler] Sent binary batch sync: {entities.Count} entities, {offset} bytes, {typeNames.Count} types to {clientId}");
    }
}
