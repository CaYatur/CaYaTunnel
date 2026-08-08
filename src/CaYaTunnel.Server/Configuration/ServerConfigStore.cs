using System.Text.Json;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Security;

namespace CaYaTunnel.Server.Configuration;

/// <summary>
/// Loads and saves <see cref="ServerConfig"/>, encrypting the secrets on the way to disk and
/// decrypting them on the way back. Callers always work with plaintext values in memory.
/// </summary>
public sealed class ServerConfigStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public ServerConfigStore(string? path = null)
    {
        _path = path ?? ServerPaths.ConfigFile;
    }

    public string Path => _path;

    /// <summary>
    /// Reads the config, creating a first-run default (with a freshly generated enrollment key)
    /// when none exists yet.
    /// </summary>
    public ServerConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                var fresh = CreateDefault();
                SaveCore(fresh);
                return fresh;
            }

            ServerConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(_path), JsonProtocol.Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Configuration at '{_path}' is not valid JSON: {ex.Message}", ex);
            }

            config ??= CreateDefault();

            config.EnrollmentKey = SecretProtector.Unprotect(config.EnrollmentKey);
            config.TlsCertificatePassword = SecretProtector.Unprotect(config.TlsCertificatePassword);
            config.PublicTlsCertificatePassword = SecretProtector.Unprotect(config.PublicTlsCertificatePassword);
            config.Dns.CloudflareApiToken = SecretProtector.Unprotect(config.Dns.CloudflareApiToken);

            if (string.IsNullOrWhiteSpace(config.EnrollmentKey))
            {
                // Either a first run that never got a key, or the config was copied from another
                // machine and DPAPI could not recover it. Either way, a new key is the only way
                // forward — and it correctly invalidates any client built against the old one.
                config.EnrollmentKey = Core.Security.EnrollmentKey.Generate();
                SaveCore(config);
            }

            return config;
        }
    }

    public void Save(ServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            SaveCore(config);
        }
    }

    private void SaveCore(ServerConfig config)
    {
        ServerPaths.EnsureCreated();

        // Serialise a copy so the caller's in-memory config keeps its plaintext secrets.
        var onDisk = JsonSerializer.Deserialize<ServerConfig>(
            JsonSerializer.Serialize(config, JsonProtocol.Options), JsonProtocol.Options)!;

        onDisk.EnrollmentKey = SecretProtector.Protect(config.EnrollmentKey);
        onDisk.TlsCertificatePassword = SecretProtector.Protect(config.TlsCertificatePassword);
        onDisk.PublicTlsCertificatePassword = SecretProtector.Protect(config.PublicTlsCertificatePassword);
        onDisk.Dns.CloudflareApiToken = SecretProtector.Protect(config.Dns.CloudflareApiToken);

        var json = JsonSerializer.Serialize(onDisk, JsonProtocol.PrettyOptions);

        // Write to a temp file and swap, so a crash mid-write cannot leave a truncated config
        // that locks the operator out of their own gateway.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private static ServerConfig CreateDefault() => new()
    {
        EnrollmentKey = Core.Security.EnrollmentKey.Generate(),
        KeyGeneration = 1,
    };

    /// <summary>
    /// Replaces the enrollment key and bumps the generation, retiring the old one. Every client
    /// carrying the previous key stops working the next time it reconnects.
    /// </summary>
    public void RotateEnrollmentKey(ServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(config.EnrollmentKey))
        {
            var salt = Core.Security.EnrollmentKey.NewSalt();
            config.RetiredKeys.Add(new RetiredKey
            {
                Generation = config.KeyGeneration,
                Salt = salt,
                Hash = Core.Security.EnrollmentKey.Hash(config.EnrollmentKey, salt),
            });

            // Keep the trail short; older entries only ever produced a nicer error message.
            const int keepRetired = 10;
            if (config.RetiredKeys.Count > keepRetired)
            {
                config.RetiredKeys.RemoveRange(0, config.RetiredKeys.Count - keepRetired);
            }
        }

        config.EnrollmentKey = Core.Security.EnrollmentKey.Generate();
        config.KeyGeneration++;
        Save(config);
    }
}
