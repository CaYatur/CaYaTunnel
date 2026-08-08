namespace CaYaTunnel.Server.Configuration;

/// <summary>
/// Where the server keeps its state. ProgramData rather than the install folder, so the same
/// paths work whether the gateway runs as a desktop app or as a Windows service under a
/// different account. Override with the CAYATUNNEL_DATA environment variable to run several
/// deployments side by side or to keep state on another volume.
/// </summary>
public static class ServerPaths
{
    public const string DataDirectoryVariable = "CAYATUNNEL_DATA";

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string RegistryFile => Path.Combine(DataDirectory, "registry.json");

    public static string ControlCertificateFile => Path.Combine(DataDirectory, "control-tls.pfx");

    public static string PublicCertificateFile => Path.Combine(DataDirectory, "public-tls.pfx");

    /// <summary>
    /// Operator-imported public HTTPS certificate. Kept separate from the generated certificate
    /// so switching back to automatic TLS never destroys the imported file and importing a bad
    /// certificate cannot overwrite the known-good fallback.
    /// </summary>
    public static string ImportedPublicCertificateFile => Path.Combine(DataDirectory, "public-tls-imported.pfx");

    /// <summary>Prebuilt client executable that provisioning copies and appends config to.</summary>
    public static string ClientStubFile => Path.Combine(DataDirectory, "stub", "CaYaTunnel.Client.exe");

    /// <summary>Provisioned per-device builds. Excluded from source control — they carry keys.</summary>
    public static string ProvisionedDirectory => Path.Combine(DataDirectory, "provisioned");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(ClientStubFile)!);
        Directory.CreateDirectory(ProvisionedDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "CaYaTunnel", "Server");
    }
}
