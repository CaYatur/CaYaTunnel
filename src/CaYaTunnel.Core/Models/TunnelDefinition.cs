namespace CaYaTunnel.Core.Models;

/// <summary>
/// One public endpoint mapped onto one target reachable from a client device. This is the
/// record the server persists and mirrors to every connected client.
/// </summary>
public sealed class TunnelDefinition
{
    public required string Id { get; init; }

    /// <summary>Human label shown in both UIs. Free text; not used for routing.</summary>
    public required string Name { get; set; }

    public required TunnelKind Kind { get; set; }

    /// <summary>Device that carries the traffic — the machine the target is reachable from.</summary>
    public required string DeviceId { get; set; }

    /// <summary>
    /// Where the client connects once traffic arrives. Either loopback (127.0.0.1) or any
    /// LAN address the client machine can reach (192.168.1.20), which is what makes this a
    /// forwarder rather than a localhost-only tool.
    /// </summary>
    public required string TargetHost { get; set; }

    public required int TargetPort { get; set; }

    /// <summary>
    /// Full public hostname for <see cref="TunnelKind.HttpHost"/> and
    /// <see cref="TunnelKind.TcpHostAware"/>, e.g. "panel.tunnel.example.com". Null otherwise.
    /// </summary>
    public string? Hostname { get; set; }

    /// <summary>
    /// Dedicated public port for <see cref="TunnelKind.PortForward"/>, or the shared listener port
    /// for <see cref="TunnelKind.TcpHostAware"/>. Null for <see cref="TunnelKind.HttpHost"/>,
    /// which always rides the shared 80/443.
    /// </summary>
    public int? PublicPort { get; set; }

    /// <summary>Handshake parser id for <see cref="TunnelKind.TcpHostAware"/>.</summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// HTTP only. When true the gateway terminates TLS and speaks plain HTTP to the client;
    /// when false it forwards the encrypted bytes untouched (the local service does its own TLS).
    /// </summary>
    public bool TerminateTls { get; set; } = true;

    /// <summary>
    /// HTTP only. Which of the two public schemes this hostname answers on. The gateway always
    /// listens on both ports; this decides what each does for this particular tunnel.
    /// </summary>
    public HttpAccess HttpAccess { get; set; } = HttpAccess.HttpAndHttps;

    /// <summary>
    /// <see cref="TunnelKind.PortForward"/> only. Which transports the public port serves.
    /// Both is common: a game server usually needs TCP and UDP on the same number.
    /// </summary>
    public TransportProtocols Transports { get; set; } = TransportProtocols.Tcp;

    /// <summary>
    /// <see cref="TunnelKind.PortForward"/> only: ride the gateway's shared port instead of
    /// taking one of its own.
    /// <para>
    /// A plain protocol says nothing about where it is going, so the gateway can only serve one
    /// such tunnel per transport on a shared port — whatever arrives that is not a recognised
    /// agent link, website or Minecraft handshake goes here. One is often all that is needed,
    /// and it is the difference between forwarding one port and forwarding several.
    /// </para>
    /// </summary>
    public bool UseSharedPort { get; set; }

    public bool ServesTcp => Kind != TunnelKind.PortForward || (Transports & TransportProtocols.Tcp) != 0;

    public bool ServesUdp => Kind == TunnelKind.PortForward && (Transports & TransportProtocols.Udp) != 0;

    /// <summary>Disabled tunnels stay in the registry but stop accepting traffic.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Set when the server auto-created the DNS record, so it can clean it up on delete.</summary>
    public string? DnsRecordId { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Device that requested creation — may differ from <see cref="DeviceId"/>.</summary>
    public string? CreatedByDeviceId { get; init; }

    public DateTimeOffset? LastActiveAt { get; set; }

    public long BytesIn { get; set; }

    public long BytesOut { get; set; }

    public long TotalConnections { get; set; }

    /// <summary>Live count of streams currently open for this tunnel; not persisted.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>What a user would paste to reach this tunnel.</summary>
    public string PublicEndpoint(ServerInfo server) => Kind switch
    {
        TunnelKind.HttpHost => $"{HttpScheme}://{Hostname}",
        TunnelKind.TcpHostAware => $"{Hostname}:{PublicPort ?? 0}",
        TunnelKind.PortForward => $"{server.PublicHost}:{PublicPort ?? 0}",
        _ => "-",
    };

    /// <summary>The scheme to show for an HTTP tunnel — http only when TLS is genuinely off.</summary>
    public string HttpScheme => HttpAccess == HttpAccess.HttpOnly ? "http" : "https";

    /// <summary>"TCP", "UDP" or "TCP + UDP", for the transports this tunnel actually carries.</summary>
    public string TransportLabel => Kind != TunnelKind.PortForward
        ? "TCP"
        : Transports switch
        {
            TransportProtocols.Both => "TCP + UDP",
            TransportProtocols.Udp => "UDP",
            _ => "TCP",
        };

    public string TargetEndpoint() => $"{TargetHost}:{TargetPort}";

    public TunnelDefinition Clone() => (TunnelDefinition)MemberwiseClone();
}
