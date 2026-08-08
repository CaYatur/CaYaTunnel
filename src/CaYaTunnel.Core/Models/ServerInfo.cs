namespace CaYaTunnel.Core.Models;

/// <summary>
/// The server's public shape, pushed to clients so their UI can build endpoints and validate
/// input without anything being hard-coded on the client side. Every field here is operator
/// configuration — no domain, port or hostname is baked into the source.
/// </summary>
public sealed class ServerInfo
{
    public string ServerVersion { get; set; } = "1.0.0";

    /// <summary>Display name for this deployment, e.g. "CaYaDev Gateway".</summary>
    public string ServerName { get; set; } = "CaYaTunnel Server";

    /// <summary>
    /// Host or IP that public TCP endpoints resolve to, e.g. "203.0.113.10" or
    /// "gateway.example.com". Shown to users as the address to connect to.
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Zone new hostnames are created under, e.g. "tunnel.example.com". Empty means the
    /// deployment is IP-and-port only and hostname tunnels are unavailable.
    /// </summary>
    public string BaseDomain { get; set; } = "";

    public bool HostnameTunnelsAvailable => !string.IsNullOrWhiteSpace(BaseDomain);

    public int HttpPort { get; set; } = 80;

    public int HttpsPort { get; set; } = 443;

    /// <summary>Shared listener port for host-aware TCP tunnels (Minecraft Java by default).</summary>
    public int MinecraftPort { get; set; } = 25565;

    /// <summary>Inclusive range the server will allocate dedicated TCP ports from.</summary>
    public int TcpPortRangeStart { get; set; } = 32000;

    public int TcpPortRangeEnd { get; set; } = 32999;

    /// <summary>True when a DNS provider is configured, so hostnames can be created automatically.</summary>
    public bool DnsAutomationEnabled { get; set; }

    /// <summary>Capability strings, so newer clients can light up features older servers lack.</summary>
    public List<string> Features { get; set; } = [];

    public ServerInfo Clone()
    {
        var copy = (ServerInfo)MemberwiseClone();
        copy.Features = [.. Features];
        return copy;
    }
}
