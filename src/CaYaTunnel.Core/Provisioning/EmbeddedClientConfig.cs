namespace CaYaTunnel.Core.Provisioning;

/// <summary>
/// Everything a provisioned client needs to find and authenticate to its server. The server
/// appends this to a copy of the client stub at download time, so the user downloads one exe
/// and runs it — no config file, no typing a key.
/// </summary>
public sealed class EmbeddedClientConfig
{
    /// <summary>Host or IP the client dials. Operator configuration, never hard-coded.</summary>
    public string ServerHost { get; set; } = "";

    public int ControlPort { get; set; }

    /// <summary>
    /// SHA-256 of the server's TLS certificate, hex encoded. The client pins this instead of
    /// trusting a CA, so the deployment works with a self-signed certificate and still refuses
    /// anything that isn't the server it was provisioned for.
    /// </summary>
    public string ServerCertificateFingerprint { get; set; } = "";

    public string EnrollmentKey { get; set; } = "";

    /// <summary>Generation this build was cut against; a server-side bump locks this build out.</summary>
    public int KeyGeneration { get; set; }

    /// <summary>Display name of the deployment, shown in the client's title bar.</summary>
    public string ServerName { get; set; } = "CaYaTunnel";

    /// <summary>Pre-assigned identity when the operator provisioned for a known machine.</summary>
    public string? DeviceId { get; set; }

    public string? SuggestedDeviceName { get; set; }

    public DateTimeOffset ProvisionedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Free-text note recorded by the server UI, e.g. who generated this build.</summary>
    public string? ProvisionedBy { get; set; }

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(ServerHost)
        && ControlPort is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(EnrollmentKey);
}
