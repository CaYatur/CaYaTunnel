using System.Net;
using System.Net.Sockets;
using CaYaTunnel.Core.Protocol;

namespace CaYaTunnel.Client;

/// <summary>
/// The agent's side of a UDP flow: a socket connected to the local or LAN target, exchanging
/// datagrams with one visitor.
/// <para>
/// One socket per flow rather than one shared socket, so replies from the target come back
/// already associated with the visitor that caused them. A single shared socket would need the
/// agent to demultiplex responses itself, which UDP gives it no reliable way to do.
/// </para>
/// </summary>
public sealed class UdpTargetChannel : IDatagramChannel
{
    private readonly UdpClient _socket;
    private int _disposed;

    private UdpTargetChannel(UdpClient socket) => _socket = socket;

    public static async Task<IDatagramChannel> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var parsed)
            ? [parsed]
            : await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            throw new IOException($"Could not resolve '{host}'.");
        }

        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var socket = new UdpClient(address.AddressFamily);

        try
        {
            // Connecting a UDP socket filters inbound datagrams to this peer and lets Send be
            // used without repeating the endpoint every time.
            socket.Connect(new IPEndPoint(address, port));
            return new UdpTargetChannel(socket);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new IOException($"Could not open a UDP socket to {host}:{port} — {ex.SocketErrorCode}.", ex);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Windows reports ICMP port-unreachable as a receive error on a connected UDP socket.
            // The target is not listening; ending the flow is the honest response.
            return null;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        try
        {
            await _socket.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Same as above: drop the datagram rather than tearing everything down, since UDP
            // senders expect loss anyway.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _socket.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
