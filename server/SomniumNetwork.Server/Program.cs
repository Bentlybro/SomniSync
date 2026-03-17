using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SomniumNetwork.Server;

class Program
{
    static async Task Main(string[] args)
    {
        var server = new GameServer();
        
        // Start game loop in background
        _ = Task.Run(async () => await server.RunGameLoopAsync());
        
        // Start WebSocket server
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:9090/");
        listener.Start();
        
        Console.WriteLine("[Server] Started on ws://localhost:9090/");
        Console.WriteLine("[Server] Spawning 100 test entities...");
        
        // Spawn 100 test entities in a grid
        for (int i = 0; i < 100; i++)
        {
            float x = (i % 10) * 5f;
            float z = (i / 10) * 5f;
            server.SpawnEntity($"entity_{i}", "cube", x, 0, z);
        }
        
        Console.WriteLine("[Server] Entities spawned. Waiting for clients...");
        
        while (true)
        {
            var context = await listener.GetContextAsync();
            
            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                _ = Task.Run(async () => await server.HandleClientAsync(wsContext.WebSocket));
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }
}
