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
        Console.WriteLine("[Server] Ready for connections...");
        
        // TODO: Load entity spawns from config file
        // For now, server just tracks entities that clients create
        
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
