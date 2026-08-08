using System.Net;
using System.Net.Sockets;

namespace CaYaTunnel.Tests;

/// <summary>
/// A pair of connected loopback sockets. Real sockets rather than an in-memory fake, so the
/// tests exercise the same partial-read and shutdown behaviour production hits.
/// </summary>
internal sealed class LoopbackChannel : IAsyncDisposable
{
    private readonly TcpClient _left;
    private readonly TcpClient _right;

    private LoopbackChannel(TcpClient left, TcpClient right)
    {
        _left = left;
        _right = right;
        Left = left.GetStream();
        Right = right.GetStream();
    }

    public Stream Left { get; }

    public Stream Right { get; }

    public static async Task<LoopbackChannel> CreateAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var connecting = new TcpClient();
            var connectTask = connecting.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            await connectTask;
            return new LoopbackChannel(connecting, accepted);
        }
        finally
        {
            listener.Stop();
        }
    }

    public ValueTask DisposeAsync()
    {
        _left.Dispose();
        _right.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Hands out ports for a test gateway. The tunnel range and the fixed ports have to be disjoint:
/// the server refuses to start if its control port sits inside the range it would allocate
/// tunnels from, and ephemeral ports are handed out in no particular order.
/// </summary>
internal static class TestPorts
{
    public const int RangeSize = 50;

    /// <summary>
    /// A free port from the operating system. On Windows these come from the dynamic range
    /// (49152+) and are handed out consecutively, which is why the tunnel range below is picked
    /// from a different band entirely rather than by retrying until the numbers happen to miss.
    /// </summary>
    public static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Start of a tunnel port range, taken from well below the dynamic range so it can never
    /// collide with the ephemeral ports the fixed listeners get.
    /// </summary>
    public static int RangeStart()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000, 29000);

    /// <summary>
    /// A port free for TCP <em>and</em> UDP. Needed wherever one number serves both, because
    /// Windows reserves whole UDP ranges for Hyper-V and WinNAT: a port TCP happily accepted can
    /// still refuse a UDP bind with access-denied.
    /// </summary>
    public static int FreeForBothProtocols()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var port = Free();

            try
            {
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException)
            {
                // Reserved for UDP, or taken between the two binds. Try another.
            }
        }

        throw new InvalidOperationException("Could not find a port free for both TCP and UDP.");
    }
}

/// <summary>A loopback TCP server that echoes everything back — stands in for a local service.</summary>
internal sealed class EchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private EchoServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _loop = AcceptLoopAsync(_cts.Token);
    }

    public int Port { get; }

    public static EchoServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new EchoServer(listener);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        try
                        {
                            await stream.CopyToAsync(stream, cancellationToken);
                            client.Client.Shutdown(SocketShutdown.Send);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                        {
                            // Client went away.
                        }
                    }
                }, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cts.Dispose();
    }
}
