namespace RandomNetKit.PunchNAT;

public class Program
{
    static void Main(string[] args)
    {
        int port = 9051;
        Console.WriteLine("Starting RandomNetKit NAT Punch Server on port " + port);
        var server = new PunchServer();

        if (!server.Start(port))
        {
            Console.WriteLine("Failed to start the NAT punch server.");
            return;
        }

        Console.WriteLine("NAT Punch Server started on port " + port + ".");
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