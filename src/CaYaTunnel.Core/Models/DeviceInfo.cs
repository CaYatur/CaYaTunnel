namespace CaYaTunnel.Core.Models;

/// <summary>
/// A registered client machine, as mirrored to every other client. Deliberately contains no
/// secret material — the key hash lives only in the server's private record.
/// </summary>
public sealed class DeviceInfo
{
    public required string Id { get; init; }

    /// <summary>Defaults to the machine name the client reports; renameable from either UI.</summary>
    public required string Name { get; set; }

    public bool Online { get; set; }

    /// <summary>Revoked devices are kept for the audit trail but can never authenticate again.</summary>
    public bool Revoked { get; set; }

    /// <summary>Public address the client connected from, as seen by the server.</summary>
    public string? RemoteAddress { get; set; }

    /// <summary>Addresses the machine holds on its own LANs — shown in the UI next to the device.</summary>
    public List<string> LocalAddresses { get; set; } = [];

    public string? ClientVersion { get; set; }

    public string? OperatingSystem { get; set; }

    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>
    /// Which generation of the server key this device was provisioned against. Bumping the
    /// server generation invalidates every client still carrying an older one.
    /// </summary>
    public int KeyGeneration { get; set; }

    /// <summary>Round-trip time of the last keep-alive, in milliseconds.</summary>
    public int? LatencyMs { get; set; }

    public DeviceInfo Clone()
    {
        var copy = (DeviceInfo)MemberwiseClone();
        copy.LocalAddresses = [.. LocalAddresses];
        return copy;
    }
}
