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
    /// Plain TCP. The protocol carries no hostname, so the tunnel owns a dedicated public
    /// port and routing is purely port -&gt; device -&gt; target.
    /// e.g. 203.0.113.10:32001 -> CAGAN-PC -> 127.0.0.1:25565
    /// </summary>
    TcpPort,
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
