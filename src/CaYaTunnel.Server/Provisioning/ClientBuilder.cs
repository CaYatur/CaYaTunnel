using CaYaTunnel.Core.Provisioning;
using CaYaTunnel.Core.Security;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Registry;

namespace CaYaTunnel.Server.Provisioning;

/// <summary>
/// Produces a ready-to-run client executable for a given deployment.
/// <para>
/// No compiler is involved and none is needed on the VPS: a prebuilt client stub is copied and
/// the configuration is appended to its tail. See <see cref="ClientConfigBlob"/> for the format
/// and for why provisioned builds are unsigned.
/// </para>
/// </summary>
public sealed class ClientBuilder(ServerConfig config, TunnelRegistry registry, string certificateFingerprint)
{
    /// <summary>
    /// Where a client stub can be found: the configured location, or beside the server
    /// executable, which is where a release archive puts it.
    /// </summary>
    public static IEnumerable<string> CandidateStubPaths()
    {
        yield return ServerPaths.ClientStubFile;
        yield return Path.Combine(AppContext.BaseDirectory, "CaYaTunnelClient.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "client", "CaYaTunnelClient.exe");
    }

    public static string? FindStub()
        => CandidateStubPaths().FirstOrDefault(File.Exists);

    /// <summary>
    /// Writes a provisioned client to <paramref name="outputPath"/>.
    /// <para>
    /// When <paramref name="deviceId"/> names a registered device, that device gets its own key
    /// and the build only works there — revoking the device kills that build alone. Otherwise
    /// the shared enrollment key is embedded, which works on any machine and is only invalidated
    /// by rotating the server key.
    /// </para>
    /// </summary>
    public async Task<ProvisionedClient> BuildAsync(
        string stubPath,
        string outputPath,
        string? deviceId = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stubPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (string.IsNullOrWhiteSpace(config.PublicHost))
        {
            throw new InvalidOperationException(
                "Set the server's public host before building a client — otherwise the client has no address to dial.");
        }

        var record = string.IsNullOrWhiteSpace(deviceId) ? null : registry.FindDevice(deviceId);
        string key;

        if (record is not null)
        {
            // A fresh per-device key, stored only as a hash. Issuing a new one invalidates any
            // earlier build for the same device, which is the right behaviour: re-provisioning
            // is how you replace a build you no longer trust.
            key = EnrollmentKey.Generate();
            var salt = EnrollmentKey.NewSalt();
            registry.SetDeviceKey(record.Info.Id, EnrollmentKey.Hash(key, salt), salt);
        }
        else
        {
            key = config.EnrollmentKey;
        }

        var embedded = new EmbeddedClientConfig
        {
            ServerHost = config.PublicHost.Trim(),
            ControlPort = config.ControlPort,
            ServerCertificateFingerprint = certificateFingerprint,
            EnrollmentKey = key,
            KeyGeneration = config.KeyGeneration,
            ServerName = config.ServerName,
            DeviceId = record?.Info.Id,
            SuggestedDeviceName = record?.Info.Name,
            ProvisionedBy = note,
        };

        await ClientConfigBlob.WriteAsync(stubPath, outputPath, embedded, cancellationToken).ConfigureAwait(false);

        return new ProvisionedClient(
            outputPath,
            record?.Info.Name,
            record is not null,
            EnrollmentKey.Fingerprint(key),
            new FileInfo(outputPath).Length);
    }

    /// <summary>Suggests a filename that says what the build is without opening it.</summary>
    public static string SuggestFileName(string? deviceName)
    {
        var suffix = string.IsNullOrWhiteSpace(deviceName)
            ? "any-device"
            : Core.Models.TunnelNameGenerator.Sanitise(deviceName);

        return $"CaYaTunnelClient-{(string.IsNullOrEmpty(suffix) ? "any-device" : suffix)}.exe";
    }
}

public sealed record ProvisionedClient(
    string Path,
    string? DeviceName,
    bool PerDeviceKey,
    string KeyFingerprint,
    long SizeBytes);
