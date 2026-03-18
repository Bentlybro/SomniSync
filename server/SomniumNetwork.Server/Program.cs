using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SomniumNetwork.Server;

class Program
{
    static async Task Main(string[] args)
    {
        // Handle Ctrl+C gracefully
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            Console.WriteLine("\n[Server] Shutting down...");
            cts.Cancel();
            e.Cancel = true;
        };
        
        var server = new GameServer();
        
        // Start game loop in background
        _ = Task.Run(async () => await server.RunGameLoopAsync(cts.Token), cts.Token);
        
        // Start WebSocket server
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:9090/");
        listener.Start();
        
        Console.WriteLine("[Server] Started on ws://localhost:9090/");
        Console.WriteLine("[Server] Ready for connections...");
        Console.WriteLine("[Server] Press Ctrl+C to stop");
        
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();
                
                if (context.Request.IsWebSocketRequest)
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    _ = Task.Run(async () => await server.HandleClientAsync(wsContext.WebSocket), cts.Token);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        catch (TaskCanceledException)
        {
            // Expected when cancelled
        }
        finally
        {
            listener.Stop();
            Console.WriteLine("[Server] Stopped");
        }
    }
}
