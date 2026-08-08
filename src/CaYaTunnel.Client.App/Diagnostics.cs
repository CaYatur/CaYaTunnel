using System.IO;
using System.Text.Json;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Provisioning;
using CaYaTunnel.Core.Security;

namespace CaYaTunnel.Client.App;

/// <summary>
/// Command-line switches handled before any window opens. These exist so the provisioning
/// pipeline can be verified against a real published single-file build — the tail-append trick
/// depends on publish settings, and only the actual binary can prove it works.
/// </summary>
public static class Diagnostics
{
    public const string DumpConfigSwitch = "--dump-config";

    /// <summary>
    /// Handles a diagnostic switch. Returns true when the app should exit without showing UI.
    /// </summary>
    public static bool TryHandle(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], DumpConfigSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = i + 1 < args.Length ? args[i + 1] : "cayatunnel-config.json";
            DumpEmbeddedConfig(path);
            return true;
        }

        return false;
    }

    private static void DumpEmbeddedConfig(string path)
    {
        var config = ClientConfigBlob.ReadFromCurrentProcess();

        // The enrollment key is deliberately reduced to a fingerprint. It is enough to prove the
        // blob round-tripped intact, and it means this switch can never spill a live credential
        // into a file someone later shares in a bug report.
        var report = new
        {
            Provisioned = config is not null,
            ExecutablePath = Environment.ProcessPath,
            config?.ServerHost,
            config?.ControlPort,
            config?.ServerName,
            config?.KeyGeneration,
            config?.ServerCertificateFingerprint,
            EnrollmentKeyFingerprint = config is null ? null : EnrollmentKey.Fingerprint(config.EnrollmentKey),
            config?.ProvisionedAt,
            config?.ProvisionedBy,
        };

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonProtocol.PrettyOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to report to — there is no console attached to a WPF process.
        }
    }
}
