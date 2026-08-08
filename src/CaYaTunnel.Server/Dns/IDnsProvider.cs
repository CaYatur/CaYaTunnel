namespace CaYaTunnel.Server.Dns;

/// <summary>
/// Creates and removes the DNS records that back hostname tunnels.
/// <para>
/// Only Cloudflare ships in 1.0, but the seam costs one file now and means adding Route 53 or
/// deSEC later touches nothing outside this folder.
/// </para>
/// </summary>
public interface IDnsProvider
{
    /// <summary>Name shown in the admin UI.</summary>
    string DisplayName { get; }

    /// <summary>False when the operator manages DNS by hand (typically one wildcard record).</summary>
    bool IsAutomated { get; }

    /// <summary>
    /// Points <paramref name="hostname"/> at <paramref name="target"/>, returning a provider
    /// record id for later removal. Implementations must be idempotent: creating a record that
    /// already points at the right place should succeed, not fail.
    /// </summary>
    /// <param name="allowProxy">
    /// Whether this record may go through the provider's HTTP proxy. Only HTTP tunnels may:
    /// a proxy that understands HTTP will happily accept a Minecraft or SSH connection and then
    /// fail to forward it, so port-based and host-aware TCP tunnels must resolve straight to the
    /// gateway.
    /// </param>
    Task<string?> CreateRecordAsync(
        string hostname,
        string target,
        bool allowProxy,
        CancellationToken cancellationToken = default);

    Task RemoveRecordAsync(string hostname, string? recordId, CancellationToken cancellationToken = default);

    /// <summary>Checks credentials so the admin UI can show a green tick instead of failing later.</summary>
    Task<DnsProviderStatus> TestAsync(CancellationToken cancellationToken = default);
}

public sealed record DnsProviderStatus(bool Ok, string Message, string? ZoneName = null);

/// <summary>
/// Used when the operator manages DNS themselves. Creating a record is a no-op that succeeds, so
/// hostname tunnels still work with a manually created wildcard record.
/// </summary>
public sealed class ManualDnsProvider : IDnsProvider
{
    public string DisplayName => "Manual (no automation)";

    public bool IsAutomated => false;

    public Task<string?> CreateRecordAsync(
        string hostname,
        string target,
        bool allowProxy,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task RemoveRecordAsync(string hostname, string? recordId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<DnsProviderStatus> TestAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DnsProviderStatus(true,
            "DNS is managed manually. Point a wildcard record at this server for hostname tunnels to resolve."));
}
