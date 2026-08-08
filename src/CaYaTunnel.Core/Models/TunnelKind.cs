namespace CaYaTunnel.Core.Models;

/// <summary>
/// How a tunnel is reached from the public internet. This distinction is load-bearing: the
/// three kinds need genuinely different listeners, so it is modelled explicitly rather than
/// as an "isHttp" flag.
/// </summary>
public enum TunnelKind
{
    /// <summary>
    /// Shared HTTP/HTTPS listener. Many tunnels live behind the same public 80/443 and are
    /// told apart by the TLS SNI name or the HTTP Host header.
    /// e.g. panel.tunnel.example.com -> 127.0.0.1:3000
    /// </summary>
    HttpHost,

    /// <summary>
    /// Shared TCP listener for protocols that announce the hostname during their handshake,
    /// so several tunnels can share one public port. Minecraft Java is the built-in case.
    /// e.g. mc1.tunnel.example.com:25565 and mc2.tunnel.example.com:25565
    /// </summary>
    TcpHostAware,

    /// <summary>
    /// A dedicated public port carrying TCP, UDP or both. Most protocols announce no hostname,
    /// so the port itself is the whole routing decision.
    /// e.g. 203.0.113.10:32001 -> CAGAN-PC -> 127.0.0.1:25565
    /// </summary>
    PortForward,
}

/// <summary>
/// Which transports a <see cref="TunnelKind.PortForward"/> tunnel serves on its public port.
/// <para>
/// Both at once is the common case for game servers, which typically listen on the same port
/// number for TCP and UDP and need both to work.
/// </para>
/// </summary>
[Flags]
public enum TransportProtocols
{
    None = 0,
    Tcp = 1,
    Udp = 2,
    Both = Tcp | Udp,
}

/// <summary>
/// Which public schemes an <see cref="TunnelKind.HttpHost"/> tunnel answers on. The gateway
/// listens on both 80 and 443 for everyone; this decides what a given hostname does with each.
/// </summary>
public enum HttpAccess
{
    /// <summary>Serve the tunnel on both http:// and https://.</summary>
    HttpAndHttps,

    /// <summary>Answer only on https://; plain HTTP is refused.</summary>
    HttpsOnly,

    /// <summary>Answer only on http://. Useful for a service that must not be behind TLS.</summary>
    HttpOnly,

    /// <summary>Answer on https://, and send plain HTTP a permanent redirect to it.</summary>
    RedirectToHttps,
}

/// <summary>
/// Handshake parsers available to <see cref="TunnelKind.TcpHostAware"/> tunnels. Stored as a
/// string on the wire so third parties can add parsers without a protocol bump.
/// </summary>
public static class HostAwareProtocols
{
    /// <summary>Minecraft Java Edition handshake packet (server address field).</summary>
    public const string MinecraftJava = "minecraft-java";

    public static readonly IReadOnlyList<string> All = [MinecraftJava];
}
