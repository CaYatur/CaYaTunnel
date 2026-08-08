using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Server.Routing;

namespace CaYaTunnel.Server.Gateway;

/// <summary>
/// The public side of the gateway. Three listener shapes, because the three tunnel kinds really
/// are different problems:
/// <list type="bullet">
/// <item>HTTP/HTTPS — one shared pair of ports, split by Host header or TLS SNI.</item>
/// <item>Host-aware TCP — one shared port, split by a protocol-specific handshake.</item>
/// <item>Plain TCP — no hostname exists in the protocol, so one listener per tunnel.</item>
/// </list>
/// </summary>
public sealed partial class TunnelServer
{
    private static readonly TimeSpan SniffTimeout = TimeSpan.FromSeconds(10);
    private const int SniffBufferSize = 16 * 1024;

    private readonly ConcurrentDictionary<int, PortListener> _portListeners = new();
    private CancellationTokenSource? _publicShutdown;

    /// <summary>
    /// Serves a connection that arrived on the shared control port. Everything that announces
    /// where it is going can be told apart here, which is what lets one open port carry agent
    /// links and public traffic at once.
    /// </summary>
    private async Task RouteSharedPortAsync(Stream client, IPEndPoint? remote, CancellationToken cancellationToken)
    {
        var prefix = await StreamPeeker.PeekAsync(
            client,
            SniffBufferSize,
            data => Classify(data.Span) != SharedPortProtocol.Unknown || data.Length >= 4096,
            SniffTimeout,
            cancellationToken).ConfigureAwait(false);

        if (prefix.Length == 0)
        {
            return;
        }

        var kind = Classify(prefix);

        // The peeked bytes are replayed, so each handler reads the connection from its start and
        // needs no knowledge that anything looked at it first.
        var stream = new PrefixedStream(client, prefix);

        switch (kind)
        {
            case SharedPortProtocol.Control:
                await HandleControlConnectionAsync(stream, remote, cancellationToken).ConfigureAwait(false);
                break;

            case SharedPortProtocol.Https:
                await RouteHttpAsync(stream, remote, secure: true, cancellationToken).ConfigureAwait(false);
                break;

            case SharedPortProtocol.Http:
                await RouteHttpAsync(stream, remote, secure: false, cancellationToken).ConfigureAwait(false);
                break;

            case SharedPortProtocol.Minecraft:
                await RouteHostAwareTcpAsync(stream, remote, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // Anything that announced no destination goes to the tunnel that claimed the
                // shared port, if one has. That is what lets a plain TCP service share the port
                // too, and why only one of them can.
                if (Registry.FindSharedPortTunnel(TransportProtocols.Tcp) is { } fallback)
                {
                    await ForwardAsync(fallback, stream, remote, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Log.Debug("gateway", $"{remote} sent something unrecognised on the shared port.");
                }

                break;
        }
    }

    private enum SharedPortProtocol
    {
        Unknown,
        Control,
        Https,
        Http,
        Minecraft,
    }

    private static SharedPortProtocol Classify(ReadOnlySpan<byte> data)
    {
        if (ProtocolSniffers.LooksLikeTls(data))
        {
            var sni = ProtocolSniffers.ReadTlsSni(data);
            if (sni is null)
            {
                return SharedPortProtocol.Unknown; // incomplete hello; keep reading
            }

            return string.Equals(sni, ProtocolConstants.ControlSniName, StringComparison.OrdinalIgnoreCase)
                ? SharedPortProtocol.Control
                : SharedPortProtocol.Https;
        }

        if (ProtocolSniffers.LooksLikeHttp(data))
        {
            return SharedPortProtocol.Http;
        }

        return ProtocolSniffers.ReadMinecraftHostname(data) is not null
            ? SharedPortProtocol.Minecraft
            : SharedPortProtocol.Unknown;
    }

    private void StartPublicListeners(CancellationToken cancellationToken)
    {
        _publicShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _publicShutdown.Token;

        // In single-port mode the control listener already serves HTTP, HTTPS and Minecraft.
        // The optional standard HTTPS endpoint is deliberately separate and off by default so
        // installations already using port 443 are never disturbed.
        if (Config.SinglePortMode)
        {
            if (Config.EnableStandardHttpsPort && Config.ControlPort != 443 && Config.EnableHttpRouter)
            {
                _listenerTasks.Add(RunListenerAsync(443, "https-standard",
                    (stream, remote, ct) => RouteHttpAsync(stream, remote, secure: true, ct), token));
                Log.Info("gateway", "Ports-free HTTPS enabled on port 443; the shared tunnel port remains unchanged.");
            }

            foreach (var tunnel in Registry.Tunnels.Where(t => t.Kind == TunnelKind.PortForward && t.PublicPort.HasValue))
            {
                StartPortListener(tunnel);
            }

            // UDP is a separate namespace from TCP, so the shared UDP tunnel can use the same
            // port number without conflicting with the control listener above it.
            if (Registry.FindSharedPortTunnel(TransportProtocols.Udp) is not null)
            {
                StartSharedUdpListener(token);
            }

            Log.Info("gateway", $"Single-port mode: agent links, HTTP, HTTPS and Minecraft all share {Config.ControlPort}.");
            return;
        }

        if (Config.EnableHttpRouter)
        {
            _listenerTasks.Add(RunListenerAsync(Config.HttpPort, "http",
                (stream, remote, ct) => RouteHttpAsync(stream, remote, secure: false, ct), token));

            _listenerTasks.Add(RunListenerAsync(Config.HttpsPort, "https",
                (stream, remote, ct) => RouteHttpAsync(stream, remote, secure: true, ct), token));

            if (Config.EnableStandardHttpsPort && Config.HttpsPort != 443)
            {
                _listenerTasks.Add(RunListenerAsync(443, "https-standard",
                    (stream, remote, ct) => RouteHttpAsync(stream, remote, secure: true, ct), token));
            }
        }

        if (Config.EnableMinecraftRouter)
        {
            _listenerTasks.Add(RunListenerAsync(Config.MinecraftPort, "minecraft",
                RouteHostAwareTcpAsync, token));
        }

        // One listener set per dedicated-port tunnel that already exists.
        foreach (var tunnel in Registry.Tunnels.Where(t => t.Kind == TunnelKind.PortForward && t.PublicPort.HasValue))
        {
            StartPortListener(tunnel);
        }
    }

    private void StopPublicListeners()
    {
        foreach (var listener in _portListeners.Values)
        {
            listener.Dispose();
        }

        _portListeners.Clear();
        _publicShutdown?.Cancel();
        _publicShutdown?.Dispose();
        _publicShutdown = null;
    }

    // ---- Dedicated ports (TCP, UDP, or both) ----------------------------------

    /// <summary>
    /// Binds whichever transports the tunnel asked for on its public port. A game server usually
    /// wants both on the same number, so they are managed together rather than as two tunnels.
    /// </summary>
    /// <summary>
    /// Serves the shared-port UDP tunnel on the control port's number. Separate from the TCP
    /// listener because UDP and TCP are different protocols: the same number is a different
    /// socket, and binding both is not a conflict.
    /// </summary>
    /// <summary>
    /// Starts or stops the shared UDP listener to match whether a tunnel currently claims it.
    /// Called whenever tunnels change, because a shared-port tunnel created after the gateway
    /// started would otherwise have nothing listening for it.
    /// </summary>
    private void EnsureSharedUdpListener()
    {
        if (!Config.SinglePortMode || _publicShutdown is null)
        {
            return;
        }

        var wanted = Registry.FindSharedPortTunnel(TransportProtocols.Udp) is not null;
        var running = _portListeners.ContainsKey(Config.ControlPort);

        if (wanted && !running)
        {
            StartSharedUdpListener(_publicShutdown.Token);
        }
        else if (!wanted && running)
        {
            StopPortListener(Config.ControlPort);
        }
    }

    private void StartSharedUdpListener(CancellationToken cancellationToken)
    {
        var port = Config.ControlPort;
        if (_portListeners.ContainsKey(port))
        {
            return;
        }

        var listener = new PortListener(port);
        if (!_portListeners.TryAdd(port, listener))
        {
            listener.Dispose();
            return;
        }

        var udp = new UdpPortRouter(port, this, Log, () => Registry.FindSharedPortTunnel(TransportProtocols.Udp));
        udp.Start(listener.Token);
        listener.Udp = udp;
    }

    private void StartPortListener(TunnelDefinition tunnel)
    {
        // A tunnel riding the shared port has no listener of its own; the control port's
        // classifier and the shared UDP listener already serve it.
        if (tunnel.UseSharedPort)
        {
            return;
        }

        if (_publicShutdown is null || tunnel.PublicPort is not { } port)
        {
            return;
        }

        if (_portListeners.ContainsKey(port))
        {
            return;
        }

        var listener = new PortListener(port);
        if (!_portListeners.TryAdd(port, listener))
        {
            listener.Dispose();
            return;
        }

        if (tunnel.ServesTcp)
        {
            listener.TcpTask = RunListenerAsync(port, $"tcp:{port}",
                (stream, remote, ct) => RouteDedicatedPortAsync(port, stream, remote, ct),
                listener.Token);
        }

        if (tunnel.ServesUdp)
        {
            var udp = new UdpPortRouter(port, this, Log);
            udp.Start(listener.Token);
            listener.Udp = udp;
        }
    }

    private void StopPortListener(int port)
    {
        if (_portListeners.TryRemove(port, out var listener))
        {
            listener.Dispose();
            Log.Info("gateway", $"Stopped listening on port {port}.");
        }
    }

    /// <summary>Rebinds a port after its transports changed.</summary>
    private void RestartPortListener(TunnelDefinition tunnel)
    {
        if (tunnel.PublicPort is not { } port)
        {
            return;
        }

        StopPortListener(port);

        if (tunnel.Enabled)
        {
            StartPortListener(tunnel);
        }
    }

    private sealed class PortListener(int port) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; } = port;

        public CancellationToken Token => _cts.Token;

        public Task? TcpTask { get; set; }

        public UdpPortRouter? Udp { get; set; }

        public void Dispose()
        {
            _cts.Cancel();

            // Closed synchronously so the port is actually free before anything tries to rebind
            // it; the router's own loops finish unwinding in the background.
            Udp?.Close();

            _cts.Dispose();
        }
    }

