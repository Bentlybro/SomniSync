namespace SomniumNetwork.Server;

/// <summary>
/// Network protocol message sent between client and server.
/// </summary>
public class NetworkMessage
{
    public string Type { get; set; } = ""; // "spawn", "despawn", "update", "variable", "client_id"
    public string? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string? OwnerId { get; set; } // Which client owns this entity
    public bool? IsMasterClient { get; set; } // Is this client the master (first to connect)?
    public Vector3? Position { get; set; }
    public Vector3? Rotation { get; set; }
    public List<EntityUpdate>? Updates { get; set; }
    public List<EntitySpawnData>? Spawns { get; set; } // For batch_sync
    public Dictionary<string, object>? Variables { get; set; }
}

public class EntityUpdate
{
    public string Id { get; set; } = "";
    public Vector3? Position { get; set; }
    public Vector3? Rotation { get; set; }
}

public class EntitySpawnData
{
    public string EntityId { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? OwnerId { get; set; }
    public Vector3? Position { get; set; }
    public Vector3? Rotation { get; set; }
}
