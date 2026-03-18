using System.Collections.Concurrent;

namespace SomniumNetwork.Server;

/// <summary>
/// Manages entity lifecycle, ownership, and state tracking.
/// </summary>
public class EntityManager
{
    private readonly Dictionary<string, Entity> _entities = new();
    private readonly Dictionary<string, string> _entityOwners = new(); // EntityId -> ClientId
    private readonly object _entityLock = new();
    
    public int EntityCount
    {
        get
        {
            lock (_entityLock)
            {
                return _entities.Count;
            }
        }
    }
    
    /// <summary>
    /// Spawn a new entity with the specified owner.
    /// </summary>
    public Entity SpawnEntity(string id, string type, Vector3 position, Vector3 rotation, string ownerId)
    {
        lock (_entityLock)
        {
            var entity = new Entity
            {
                Id = id,
                Type = type,
                Position = position,
                Rotation = rotation
            };
            
            _entities[id] = entity;
            _entityOwners[id] = ownerId;
            
            if (_entities.Count % 100 == 0)
            {
                Console.WriteLine($"[EntityManager] Total entities: {_entities.Count}");
            }
            
            return entity;
        }
    }
    
    /// <summary>
    /// Remove an entity by ID.
    /// </summary>
    public bool DespawnEntity(string id)
    {
        lock (_entityLock)
        {
            _entityOwners.Remove(id);
            return _entities.Remove(id);
        }
    }
    
    /// <summary>
    /// Update an entity's position and rotation (validates ownership).
    /// </summary>
    public bool UpdateEntity(string entityId, string clientId, Vector3 position, Vector3? rotation)
    {
        lock (_entityLock)
        {
            // Validate ownership
            if (!_entityOwners.TryGetValue(entityId, out var ownerId) || ownerId != clientId)
            {
                return false; // Not the owner
            }
            
            if (_entities.TryGetValue(entityId, out var entity))
            {
                entity.Position = position;
                if (rotation != null)
                {
                    entity.Rotation = rotation;
                }
                entity.MarkAsDirty(); // Force update on next broadcast
                return true;
            }
            
            return false;
        }
    }
    
    /// <summary>
    /// Get all entities (for initial sync to new clients).
    /// </summary>
    public List<Entity> GetAllEntities()
    {
        lock (_entityLock)
        {
            return new List<Entity>(_entities.Values);
        }
    }
    
    /// <summary>
    /// Get entities that have moved since last update (delta compression).
    /// </summary>
    public List<Entity> GetMovedEntities()
    {
        lock (_entityLock)
        {
            var moved = new List<Entity>();
            
            foreach (var entity in _entities.Values)
            {
                if (entity.HasMoved())
                {
                    moved.Add(entity);
                    entity.MarkAsSent();
                }
            }
            
            return moved;
        }
    }
    
    /// <summary>
    /// Check if a client owns an entity.
    /// </summary>
    public bool IsOwner(string entityId, string clientId)
    {
        lock (_entityLock)
        {
            return _entityOwners.TryGetValue(entityId, out var ownerId) && ownerId == clientId;
        }
    }
    
    /// <summary>
    /// Get the owner of an entity.
    /// </summary>
    public string? GetOwner(string entityId)
    {
        lock (_entityLock)
        {
            return _entityOwners.TryGetValue(entityId, out var ownerId) ? ownerId : null;
        }
    }
}
