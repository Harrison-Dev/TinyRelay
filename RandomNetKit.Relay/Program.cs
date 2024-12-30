using System;
using System.Threading;

namespace RandomNetKit.Relay;

public class Program
{
    static void Main(string[] args)
    {
        int port = 9050;
        Console.WriteLine("Starting RandomNetKit Relay Server on port " + port);
        var server = new RelayServer();

        if (!server.Start(port))
        {
            Console.WriteLine("Failed to start the relay server.");
            return;
        }

        Console.WriteLine("RelayServer started on port " + port + ".");
        Console.WriteLine("Press Ctrl+C to stop...");

        var stopEvent = new ManualResetEventSlim();
        Console.CancelKeyPress += (sender, e) =>
        {
            Console.WriteLine("Stopping server...");
            e.Cancel = true;
            stopEvent.Set();
        };

        while (!stopEvent.IsSet)
        {
            server.PollEvents();
            Thread.Sleep(15); // ~60fps update rate
        }

        server.Stop();
        Console.WriteLine("Server stopped.");
    }
}
