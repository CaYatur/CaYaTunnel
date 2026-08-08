using System.Security.Cryptography.X509Certificates;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Gateway;
using Xunit;

namespace CaYaTunnel.Tests;

/// <summary>
/// Automatic HTTPS has two failure modes that look like success from the outside: an account
/// that is silently re-registered on every renewal until Let's Encrypt refuses, and a self-signed
/// stand-in sitting in the automatic certificate's file so the renewal check never fires again.
/// Both are cheap to assert and expensive to discover in production.
/// </summary>
public class AutomaticTlsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"cayatunnel-acme-{Guid.NewGuid():n}");

    public AutomaticTlsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void The_acme_account_key_is_reused_between_runs()
    {
        // A fresh key means a fresh Let's Encrypt account, and accounts per IP address are
        // capped — a gateway that restarts a few times would start being refused.
        var path = Path.Combine(_directory, "acme-account-key.pem");

        var first = AutomaticTlsCertificateManager.LoadOrCreateAccountKey(path);
        var second = AutomaticTlsCertificateManager.LoadOrCreateAccountKey(path);

        Assert.True(File.Exists(path));
        Assert.Equal(first.ToPem(), second.ToPem());
    }

    [Fact]
    public void The_stored_account_key_is_not_left_in_plain_text()
    {
        // It is an account credential and lives in a directory other users can read.
        var path = Path.Combine(_directory, "acme-account-key.pem");
        var key = AutomaticTlsCertificateManager.LoadOrCreateAccountKey(path);

        var onDisk = File.ReadAllText(path);

        Assert.DoesNotContain("PRIVATE KEY", onDisk, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(key.ToPem(), onDisk);
    }

    [Fact]
    public void An_unreadable_account_key_produces_a_new_one_rather_than_a_failure()
    {
        // Copying the data directory to another machine leaves a key this machine cannot decrypt.
        // Registering again is a working outcome; refusing to start is not.
        var path = Path.Combine(_directory, "acme-account-key.pem");
        File.WriteAllText(path, "not a key at all");

        var key = AutomaticTlsCertificateManager.LoadOrCreateAccountKey(path);

        Assert.NotNull(key);
        Assert.Contains("PRIVATE KEY", key.ToPem(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_certificate_is_never_generated_into_the_automatic_certificate_file()
    {
        // The trap: a self-signed stand-in written there is valid for ten years, so the renewal
        // check finds nothing due and automatic HTTPS never runs — while visitors keep seeing an
        // untrusted certificate.
        var config = new ServerConfig { AutomaticTlsEnabled = true, BaseDomain = "tunnel.example.com" };

        var (path, _, mayCreate) = CertificateManager.ChoosePublicCertificate(config, automaticCertificateExists: false);

        // Generating the fallback is right — it is where it gets written that matters.
        Assert.True(mayCreate);
        Assert.NotEqual(ServerPaths.AutomaticPublicCertificateFile, path);
        Assert.Equal(ServerPaths.PublicCertificateFile, path);
    }

    [Fact]
    public void The_automatic_certificate_is_used_once_it_exists_and_is_never_regenerated()
    {
        var config = new ServerConfig { AutomaticTlsEnabled = true, BaseDomain = "tunnel.example.com" };

        var (path, _, mayCreate) = CertificateManager.ChoosePublicCertificate(config, automaticCertificateExists: true);

        Assert.Equal(ServerPaths.AutomaticPublicCertificateFile, path);
        Assert.False(mayCreate);
    }

    [Fact]
    public void An_imported_certificate_wins_when_automatic_https_is_off()
    {
        var config = new ServerConfig
        {
            AutomaticTlsEnabled = false,
            PublicTlsCertificatePath = @"C:\certs\mine.pfx",
            PublicTlsCertificatePassword = "secret",
        };

        var (path, password, mayCreate) = CertificateManager.ChoosePublicCertificate(config, automaticCertificateExists: true);

        Assert.Equal(@"C:\certs\mine.pfx", path);
        Assert.Equal("secret", password);
        Assert.True(mayCreate);
    }

    [Fact]
    public void Load_refuses_a_missing_file_instead_of_inventing_one()
    {
        // LoadOrCreate exists for the fallback; the automatic path must fail loudly instead.
        var missing = Path.Combine(_directory, "nope.pfx");

        Assert.ThrowsAny<Exception>(() => CertificateManager.Load(missing, ""));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Renewal_is_due_when_there_is_no_certificate_yet()
    {
        Assert.True(AutomaticTlsCertificateManager.NeedsRenewal(Path.Combine(_directory, "absent.pfx")));
    }

    [Fact]
    public void Renewal_is_not_due_for_a_certificate_with_plenty_of_life_left()
    {
        var path = Path.Combine(_directory, "fresh.pfx");
        using var certificate = CertificateManager.CreateSelfSigned("test.example.com");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));

        Assert.False(AutomaticTlsCertificateManager.NeedsRenewal(path));
    }

    [Fact]
    public void Automatic_https_is_rejected_without_the_dns_provider_it_depends_on()
    {
        // DNS-01 validation is the whole mechanism; without Cloudflare there is nothing to write
        // the challenge record with, and finding that out at renewal time would be worse.
        var config = new ServerConfig
        {
            AutomaticTlsEnabled = true,
            AutomaticTlsEmail = "admin@example.com",
            BaseDomain = "tunnel.example.com",
            PublicHost = "203.0.113.10",
            EnrollmentKey = "key",
        };

        config.Dns.Provider = DnsProviderKind.None;

        Assert.Contains(config.Validate(), p => p.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Automatic_https_is_rejected_without_a_contact_address()
    {
        var config = new ServerConfig
        {
            AutomaticTlsEnabled = true,
            AutomaticTlsEmail = "not-an-address",
            BaseDomain = "tunnel.example.com",
            PublicHost = "203.0.113.10",
            EnrollmentKey = "key",
        };

        config.Dns.Provider = DnsProviderKind.Cloudflare;
        config.Dns.CloudflareApiToken = "token";
        config.Dns.CloudflareZoneId = "zone";

        Assert.Contains(config.Validate(), p => p.Contains("e-mail", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder.
        }
    }
}
