using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;

namespace CaYaTunnel.Server.Gateway;

/// <summary>
/// Serves one public UDP port.
/// <para>
/// UDP has no connections, so "who is this traffic from" has to be reconstructed: datagrams are
/// grouped into flows by sender address, each flow gets its own multiplexed stream to the device,
/// and a flow ends when it goes quiet. That idle timeout is the only thing that ever closes a
/// flow — nothing in the protocol says goodbye.
/// </para>
/// </summary>
internal sealed class UdpPortRouter : IAsyncDisposable
{
    /// <summary>How long a flow may sit silent before it is dropped.</summary>
    private static readonly TimeSpan FlowIdleTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Datagrams buffered per flow while the device is slow. Small on purpose: a UDP sender
    /// already tolerates loss, and a deep queue would only add latency to a live game.
    /// </summary>
    private const int FlowQueueCapacity = 256;

    private readonly ConcurrentDictionary<IPEndPoint, UdpFlow> _flows = new();
    private readonly int _port;
    private readonly TunnelServer _server;
    private readonly GatewayLog _log;
    private readonly CancellationTokenSource _shutdown = new();

    private UdpClient? _socket;

    public UdpPortRouter(int port, TunnelServer server, GatewayLog log)
    {
        _port = port;
        _server = server;
        _log = log;
    }

    public Task? Task { get; private set; }

    public void Start(CancellationToken cancellationToken)
    {
        Task = RunAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);

        try
        {
            _socket = new UdpClient(_port);

            // Without this, one ICMP port-unreachable from a previous datagram makes the next
            // ReceiveAsync throw on Windows and would kill the whole listener.
            DisableConnectionReset(_socket);
        }
        catch (SocketException ex)
        {
            _log.Error("udp", $"Could not listen on UDP {_port} — {ex.Message}");
            return;
        }

        _log.Info("gateway", $"Listening on {_port} (udp).");

        var sweeper = SweepIdleFlowsAsync(linked.Token);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _socket.ReceiveAsync(linked.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // stale ICMP for a flow that has gone; keep serving everyone else
                }

