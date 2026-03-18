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
        var listener = new HttpListener();
        
        // Handle Ctrl+C gracefully
        var cts = new CancellationTokenSource();
        var shutdownRequested = false;
        Console.CancelKeyPress += (sender, e) =>
        {
            if (shutdownRequested)
                return; // Ignore repeated Ctrl+C
            
            shutdownRequested = true;
            Console.WriteLine("\n[Server] Shutting down...");
            cts.Cancel();
            listener.Stop(); // Stop listener to unblock GetContextAsync
            e.Cancel = true; // Prevent immediate termination, allow cleanup
        };
        
        // Start game loop in background
        _ = Task.Run(async () => await server.RunGameLoopAsync(cts.Token), cts.Token);
        
        // Start WebSocket server
        listener.Prefixes.Add("http://*:9090/");
        listener.Start();
        
        Console.WriteLine("[Server] Started on ws://0.0.0.0:9090/");
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
        catch (HttpListenerException)
        {
            // Expected when listener is stopped during GetContextAsync
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
            Console.WriteLine("[Server] Stopped");
        }
        
        // Give tasks a moment to finish cleanup
        await Task.Delay(100);
    }
}
