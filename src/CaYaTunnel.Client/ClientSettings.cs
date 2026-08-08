using System.Text.Json;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Provisioning;

namespace CaYaTunnel.Client;

/// <summary>
/// Everything the client remembers between runs. Kept small on purpose: a provisioned build
/// already knows where its server is, so this is mostly identity and user preferences.
/// </summary>
public sealed class ClientSettings
{
    /// <summary>Assigned by the server on first connect and reused forever after.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Defaults to the machine name; the user can override it.</summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Connection details for a client that was not provisioned by a server — entered by hand.
    /// A provisioned build ignores these in favour of its embedded config.
    /// </summary>
    public string? ManualServerHost { get; set; }

    public int ManualControlPort { get; set; }

    public string? ManualEnrollmentKey { get; set; }

    public string? ManualCertificateFingerprint { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>Start hidden in the tray — the usual choice when launching at sign-in.</summary>
    public bool StartMinimised { get; set; }

    /// <summary>Register the startup entry so it runs elevated via Task Scheduler.</summary>
    public bool StartElevated { get; set; }

    public bool ConnectOnLaunch { get; set; } = true;

    /// <summary>Minimise to the tray instead of exiting when the window is closed.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    /// <summary>Last window size, so the app reopens the way the user left it.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }
}

/// <summary>
/// Loads and saves <see cref="ClientSettings"/>.
/// <para>
/// Portable first: if the folder holding the exe is writable, settings live beside it so the
/// whole thing can be carried on a stick and cleaned up by deleting one folder. When it is not
/// writable — Program Files, a read-only share — it falls back to the user's AppData rather
/// than failing.
/// </para>
/// </summary>
public sealed class ClientSettingsStore
{
    private const string FileName = "cayatunnel.settings.json";

    private readonly Lock _gate = new();

    public ClientSettingsStore(string? explicitPath = null)
    {
        Path = explicitPath ?? ResolvePath();
        IsPortable = !Path.StartsWith(AppDataRoot, StringComparison.OrdinalIgnoreCase);
    }

    public string Path { get; }

    /// <summary>True when settings sit next to the executable rather than in AppData.</summary>
    public bool IsPortable { get; }

    private static string AppDataRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CaYaTunnel");

    public ClientSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return new ClientSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(Path), JsonProtocol.Options)
                    ?? new ClientSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Corrupt settings must not stop the app from starting; defaults are fine and
                // the next save overwrites them.
                return new ClientSettings();
            }
        }
    }

    public void Save(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonProtocol.PrettyOptions));
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing preferences is annoying; crashing is worse.
            }
        }
    }

    /// <summary>Deletes stored state — the "forget this device" button.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing useful to do.
            }
        }
    }

    private static string ResolvePath()
    {
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        return CanWriteTo(AppContext.BaseDirectory)
            ? beside
            : System.IO.Path.Combine(AppDataRoot, FileName);
    }

    private static bool CanWriteTo(string directory)
    {
        try
        {
            var probe = System.IO.Path.Combine(directory, $".cayatunnel-{Guid.NewGuid():n}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Where the client gets its server details: an embedded blob from provisioning, or values the
/// user typed in.
/// </summary>
public sealed record ClientConnectionProfile(
    string ServerHost,
    int ControlPort,
    string EnrollmentKey,
    string? CertificateFingerprint,
    string ServerName,
    bool Provisioned)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(ServerHost)
        && ControlPort is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(EnrollmentKey);

    /// <summary>
    /// Prefers the provisioned blob and falls back to manual settings, so a build handed out by
    /// the server needs no setup at all while a bare stub can still be pointed at one by hand.
    /// </summary>
    public static ClientConnectionProfile Resolve(ClientSettings settings, EmbeddedClientConfig? embedded)
    {
        if (embedded is { IsUsable: true })
        {
            return new ClientConnectionProfile(
                embedded.ServerHost,
                embedded.ControlPort,
                embedded.EnrollmentKey,
                embedded.ServerCertificateFingerprint,
                embedded.ServerName,
                Provisioned: true);
        }

        return new ClientConnectionProfile(
            settings.ManualServerHost ?? "",
            settings.ManualControlPort,
            settings.ManualEnrollmentKey ?? "",
            settings.ManualCertificateFingerprint,
            "CaYaTunnel",
            Provisioned: false);
    }
}