                await DispatchAsync(received, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _log.Debug("udp", $"Listener on {_port} stopped: {ex.Message}");
        }
        finally
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);

            try
            {
                await sweeper.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await CloseAllFlowsAsync().ConfigureAwait(false);
            _socket?.Dispose();
        }
    }

    private async Task DispatchAsync(UdpReceiveResult received, CancellationToken cancellationToken)
    {
        if (_flows.TryGetValue(received.RemoteEndPoint, out var existing))
        {
            existing.Offer(received.Buffer);
            return;
        }

        var tunnel = _server.Registry.FindByPublicPort(_port);
        if (tunnel is null || !tunnel.ServesUdp)
        {
            return; // nothing registered for UDP here; silently drop, as a closed port would
        }

        var session = _server.FindSession(tunnel.DeviceId);
        if (session is null)
        {
            _log.Debug("udp", $"'{tunnel.Name}' received a datagram but its device is offline.");
            return;
        }

        var flow = new UdpFlow(_socket!, received.RemoteEndPoint, FlowQueueCapacity);
        if (!_flows.TryAdd(received.RemoteEndPoint, flow))
        {
            // Another datagram from the same sender raced us; hand it to the winner.
            _flows[received.RemoteEndPoint].Offer(received.Buffer);
            await flow.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // Queue the datagram that started the flow before the pump begins, so the first packet —
        // usually the one that matters most, a handshake — is never the one that gets lost.
        flow.Offer(received.Buffer);

        _ = Task.Run(() => ServeFlowAsync(tunnel, session, received.RemoteEndPoint, flow, cancellationToken), CancellationToken.None);
    }

    private async Task ServeFlowAsync(
        TunnelDefinition tunnel,
        DeviceSession session,
        IPEndPoint remote,
        UdpFlow flow,
        CancellationToken cancellationToken)
    {
        MuxStream? mux = null;

        try
        {
            mux = await session.OpenStreamAsync(tunnel, remote.ToString(), cancellationToken, StreamTransports.Udp)
                .ConfigureAwait(false);

            _server.OnTunnelConnectionOpened(tunnel.Id);

            var (toTarget, fromTarget) = await DatagramPump.RunAsync(mux, flow, cancellationToken).ConfigureAwait(false);

            _server.OnTunnelConnectionClosed(tunnel.Id, bytesIn: fromTarget, bytesOut: toTarget);
            _log.Debug("udp", $"{remote} -> '{tunnel.Name}' flow ended ({fromTarget} in, {toTarget} out).");
        }
        catch (TargetUnreachableException ex)
        {
            _log.Warning("udp", $"'{session.DeviceName}' could not open UDP to {tunnel.TargetEndpoint()}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Link went away mid-flow.
        }
        finally
        {
            _flows.TryRemove(new KeyValuePair<IPEndPoint, UdpFlow>(remote, flow));
            await flow.DisposeAsync().ConfigureAwait(false);

            if (mux is not null)
            {
                await mux.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SweepIdleFlowsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var cutoff = DateTimeOffset.UtcNow - FlowIdleTimeout;

            foreach (var (endpoint, flow) in _flows)
            {
                if (flow.LastActivity > cutoff)
                {
                    continue;
                }

                if (_flows.TryRemove(new KeyValuePair<IPEndPoint, UdpFlow>(endpoint, flow)))
                {
                    await flow.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task CloseAllFlowsAsync()
    {
        foreach (var flow in _flows.Values)
        {
            await flow.DisposeAsync().ConfigureAwait(false);
        }

        _flows.Clear();
    }

    /// <summary>
    /// Stops Windows reporting an ICMP "port unreachable" for a previously sent datagram as an
    /// exception on the next receive, which would otherwise take down a listener serving many
    /// unrelated flows.
    /// </summary>
    private static void DisableConnectionReset(UdpClient socket)
    {
        const int SIO_UDP_CONNRESET = -1744830452;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            socket.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            // Older or unusual stacks: the receive loop already tolerates the exception.
        }
    }

    /// <summary>
    /// Releases the port immediately and synchronously.
    /// <para>
    /// Rebinding after a change (say, adding UDP to an existing tunnel) happens on the very next
    /// statement, and an asynchronous teardown would still be holding the socket when the new
    /// listener tries to bind. Closing the socket here makes the port genuinely free by the time
    /// this returns; the receive loop then unwinds on its own.
    /// </para>
    /// </summary>
    public void Close()
    {
        _shutdown.Cancel();
        _socket?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Close();

        if (Task is not null)
        {
            try
            {
                await Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Going down anyway.
            }
        }

        await CloseAllFlowsAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}

/// <summary>
/// One visitor's UDP conversation, presented to the pump as a datagram channel. Inbound
/// datagrams arrive from the shared listener; outbound ones are sent back to that same address.
/// </summary>
internal sealed class UdpFlow : IDatagramChannel
{
    private readonly Channel<byte[]> _inbound;
    private readonly UdpClient _socket;
    private readonly IPEndPoint _remote;
    private int _disposed;

    public UdpFlow(UdpClient socket, IPEndPoint remote, int capacity)
    {
        _socket = socket;
        _remote = remote;

        // Dropping the oldest queued datagram under pressure matches what a congested network
        // would do, and keeps latency bounded instead of growing a backlog.
        _inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        LastActivity = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset LastActivity { get; private set; }

    public void Offer(byte[] datagram)
    {
        LastActivity = DateTimeOffset.UtcNow;
        _inbound.Writer.TryWrite(datagram);
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        LastActivity = DateTimeOffset.UtcNow;

        try
        {
            await _socket.SendAsync(datagram, _remote, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The visitor has gone; the idle sweep will collect this flow.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inbound.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}
