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
                
                case "client_batch_spawn":
                    await HandleBatchSpawnAsync(message, clientId);
                    break;
                
                case "request_sync":
                    Console.WriteLine($"[MessageHandler] Client {clientId} requesting sync");
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
    
    private async Task HandleBatchSpawnAsync(NetworkMessage message, string clientId)
    {
        if (message.Spawns == null || message.Spawns.Count == 0)
            return;
        
        Console.WriteLine($"[MessageHandler] Client {clientId} batch spawning {message.Spawns.Count} entities");
        
        // Spawn all entities on server first (no broadcasts yet)
        foreach (var spawn in message.Spawns)
        {
            if (string.IsNullOrEmpty(spawn.EntityId) || spawn.Position == null)
                continue;
            
            _entityManager.SpawnEntity(
                spawn.EntityId,
                spawn.EntityType ?? "entity",
                spawn.Position,
                spawn.Rotation ?? new Vector3(),
                clientId
            );
        }
        
        Console.WriteLine($"[MessageHandler] Batch spawn complete: {_entityManager.EntityCount} total entities");
        
        // Auto-sync any connected spectators that aren't ready yet
        // This handles the race condition where spectator connected before master registered entities
        await SyncWaitingClientsAsync(clientId);
    }
    
    /// <summary>
    /// Send binary batch_sync to all non-master clients after master spawns entities.
    /// Re-sends full state every time — handles split batch spawns gracefully.
    /// Client skips already-linked entities, so duplicates are harmless.
    /// </summary>
    private async Task SyncWaitingClientsAsync(string masterClientId)
    {
        var spectators = _broadcaster.GetAllClientsExcept(masterClientId);
        
        if (spectators.Count == 0)
            return;
        
        Console.WriteLine($"[MessageHandler] Syncing {spectators.Count} spectator(s) with {_entityManager.EntityCount} entities");
        
        foreach (var clientId in spectators)
        {
            await HandleRequestSyncAsync(clientId);
        }
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
    
    /// <summary>
    /// Handle binary position updates from master client.
    /// Same format as broadcast: [0x01][ushort count][per entity: 16B GUID + 12B pos + 12B rot]
    /// Zero JSON parsing, zero string allocation — pure speed.
    /// </summary>
    public void HandleBinaryMessage(byte[] data, int length, string clientId)
    {
        if (length < 3) return;
        
        byte msgType = data[0];
        
        if (msgType == 0x03) // Binary batch spawn
        {
            HandleBinaryBatchSpawn(data, length, clientId);
        }
        else if (msgType == 0x01) // Binary position update
        {
            ushort count = BitConverter.ToUInt16(data, 1);
            int offset = 3;
            
            for (int i = 0; i < count; i++)
            {
                if (offset + 40 > length) break;
                
                // Read GUID (16 bytes)
                var guid = new Guid(new ReadOnlySpan<byte>(data, offset, 16));
                string entityId = guid.ToString();
                offset += 16;
                
                // Read position (12 bytes)
                float px = BitConverter.ToSingle(data, offset); offset += 4;
                float py = BitConverter.ToSingle(data, offset); offset += 4;
                float pz = BitConverter.ToSingle(data, offset); offset += 4;
                
                // Read rotation (12 bytes)
                float rx = BitConverter.ToSingle(data, offset); offset += 4;
                float ry = BitConverter.ToSingle(data, offset); offset += 4;
                float rz = BitConverter.ToSingle(data, offset); offset += 4;
                
                var pos = new Vector3 { X = px, Y = py, Z = pz };
                var rot = new Vector3 { X = rx, Y = ry, Z = rz };
                
                _entityManager.UpdateEntity(entityId, clientId, pos, rot);
            }
        }
    }
    
    private async void HandleBinaryBatchSpawn(byte[] data, int length, string clientId)
    {
        if (length < 4) return;
        
        ushort count = BitConverter.ToUInt16(data, 1);
        byte typeCount = data[3];
        int offset = 4;
        
        // Read type table
        string[] typeNames = new string[typeCount];
        for (int t = 0; t < typeCount; t++)
        {
            if (offset >= length) return;
            byte nameLen = data[offset++];
            typeNames[t] = System.Text.Encoding.UTF8.GetString(data, offset, nameLen);
            offset += nameLen;
        }
        
        Console.WriteLine($"[MessageHandler] Client {clientId} binary batch spawning {count} entities ({typeCount} types)");
        
        for (int i = 0; i < count; i++)
        {
            if (offset + 41 > length) break;
            
            // GUID (16 bytes)
            var guid = new Guid(new ReadOnlySpan<byte>(data, offset, 16));
            string entityId = guid.ToString();
            offset += 16;
            
            // Type index (1 byte)
            byte typeIndex = data[offset++];
            string entityType = typeIndex < typeNames.Length ? typeNames[typeIndex] : "entity";
            
            // Position (12 bytes)
            float px = BitConverter.ToSingle(data, offset); offset += 4;
            float py = BitConverter.ToSingle(data, offset); offset += 4;
            float pz = BitConverter.ToSingle(data, offset); offset += 4;
            
            // Rotation (12 bytes)
            float rx = BitConverter.ToSingle(data, offset); offset += 4;
            float ry = BitConverter.ToSingle(data, offset); offset += 4;
            float rz = BitConverter.ToSingle(data, offset); offset += 4;
            
            _entityManager.SpawnEntity(
                entityId,
                entityType,
                new Vector3 { X = px, Y = py, Z = pz },
                new Vector3 { X = rx, Y = ry, Z = rz },
                clientId
            );
        }
        
        Console.WriteLine($"[MessageHandler] Binary batch spawn complete: {_entityManager.EntityCount} total entities");
        
        // Auto-sync spectators
        await SyncWaitingClientsAsync(clientId);
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
