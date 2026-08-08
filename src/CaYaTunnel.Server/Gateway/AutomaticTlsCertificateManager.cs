using System.Security.Cryptography.X509Certificates;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Dns;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;

namespace CaYaTunnel.Server.Gateway;

/// <summary>
/// Obtains and renews a browser-trusted wildcard certificate with Let's Encrypt DNS-01.
/// Validation is performed through Cloudflare DNS and therefore never needs ports 80 or 443.
/// </summary>
internal static class AutomaticTlsCertificateManager
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromMinutes(3);

    public static bool NeedsRenewal(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password: null,
                X509KeyStorageFlags.EphemeralKeySet);
            return certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.Add(RenewBefore);
        }
        catch
        {
            return true;
        }
    }

    public static async Task<bool> EnsureCertificateAsync(
        ServerConfig config,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!config.AutomaticTlsEnabled)
        {
            return false;
        }

        if (!force && !NeedsRenewal(ServerPaths.AutomaticPublicCertificateFile))
        {
            return false;
        }

        var baseDomain = config.BaseDomain.Trim().Trim('.');
        var wildcard = $"*.{baseDomain}";
        var challengeName = $"_acme-challenge.{baseDomain}";

        using var dns = new CloudflareDnsProvider(
            config.Dns.CloudflareApiToken,
            config.Dns.CloudflareZoneId,
            proxied: false,
            ttl: 60);

        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var acme = new AcmeContext(WellKnownServers.LetsEncryptV2, accountKey);
        await acme.NewAccount(config.AutomaticTlsEmail.Trim(), termsOfServiceAgreed: true)
            .ConfigureAwait(false);

        var order = await acme.NewOrder(new[] { wildcard }).ConfigureAwait(false);
        var authorizations = await order.Authorizations().ConfigureAwait(false);
        if (!authorizations.Any())
        {
            throw new InvalidOperationException("Let's Encrypt returned no DNS authorization for the wildcard certificate.");
        }

        foreach (var authorization in authorizations)
        {
            var challenge = await authorization.Dns().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Let's Encrypt did not offer a DNS-01 challenge.");
            var txtValue = acme.AccountKey.DnsTxt(challenge.Token);
            string? recordId = null;

            try
            {
                recordId = await dns.CreateTxtRecordAsync(challengeName, txtValue, cancellationToken)
                    .ConfigureAwait(false);

                // Cloudflare's authoritative DNS is usually quick, but giving it a short settling
                // window avoids asking Let's Encrypt before the TXT record reaches all nameservers.
                await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                await challenge.Validate().ConfigureAwait(false);

                var deadline = DateTimeOffset.UtcNow + ValidationTimeout;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resource = await authorization.Resource().ConfigureAwait(false);
                    if (resource.Status == AuthorizationStatus.Valid)
                    {
                        break;
                    }

                    if (resource.Status is AuthorizationStatus.Invalid
                        or AuthorizationStatus.Revoked
                        or AuthorizationStatus.Deactivated
                        or AuthorizationStatus.Expired)
                    {
                        throw new InvalidOperationException($"Let's Encrypt DNS validation failed ({resource.Status}).");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }

                var final = await authorization.Resource().ConfigureAwait(false);
                if (final.Status != AuthorizationStatus.Valid)
                {
                    throw new TimeoutException("Timed out waiting for Let's Encrypt DNS validation.");
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(recordId))
                {
                    try
                    {
                        await dns.RemoveRecordAsync(challengeName, recordId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // A stale _acme-challenge TXT record is harmless and can be cleaned on the
                        // next attempt. Never discard an issued certificate because cleanup failed.
                    }
                }
            }
        }

        var certificateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var chain = await order.Generate(
                new CsrInfo { CommonName = wildcard },
                certificateKey,
                retryCount: 5)
            .ConfigureAwait(false);
        var pfx = chain.ToPfx(certificateKey).Build("CaYaTunnel automatic HTTPS", "");

        System.IO.Directory.CreateDirectory(ServerPaths.DataDirectory);
        var tempPath = ServerPaths.AutomaticPublicCertificateFile + ".new";
        await File.WriteAllBytesAsync(tempPath, pfx, cancellationToken).ConfigureAwait(false);

        // Load before replacing the live file, so a malformed response can never destroy the
        // currently working certificate.
        using (var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                   tempPath,
                   "",
                   X509KeyStorageFlags.EphemeralKeySet))
        {
            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException("The ACME certificate does not contain its private key.");
            }
        }

        File.Move(tempPath, ServerPaths.AutomaticPublicCertificateFile, overwrite: true);
        return true;
    }
}
