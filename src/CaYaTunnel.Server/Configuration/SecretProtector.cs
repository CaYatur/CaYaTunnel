using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CaYaTunnel.Server.Configuration;

/// <summary>
/// Encrypts the two secrets that must not sit in plain text on the VPS: the enrollment key and
/// the Cloudflare API token. Uses DPAPI at machine scope so the value is bound to this machine
/// and readable whether the gateway runs as a desktop app or as a Windows service.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "enc:v1:";

    /// <summary>
    /// True when secrets can actually be encrypted. On a non-Windows host DPAPI is unavailable
    /// and values are stored as-is — the caller surfaces that so it is a known trade-off rather
    /// than a silent downgrade.
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plaintext ?? string.Empty;
        }

        if (!IsSupported)
        {
            return plaintext;
        }

        return Prefix + Convert.ToBase64String(ProtectWindows(Encoding.UTF8.GetBytes(plaintext)));
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored; // written before encryption was available, or on another platform
        }

        if (!IsSupported)
        {
            return string.Empty;
        }

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(UnprotectWindows(cipher));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Config copied from a different machine: the secret cannot be recovered, and the
            // operator has to re-enter it. Better than crashing on startup.
            return string.Empty;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data)
        => ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.LocalMachine);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data)
        => ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.LocalMachine);
}
