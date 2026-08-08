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
