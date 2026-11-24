using System.Net;
using System.Net.Sockets;

namespace RustText.LockFixture;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--ports"])
        {
            using var tcp4 = Listen(IPAddress.Loopback);
            using var tcp6 = Listen(IPAddress.IPv6Loopback);
            using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            Console.WriteLine($"{Port(tcp4.LocalEndpoint)} {Port(tcp6.LocalEndpoint)} " +
                $"{Port(udp4.Client.LocalEndPoint!)} {Port(udp6.Client.LocalEndPoint!)}");
            if (Console.ReadLine() is not null)
            {
                tcp4.Stop();
                Console.WriteLine("released");
                Console.ReadLine();
            }
            return 0;
        }

        if (args is not [var path, var ready, var stop, var shareDelete] || !bool.TryParse(shareDelete, out var permitDeletion))
            return 2;
        var share = permitDeletion ? FileShare.ReadWrite | FileShare.Delete : FileShare.ReadWrite;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, share);
        await File.WriteAllTextAsync(ready, "ready");
        for (var i = 0; i < 2400 && !File.Exists(stop); i++) await Task.Delay(50);
        return 0;
    }

    private static TcpListener Listen(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        return listener;
    }

    private static int Port(EndPoint endpoint) => ((IPEndPoint)endpoint).Port;
}
