using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using CaYaTunnel.Core.Models;

namespace CaYaTunnel.Client;

/// <summary>
/// Answers "is this tunnel actually working?" by trying it, rather than by reporting what the
/// configuration says.
/// <para>
/// Two questions, in order, because they have different answers and different fixes. Is the
/// local service up? And does the public address reach it? Testing the public address first
/// would blame the tunnel for a service that was never running.
/// </para>
/// </summary>
public static class TunnelTester
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

    public static async Task<TunnelTestResult> RunAsync(
        TunnelDefinition tunnel,
        ServerInfo server,
        bool deviceOnline,
        bool targetIsLocal,
        CancellationToken cancellationToken = default)
    {
        var result = new TunnelTestResult();

        if (!deviceOnline)
        {
            result.DeviceOffline = true;
            return result;
        }

        // Only worth trying when this machine is the one carrying the tunnel; another device's
        // loopback address means nothing here.
        if (targetIsLocal)
        {
            result.TargetChecked = true;
            result.TargetReachable = await CanReachAsync(
                tunnel.TargetHost, tunnel.TargetPort, tunnel.ServesUdp && !tunnel.ServesTcp, cancellationToken)
                .ConfigureAwait(false);

            if (!result.TargetReachable)
            {
                // Testing the public path now would fail for a reason that has nothing to do
                // with the tunnel, and send the user looking in the wrong place.
                return result;
            }
        }

        var (host, port) = PublicEndpoint(tunnel, server);
        if (host is null)
        {
            return result;
        }

        result.PublicChecked = true;

        // A UDP-only tunnel has nothing to connect to: there is no handshake, and silence is
        // indistinguishable from a working service that had nothing to say.
        if (tunnel.ServesUdp && !tunnel.ServesTcp)
        {
            result.PublicUdpNotTestable = true;
            return result;
        }

        result.PublicReachable = await CanReachAsync(host, port, udp: false, cancellationToken).ConfigureAwait(false);

        if (result.PublicReachable && tunnel.Kind == TunnelKind.HttpHost)
        {
            // Reaching the gateway is not the same as reaching this tunnel — an unrouted
            // hostname still completes a TCP connection and then answers 404.
            result.RoutedCorrectly = await ReachesTunnelAsync(tunnel, host, port, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static (string? Host, int Port) PublicEndpoint(TunnelDefinition tunnel, ServerInfo server) => tunnel.Kind switch
    {
        TunnelKind.HttpHost => (tunnel.Hostname,
            tunnel.HttpAccess == HttpAccess.HttpOnly ? server.HttpPort : server.HttpsPort),
        TunnelKind.TcpHostAware => (tunnel.Hostname, tunnel.PublicPort ?? server.MinecraftPort),
        TunnelKind.PortForward => (server.PublicHost, tunnel.PublicPort ?? 0),
        _ => (null, 0),
    };

    private static async Task<bool> CanReachAsync(string host, int port, bool udp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StepTimeout);

        try
        {
            if (udp)
            {
                // UDP has no connect handshake. Binding a connected socket at least proves the
                // address resolves and is usable; it cannot prove anyone is listening.
                using var probe = new UdpClient();
                probe.Connect(host, port);
                return true;
            }

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a real request through the public address and checks the gateway routed it here
    /// rather than answering with its own "no tunnel for that hostname" page.
    /// </summary>
    private static async Task<bool> ReachesTunnelAsync(
        TunnelDefinition tunnel,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StepTimeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            Stream stream = client.GetStream();

            if (tunnel.HttpAccess != HttpAccess.HttpOnly)
            {
                // The gateway may present a self-signed certificate; this test is about routing,
                // not about trust, and the tunnel's own security does not depend on it.
                var tls = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host },
                    timeout.Token).ConfigureAwait(false);
                stream = tls;
            }

            var request = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: CaYaTunnel-Test\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            var buffer = new byte[2048];
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);

            if (read == 0)
            {
                return false;
            }

            var response = Encoding.ASCII.GetString(buffer, 0, read);

            // The gateway's own refusals are the only responses that mean "not routed here".
            // Anything the service itself returns — including its own 404 — means it was reached.
            return !response.Contains("CaYaTunnel", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException)
        {
            return false;
        }
    }
}

public sealed class TunnelTestResult
{
    public bool DeviceOffline { get; set; }

    public bool TargetChecked { get; set; }

    public bool TargetReachable { get; set; }

    public bool PublicChecked { get; set; }

    public bool PublicReachable { get; set; }

    /// <summary>UDP has no handshake, so "reachable" cannot be established by connecting.</summary>
    public bool PublicUdpNotTestable { get; set; }

    /// <summary>Null when the check does not apply — only HTTP tunnels can prove routing.</summary>
    public bool? RoutedCorrectly { get; set; }

    public bool LooksHealthy =>
        !DeviceOffline
        && (!TargetChecked || TargetReachable)
        && (!PublicChecked || PublicReachable || PublicUdpNotTestable)
        && RoutedCorrectly is not false;
}