    // ---- Generic accept loop ---------------------------------------------------

    private async Task RunListenerAsync(
        int port,
        string label,
        Func<Stream, IPEndPoint?, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            Log.Error("gateway", $"Refusing to listen on an invalid port for {label}: {port}.");
            return;
        }

        // Exclusive on purpose. Windows otherwise lets this bind 0.0.0.0:443 while another
        // service holds 443 on one specific address, and then splits traffic between them at
        // random — the other service's visitors intermittently get this gateway's certificate.
        // Refusing to start a listener is always better than quietly stealing half of someone
        // else's traffic.
        var listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };

        try
        {
            listener.Start();
            Log.Info("gateway", $"Listening on {port} ({label}).");
        }
        catch (SocketException ex)
        {
            // Loud, not just logged: a port that is not bound means this listener silently does
            // nothing, and the operator needs to know which one and why. The two causes need
            // different answers, so they are not collapsed into one message — a reserved range
            // cannot be freed by stopping anything.
            var message = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse =>
                    $"Port {port} ({label}) is already in use by something else, so it was not opened. "
                    + "Turn on single-port mode, or change the port, or stop whatever is using it.",

                SocketError.AccessDenied =>
                    $"Port {port} ({label}) could not be opened: Windows reserves it, or this account may not bind it. "
                    + "Check with: netsh int ipv4 show excludedportrange protocol=tcp",

                _ => $"Port {port} ({label}) could not be opened — {ex.SocketErrorCode}: {ex.Message}",
            };

            Log.Error("gateway", message);
            ListenerFailed?.Invoke(message);
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    var remote = client.Client.RemoteEndPoint as IPEndPoint;
                    try
                    {
                        client.NoDelay = true;
                        await handler(client.GetStream(), remote, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException)
                    {
                        Log.Debug(label, $"Connection from {remote} ended: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(label, $"Unexpected failure serving {remote}", ex);
                    }
                    finally
                    {
                        client.Dispose();
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (SocketException ex)
        {
            Log.Error("gateway", $"Listener on {port} ({label}) stopped — {ex.Message}");
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---- HTTP / HTTPS ------------------------------------------------------------

    private async Task RouteHttpAsync(Stream client, IPEndPoint? remote, bool secure, CancellationToken cancellationToken)
    {
        Stream stream = client;
        string? hostname;

        if (secure)
        {
            var hello = await StreamPeeker.PeekAsync(
                stream,
                SniffBufferSize,
                data => ProtocolSniffers.ReadTlsSni(data.Span) is not null || data.Length >= 4096,
                SniffTimeout,
                cancellationToken).ConfigureAwait(false);

            if (hello.Length == 0)
            {
                return;
            }

            hostname = ProtocolSniffers.ReadTlsSni(hello);
            stream = new PrefixedStream(stream, hello);

            if (hostname is null)
            {
                Log.Debug("https", $"{remote} sent no SNI; nothing to route to.");
                return;
            }

            var sniTunnel = Registry.FindByHostname(hostname, TunnelKind.HttpHost);
            if (sniTunnel is null)
            {
                Log.Debug("https", $"No tunnel is registered for {hostname}.");
                return;
            }

            if (sniTunnel.HttpAccess == HttpAccess.HttpOnly)
            {
                // The operator asked for plain HTTP only. Completing the TLS handshake just to
                // refuse would be worse: the browser would show a certificate the site never
                // meant to present.
                Log.Debug("https", $"'{sniTunnel.Name}' is configured for plain HTTP only.");
                return;
            }

            if (!sniTunnel.TerminateTls)
            {
                // Passthrough: the service behind the tunnel speaks TLS itself, so the encrypted
                // bytes travel untouched and the gateway never sees the plaintext.
                await ForwardAsync(sniTunnel, stream, remote, cancellationToken).ConfigureAwait(false);
                return;
            }

            var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _publicCertificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
                Log.Debug("https", $"TLS handshake with {remote} failed: {ex.Message}");
                return;
            }

            // Wrapped so the pump can still half-close through the TLS layer to the socket.
            await using var terminated = new TlsVisitorStream(tls, stream);
            await ForwardAsync(sniTunnel, terminated, remote, cancellationToken).ConfigureAwait(false);
            return;
        }

        var head = await StreamPeeker.PeekAsync(
            stream,
            SniffBufferSize,
            data => ProtocolSniffers.HasCompleteHttpHead(data.Span),
            SniffTimeout,
            cancellationToken).ConfigureAwait(false);

        if (head.Length == 0)
        {
            return;
        }

        hostname = ProtocolSniffers.ReadHttpHost(head);
        stream = new PrefixedStream(stream, head);

        if (hostname is null)
        {
            await WriteHttpErrorAsync(stream, 400, "No Host header", cancellationToken).ConfigureAwait(false);
            return;
        }

        var tunnel = Registry.FindByHostname(hostname, TunnelKind.HttpHost);
        if (tunnel is null)
        {
            await WriteHttpErrorAsync(stream, 404, $"No tunnel is registered for {hostname}", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        switch (tunnel.HttpAccess)
        {
            case HttpAccess.HttpsOnly:
                await WriteHttpErrorAsync(stream, 403,
                    "This tunnel is available over HTTPS only.", cancellationToken).ConfigureAwait(false);
                return;

            case HttpAccess.RedirectToHttps:
                await WriteRedirectAsync(stream, $"https://{hostname}", ProtocolSniffers.ReadHttpTarget(head),
                    cancellationToken).ConfigureAwait(false);
                return;
        }

        await ForwardAsync(tunnel, stream, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a permanent redirect, preserving the path and query the visitor asked for.</summary>
    private static async Task WriteRedirectAsync(Stream stream, string origin, string? target, CancellationToken cancellationToken)
    {
        var location = origin + (string.IsNullOrEmpty(target) ? "/" : target);

        var response = "HTTP/1.1 301 Moved Permanently\r\n"
            + $"Location: {location}\r\n"
            + "Content-Length: 0\r\n"
            + "Connection: close\r\n\r\n";

        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Visitor gave up first.
        }
    }

    /// <summary>
    /// A plain-HTTP reply for the cases a browser would otherwise show as a dead connection.
    /// Only used on the cleartext listener, where we know the client speaks HTTP.
    /// </summary>
    private static async Task WriteHttpErrorAsync(Stream stream, int status, string message, CancellationToken cancellationToken)
    {
        var reason = status switch
        {
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            _ => "Error",
        };

        var body = $"<!doctype html><meta charset=\"utf-8\"><title>{status} {reason}</title>"
            + $"<body style=\"font:14px system-ui;background:#0d0d10;color:#e8e8ea;padding:3rem\">"
            + $"<h1 style=\"color:#e8232a\">{status} {reason}</h1><p>{WebUtility.HtmlEncode(message)}</p>"
            + "<p style=\"opacity:.6\">CaYaTunnel</p></body>";

        var response = $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
            + "Connection: close\r\n\r\n"
            + body;

        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Client gave up first.
        }
    }

    // ---- Host-aware TCP (Minecraft Java) --------------------------------------------

    private async Task RouteHostAwareTcpAsync(Stream client, IPEndPoint? remote, CancellationToken cancellationToken)
    {
        var handshake = await StreamPeeker.PeekAsync(
            client,
            SniffBufferSize,
            data => ProtocolSniffers.HasCompleteMinecraftHandshake(data.Span),
            SniffTimeout,
            cancellationToken).ConfigureAwait(false);

        if (handshake.Length == 0)
        {
            return;
        }

        var hostname = ProtocolSniffers.ReadMinecraftHostname(handshake);
        var stream = new PrefixedStream(client, handshake);

        if (hostname is null)
        {
            Log.Debug("minecraft", $"{remote} did not send a readable handshake.");
            return;
        }

        var tunnel = Registry.FindByHostname(hostname, TunnelKind.TcpHostAware);
        if (tunnel is null)
        {
            Log.Debug("minecraft", $"No tunnel is registered for {hostname}.");
            return;
        }

        await ForwardAsync(tunnel, stream, remote, cancellationToken).ConfigureAwait(false);
    }

    // ---- Dedicated port ----------------------------------------------------------------

    private async Task RouteDedicatedPortAsync(int port, Stream client, IPEndPoint? remote, CancellationToken cancellationToken)
    {
        var tunnel = Registry.FindByPublicPort(port);
        if (tunnel is null)
        {
            Log.Debug("tcp", $"Nothing is registered on port {port} any more.");
            return;
        }

        // No sniffing here on purpose: a plain TCP protocol carries no destination, so the port
        // itself is the entire routing decision.
        await ForwardAsync(tunnel, client, remote, cancellationToken).ConfigureAwait(false);
    }

    // ---- Shared forwarding -------------------------------------------------------------

    private async Task ForwardAsync(TunnelDefinition tunnel, Stream visitor, IPEndPoint? remote, CancellationToken cancellationToken)
    {
        var session = FindSession(tunnel.DeviceId);
        if (session is null)
        {
            Log.Debug("route", $"'{tunnel.Name}' is registered but its device is offline.");
            if (tunnel.Kind == TunnelKind.HttpHost && tunnel.TerminateTls)
            {
                await WriteHttpErrorAsync(visitor, 503, "The device serving this tunnel is offline.", cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        // Only possible where the gateway sees plaintext: a passthrough TLS tunnel is encrypted
        // end to end and there is no header here to change.
        if (tunnel.RewriteHostHeader && tunnel.Kind == TunnelKind.HttpHost && tunnel.TerminateTls)
        {
            visitor = new HostRewritingStream(visitor, tunnel.TargetEndpoint());
        }

        MuxStream mux;
        try
        {
            mux = await session.OpenStreamAsync(tunnel, remote?.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (TargetUnreachableException ex)
        {
            Log.Warning("route", $"'{session.DeviceName}' could not reach {tunnel.TargetEndpoint()}: {ex.Message}");
            if (tunnel.Kind == TunnelKind.HttpHost && tunnel.TerminateTls)
            {
                await WriteHttpErrorAsync(visitor, 502, ex.Message, cancellationToken).ConfigureAwait(false);
            }

            return;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return; // link went away mid-open
        }

        var opened = Registry.RecordTraffic(tunnel.Id, 0, 0, activeDelta: 1);
        if (opened is not null)
        {
            Broadcast(ControlMessageTypes.TunnelStats, opened);
        }

        try
        {
            var (toVisitor, fromVisitor) = await StreamPump.RunAsync(mux, visitor, cancellationToken).ConfigureAwait(false);

            var closed = Registry.RecordTraffic(tunnel.Id, fromVisitor, toVisitor, activeDelta: -1);
            if (closed is not null)
            {
                Broadcast(ControlMessageTypes.TunnelStats, closed);
            }

            Log.Debug("route", $"{remote} -> '{tunnel.Name}' finished ({fromVisitor} in, {toVisitor} out).");
        }
        finally
        {
            await mux.DisposeAsync().ConfigureAwait(false);
        }
    }
}
