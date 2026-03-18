using System.Net.WebSockets;

namespace SomniumNetwork.Server;

/// <summary>
/// Main game server coordinator. Runs the game loop and delegates to specialized components.
/// </summary>
public class GameServer
{
    private readonly EntityManager _entityManager;
    private readonly NetworkBroadcaster _broadcaster;
    private readonly MessageHandler _messageHandler;
    private readonly ClientConnection _clientConnection;
    
    private float _time = 0f;
    
    public GameServer()
    {
        _entityManager = new EntityManager();
        _broadcaster = new NetworkBroadcaster();
        _messageHandler = new MessageHandler(_entityManager, _broadcaster);
        _clientConnection = new ClientConnection(_entityManager, _broadcaster, _messageHandler);
    }
    
    /// <summary>
    /// Main game loop - runs at 60Hz, broadcasts entity updates.
    /// </summary>
    public async Task RunGameLoopAsync(CancellationToken cancellationToken = default)
    {
        var tickRate = 60; // 60 Hz for smooth updates
        var deltaTime = 1f / tickRate;
        
        Console.WriteLine("[GameServer] Game loop started (60 Hz)");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var startTime = DateTime.UtcNow;
            
            // Update game state
            _time += deltaTime;
            
            // Server doesn't move entities by default
            // Client-authoritative: entities with client authority send their own updates
            // Server-authoritative (NPCs, etc.): AI logic would go here
            
            // Broadcast entity updates to all clients (delta compression)
            await BroadcastUpdatesAsync();
            
            // Sleep to maintain tick rate
            var elapsed = DateTime.UtcNow - startTime;
            var sleepTime = TimeSpan.FromSeconds(deltaTime) - elapsed;
            if (sleepTime > TimeSpan.Zero)
            {
                await Task.Delay(sleepTime);
            }
        }
        
        Console.WriteLine("[GameServer] Game loop stopped");
    }
    
    /// <summary>
    /// Handle a new client WebSocket connection.
    /// </summary>
    public async Task HandleClientAsync(WebSocket socket)
    {
        await _clientConnection.HandleAsync(socket);
    }
    
    private async Task BroadcastUpdatesAsync()
    {
        var movedEntities = _entityManager.GetMovedEntities();
        
        if (movedEntities.Count > 0)
        {
            await _broadcaster.BroadcastUpdatesAsync(movedEntities);
        }
    }
}
