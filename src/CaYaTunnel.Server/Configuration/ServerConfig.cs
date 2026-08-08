using CaYaTunnel.Core.Models;

namespace CaYaTunnel.Server.Configuration;

/// <summary>
/// Everything an operator can change about a deployment. No domain, host, port or provider is
/// baked into the source — someone cloning this repo configures their own here and never edits
/// code. Secrets in this object are encrypted at rest by <see cref="ServerConfigStore"/>.
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Display name for this deployment, shown in both UIs.</summary>
    public string ServerName { get; set; } = "CaYaTunnel Server";

    // ---- Control channel (the port you forward to this machine) -----------

    /// <summary>
    /// Port clients dial. Deliberately outside the well-known ranges so it does not collide
    /// with anything standard and attracts less scanner noise.
    /// </summary>
    public int ControlPort { get; set; } = 48771;

    /// <summary>Interface the control listener binds to. "0.0.0.0" means every interface.</summary>
    public string ControlBindAddress { get; set; } = "0.0.0.0";

    // ---- Public identity --------------------------------------------------

    /// <summary>
    /// Address users connect to for port-based tunnels — the public IP, or a hostname that
    /// resolves to it. Also what the client shows as the endpoint to copy.
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Zone that hostname tunnels are created under, e.g. "tunnel.example.com". Leave empty for
    /// an IP-and-port-only deployment; the UI then hides hostname tunnels instead of failing.
    /// </summary>
    public string BaseDomain { get; set; } = "";

    // ---- Public listeners --------------------------------------------------

    /// <summary>
    /// Serve agent links, HTTP, HTTPS and Minecraft all on <see cref="ControlPort"/>.
    /// <para>
    /// Everything that announces where it is going can share one port, because the gateway can
    /// read that from the first bytes: agent links by their TLS server name, websites by SNI or
    /// Host, Minecraft by its handshake. Tunnels with a dedicated public port are the exception
    /// and still need their own — a protocol that carries no destination cannot be told apart
    /// from any other, so the port number has to be the destination.
    /// </para>
    /// <para>
    /// The trade is that visitors reach websites on the control port rather than 443, unless
    /// something in front (Cloudflare, a load balancer) maps it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// On by default. A gateway that helps itself to 80, 443 and 25565 on a server already
    /// running something else is an ambush: the other service either stops working or ends up
    /// sharing the port, and visitors get TLS errors from a certificate that was never meant for
    /// them. Out of the box CaYaTunnel therefore touches exactly one port and nothing else.
    /// </remarks>
    public bool SinglePortMode { get; set; } = true;

    public bool EnableHttpRouter { get; set; } = true;

    public int HttpPort { get; set; } = 80;

    public int HttpsPort { get; set; } = 443;

    /// <summary>Shared listener for host-aware TCP tunnels (Minecraft Java).</summary>
    public bool EnableMinecraftRouter { get; set; } = true;

    public int MinecraftPort { get; set; } = 25565;

    /// <summary>Range dedicated TCP tunnels are allocated from when no port is given.</summary>
    public int TcpPortRangeStart { get; set; } = 32000;

    public int TcpPortRangeEnd { get; set; } = 32999;

    // ---- Authentication ----------------------------------------------------

    /// <summary>
    /// Secret embedded in provisioned clients. Regenerating it locks out every existing build —
    /// that is the intended kill switch, not a side effect.
    /// </summary>
    public string EnrollmentKey { get; set; } = "";

    /// <summary>Bumped on every rotation so a stale client gets "key rotated", not "bad key".</summary>
    public int KeyGeneration { get; set; } = 1;

    /// <summary>
    /// Keys retired by a rotation. Kept only so an old client can be told precisely why it was
    /// refused; they never grant access.
    /// </summary>
    public List<RetiredKey> RetiredKeys { get; set; } = [];

    /// <summary>When true a newly seen device is held until an operator approves it.</summary>
    public bool RequireManualApproval { get; set; }

    // ---- TLS ----------------------------------------------------------------

    /// <summary>
    /// Optional PFX for the control listener. Empty means the server generates and persists a
    /// self-signed certificate, which is fine because clients pin its fingerprint rather than
    /// trusting a CA.
    /// </summary>
    public string TlsCertificatePath { get; set; } = "";

    public string TlsCertificatePassword { get; set; } = "";

    /// <summary>
    /// Optional PFX used to terminate public HTTPS. Without one the gateway still serves HTTPS
    /// with its self-signed certificate, which works behind Cloudflare's "Full" mode.
    /// </summary>
    public string PublicTlsCertificatePath { get; set; } = "";

    public string PublicTlsCertificatePassword { get; set; } = "";

    /// <summary>
    /// Automatically obtain and renew a browser-trusted wildcard certificate with Let's Encrypt
    /// using Cloudflare DNS-01. This never requires ports 80 or 443 for validation.
    /// </summary>
    public bool AutomaticTlsEnabled { get; set; }

    /// <summary>Contact e-mail registered with the ACME account.</summary>
    public string AutomaticTlsEmail { get; set; } = "";

    /// <summary>
    /// Optional ports-free HTTPS endpoint. Off by default because port 443 may already belong to
    /// another service. When enabled, the public HTTPS router also binds 443 and reported HTTPS
    /// URLs omit the explicit port.
    /// </summary>
    public bool EnableStandardHttpsPort { get; set; }

    // ---- DNS ----------------------------------------------------------------

    public DnsSettings Dns { get; set; } = new();

    // ---- Housekeeping --------------------------------------------------------

    /// <summary>Start the gateway automatically when the admin app launches.</summary>
    public bool AutoStartGateway { get; set; } = true;

    /// <summary>Launch the admin app at Windows sign-in.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Start minimised to the tray when launched at sign-in.</summary>
    public bool StartMinimised { get; set; }

    public ServerInfo ToServerInfo(bool dnsAutomationEnabled) => new()
    {
        ServerName = ServerName,
        PublicHost = PublicHost,
        BaseDomain = BaseDomain,

        // In single-port mode visitors arrive on the shared port, so that is what the ports
        // reported to clients have to say. Reporting 443 here would have every tunnel display an
        // address that goes to whatever else is on 443.
        HttpPort = SinglePortMode ? ControlPort : HttpPort,
        HttpsPort = EnableStandardHttpsPort ? 443 : (SinglePortMode ? ControlPort : HttpsPort),
        MinecraftPort = SinglePortMode ? ControlPort : MinecraftPort,
        TcpPortRangeStart = TcpPortRangeStart,
        TcpPortRangeEnd = TcpPortRangeEnd,
        DnsAutomationEnabled = dnsAutomationEnabled,
        Features = ["http-router", "minecraft-router", "tcp-ports", "realtime-events", "client-provisioning"],
    };

    /// <summary>
    /// Ports the gateway binds for itself, which must never be allocated to a tunnel. Only the
    /// routers that are actually enabled are reserved, so turning one off frees its port.
    /// </summary>
    public IEnumerable<(string Name, int Port)> ReservedPorts()
    {
        yield return ("control port", ControlPort);

        // In single-port mode the shared listener owns the control port. The optional ports-free
        // HTTPS endpoint additionally owns 443, but remains off by default.
        if (SinglePortMode)
        {
            if (EnableStandardHttpsPort && ControlPort != 443)
            {
                yield return ("standard HTTPS port", 443);
            }

            yield break;
        }

        if (EnableHttpRouter)
        {
            yield return ("HTTP port", HttpPort);
            yield return ("HTTPS port", HttpsPort);
            if (EnableStandardHttpsPort && HttpsPort != 443)
            {
                yield return ("standard HTTPS port", 443);
            }
        }

        if (EnableMinecraftRouter)
        {
            yield return ("Minecraft port", MinecraftPort);
        }
    }

    /// <summary>Problems that would stop the gateway from starting, in operator language.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (ControlPort is < 1 or > 65535)
        {
            problems.Add("Control port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(EnrollmentKey))
        {
            problems.Add("No enrollment key has been generated yet.");
        }

        if (TcpPortRangeStart is < 1 or > 65535 || TcpPortRangeEnd is < 1 or > 65535)
        {
            problems.Add("TCP port range must fall between 1 and 65535.");
        }
        else if (TcpPortRangeEnd < TcpPortRangeStart)
        {
            problems.Add("TCP port range ends before it starts.");
        }

        // Every port the gateway binds for itself has to stay clear of the range it hands out,
        // or a tunnel gets allocated a port that is already in use and simply fails to bind.
        foreach (var (name, port) in ReservedPorts())
        {
            if (port >= TcpPortRangeStart && port <= TcpPortRangeEnd)
            {
                problems.Add($"The {name} ({port}) falls inside the tunnel port range and would be handed out to a tunnel.");
            }
        }

        if (string.IsNullOrWhiteSpace(PublicHost))
        {
            problems.Add("Public host is empty, so port-based tunnels have no address to show users.");
        }

        if (Dns.Provider == DnsProviderKind.Cloudflare)
        {
            if (string.IsNullOrWhiteSpace(Dns.CloudflareApiToken))
            {
                problems.Add("Cloudflare is selected but no API token is set.");
            }

            if (string.IsNullOrWhiteSpace(Dns.CloudflareZoneId))
            {
                problems.Add("Cloudflare is selected but no zone id is set.");
            }

            if (string.IsNullOrWhiteSpace(BaseDomain))
            {
                problems.Add("Cloudflare is selected but no base domain is set, so there is nothing to create records under.");
            }
        }

        if (AutomaticTlsEnabled)
        {
            if (Dns.Provider != DnsProviderKind.Cloudflare)
            {
                problems.Add("Automatic HTTPS currently requires Cloudflare DNS automation for DNS-01 validation.");
            }

            if (string.IsNullOrWhiteSpace(AutomaticTlsEmail) || !AutomaticTlsEmail.Contains('@'))
            {
                problems.Add("Automatic HTTPS needs a valid contact e-mail for the Let's Encrypt account.");
            }
        }

        if (EnableStandardHttpsPort && !EnableHttpRouter)
        {
            problems.Add("Ports-free HTTPS requires the HTTP/HTTPS hostname router to be enabled.");
        }

        return problems;
    }
}

/// <summary>A key removed by rotation, kept for diagnostics only.</summary>
public sealed class RetiredKey
{
    public int Generation { get; set; }

    public string Hash { get; set; } = "";

    public string Salt { get; set; } = "";

    public DateTimeOffset RetiredAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum DnsProviderKind
{
    /// <summary>Operator manages DNS by hand — typically one wildcard record.</summary>
    None,

    Cloudflare,
}

public sealed class DnsSettings
{
    public DnsProviderKind Provider { get; set; } = DnsProviderKind.None;

    public string CloudflareApiToken { get; set; } = "";

    public string CloudflareZoneId { get; set; } = "";

    /// <summary>Route records through Cloudflare's proxy (the orange cloud).</summary>
    public bool ProxyRecords { get; set; } = true;

    /// <summary>1 means "automatic" to Cloudflare.</summary>
    public int RecordTtl { get; set; } = 1;
}
