using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
    private static readonly TimeSpan DnsPropagationTimeout = TimeSpan.FromMinutes(2);
    private static readonly HttpClient PublicDnsHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

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

        var accountKey = LoadOrCreateAccountKey();
        var acme = new AcmeContext(WellKnownServers.LetsEncryptV2, accountKey);

        // With a key it has seen before, ACME returns the existing account rather than making
        // another one, so this is safe to call on every renewal.
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

                // Do not acknowledge the ACME challenge until the exact TXT value is visible from
                // a public recursive resolver. A fixed delay is unreliable and can turn an otherwise
                // valid DNS-01 challenge permanently Invalid before Cloudflare propagation finishes.
                await WaitForTxtPropagationAsync(challengeName, txtValue, cancellationToken)
                    .ConfigureAwait(false);

                var challengeResult = await challenge.Validate().ConfigureAwait(false);
                if (challengeResult.Status == ChallengeStatus.Invalid)
                {
                    var detail = challengeResult.Error?.Detail;
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(detail)
                            ? "Let's Encrypt rejected the DNS-01 challenge."
                            : $"Let's Encrypt rejected the DNS-01 challenge: {detail}");
                }

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

    /// <summary>
    /// The ACME account key, kept between runs.
    /// <para>
    /// Generating a fresh key would register a brand new Let's Encrypt account on every
    /// certificate request, and Let's Encrypt caps new accounts per IP address — a gateway that
    /// restarts a few times, or simply renews often enough, would start being refused. Reusing
    /// one key also keeps the account's issuance history intact, which is what its rate limits
    /// are measured against.
    /// </para>
    /// <para>
    /// The key is an account credential, so it is encrypted at rest the same way the enrollment
    /// key and the Cloudflare token are.
    /// </para>
    /// </summary>
    internal static IKey LoadOrCreateAccountKey(string? keyPath = null)
    {
        var path = keyPath ?? ServerPaths.AcmeAccountKeyFile;

        if (File.Exists(path))
        {
            try
            {
                var stored = SecretProtector.Unprotect(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    return KeyFactory.FromPem(stored);
                }
            }
            catch (Exception)
            {
                // Deliberately broad. This file can be unreadable, truncated, copied from another
                // machine so DPAPI cannot recover it, or written by a future version — and the
                // PEM parser reports each of those differently, including NotSupportedException.
                // Every one of them has the same right answer: register a new account. Narrowing
                // this would turn a recoverable situation into a gateway that cannot get a
                // certificate at all.
            }
        }

        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, SecretProtector.Protect(key.ToPem()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The certificate can still be issued; only the next renewal pays for it by
            // registering again.
        }

        return key;
    }

    private static async Task WaitForTxtPropagationAsync(
        string hostname,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DnsPropagationTimeout;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(hostname)}&type=TXT");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

                using var response = await PublicDnsHttp.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (document.RootElement.TryGetProperty("Answer", out var answers)
                    && answers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var answer in answers.EnumerateArray())
                    {
                        if (!answer.TryGetProperty("data", out var dataElement))
                        {
                            continue;
                        }

                        var data = dataElement.GetString()?.Trim();
                        if (string.IsNullOrEmpty(data))
                        {
                            continue;
                        }

                        // DNS JSON represents TXT RDATA with surrounding quotes.
                        data = data.Trim('"').Replace("\\\"", "\"");
                        if (string.Equals(data, expectedValue, StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"DNS-01 TXT record '{hostname}' did not become publicly visible within {DnsPropagationTimeout.TotalSeconds:0} seconds."
            + (lastError is null ? "" : $" Last DNS check error: {lastError.Message}"));
    }
}
