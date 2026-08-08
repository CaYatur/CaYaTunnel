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

    private void StartPublicListeners(CancellationToken cancellationToken)
    {
        _publicShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _publicShutdown.Token;

        if (Config.EnableHttpRouter)
        {
            _listenerTasks.Add(RunListenerAsync(Config.HttpPort, "http",
                (stream, remote, ct) => RouteHttpAsync(stream, remote, secure: false, ct), token));

            _listenerTasks.Add(RunListenerAsync(Config.HttpsPort, "https",
                (stream, remote, ct) => RouteHttpAsync(stream, remote, secure: true, ct), token));
        }

        if (Config.EnableMinecraftRouter)
        {
            _listenerTasks.Add(RunListenerAsync(Config.MinecraftPort, "minecraft",
                RouteHostAwareTcpAsync, token));
        }

        // One listener per dedicated-port tunnel that already exists.
        foreach (var tunnel in Registry.Tunnels.Where(t => t.Kind == TunnelKind.TcpPort && t.PublicPort.HasValue))
        {
            StartPortListener(tunnel.PublicPort!.Value);
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

    // ---- Dedicated TCP ports -------------------------------------------------

    private void StartPortListener(int port)
    {
        if (_publicShutdown is null || _portListeners.ContainsKey(port))
        {
            return;
        }

        var listener = new PortListener(port);
        if (!_portListeners.TryAdd(port, listener))
        {
            listener.Dispose();
            return;
        }

        listener.Task = RunListenerAsync(port, $"tcp:{port}",
            (stream, remote, ct) => RouteDedicatedPortAsync(port, stream, remote, ct),
            listener.Token);
    }

    private void StopPortListener(int port)
    {
        if (_portListeners.TryRemove(port, out var listener))
        {
            listener.Dispose();
            Log.Info("gateway", $"Stopped listening on TCP {port}.");
        }
    }

    private sealed class PortListener(int port) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; } = port;

        public CancellationToken Token => _cts.Token;

        public Task? Task { get; set; }

        public void Dispose()
        {
            _cts.Cancel();
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

        var listener = new TcpListener(IPAddress.Any, port);

        try
        {
            listener.Start();
            Log.Info("gateway", $"Listening on {port} ({label}).");
        }
        catch (SocketException ex)
        {
            // A port already in use must not take the whole gateway down — the other listeners
            // are still useful, and the operator gets a precise message.
            Log.Error("gateway", $"Could not listen on {port} ({label}) — {ex.Message}");
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

        await ForwardAsync(tunnel, stream, remote, cancellationToken).ConfigureAwait(false);
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
