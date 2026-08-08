using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CaYaTunnel.Server.Gateway;

/// <summary>
/// Supplies the TLS certificates the gateway presents.
/// <para>
/// A self-signed certificate is the default and is not a shortcut: provisioned clients pin the
/// server's certificate fingerprint rather than trusting a CA, which is stricter than public CA
/// validation and needs no certificate authority, no renewal job and no domain. An operator who
/// wants a CA-issued certificate can still point the config at a PFX.
/// </para>
/// </summary>
public static class CertificateManager
{
    private const int ValidityYears = 10;

    /// <summary>
    /// Schannel cannot perform a TLS handshake with an ephemeral key, and a certificate built by
    /// <see cref="CertificateRequest.CreateSelfSigned"/> has exactly that. Every certificate here
    /// is therefore round-tripped through PKCS#12 with these flags to get a persisted key.
    /// User scope rather than machine scope so the gateway works unelevated as a desktop app and
    /// as a service, without needing write access to the machine key store.
    /// </summary>
    private const X509KeyStorageFlags StorageFlags =
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet;

    /// <summary>
    /// Loads the certificate at <paramref name="path"/>, generating and persisting a self-signed
    /// one if it is missing.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string path, string password, string subjectName)
    {
        if (File.Exists(path))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(
                    path,
                    string.IsNullOrEmpty(password) ? null : password,
                    StorageFlags);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"Could not open the certificate at '{path}'. If it has a password, set it in the server settings. ({ex.Message})",
                    ex);
            }
        }

        var created = CreateSelfSigned(subjectName);
        Save(created, path, password);
        return created;
    }

    public static X509Certificate2 CreateSelfSigned(string subjectName)
    {
        var safeSubject = string.IsNullOrWhiteSpace(subjectName) ? "cayatunnel" : subjectName.Trim();

        using var key = RSA.Create(3072);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={safeSubject}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication

        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(safeSubject, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        else
        {
            sanBuilder.AddDnsName(safeSubject);
        }

        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(ValidityYears));

        // Round-trip so the private key is persisted rather than ephemeral; see StorageFlags.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null, StorageFlags);
    }

    public static void Save(X509Certificate2 certificate, string path, string password)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = string.IsNullOrEmpty(password)
            ? certificate.Export(X509ContentType.Pkcs12)
            : certificate.Export(X509ContentType.Pkcs12, password);

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// SHA-256 of the DER encoding, hex encoded — the value clients pin. Matches what
    /// <c>openssl x509 -fingerprint -sha256</c> prints, minus the colons.
    /// </summary>
    public static string Fingerprint(X509Certificate2 certificate)
        => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

    /// <summary>Human-readable grouping for display, e.g. "ab:cd:ef:...".</summary>
    public static string FormatFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return string.Empty;
        }

        var pairs = new List<string>(fingerprint.Length / 2);
        for (var i = 0; i + 1 < fingerprint.Length; i += 2)
        {
            pairs.Add(fingerprint.Substring(i, 2));
        }

        return string.Join(':', pairs);
    }
}
