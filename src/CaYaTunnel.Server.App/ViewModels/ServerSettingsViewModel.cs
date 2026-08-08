using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Gateway;
using CaYaTunnel.Ui;
using Microsoft.Win32;

namespace CaYaTunnel.Server.App.ViewModels;

/// <summary>
/// The gateway's settings. Edits are held here and applied together, because most of them
/// restart listeners and applying each keystroke would drop live tunnels repeatedly.
/// </summary>
public sealed class ServerSettingsViewModel : ViewModelBase
{
    private const string StartupAppName = "CaYaTunnel Server";
    private const string StartupTaskName = "CaYaTunnel Server Startup";

    private readonly ServerShellViewModel _shell;
    private readonly ServerConfigStore _store;
    private readonly TunnelServer _server;

    private ServerConfig _draft;
    private string? _message;
    private bool _messageIsError;
    private string? _dnsStatus;
    private bool _dnsOk;

    public ServerSettingsViewModel(ServerShellViewModel shell, ServerConfigStore store, TunnelServer server)
    {
        _shell = shell;
        _store = store;
        _server = server;
        _draft = Clone(shell.Config);

        StartupState = StartupManager.GetState(StartupAppName, StartupTaskName);

        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => SetMessage(ex.Message, true));
        TestDnsCommand = new AsyncRelayCommand(TestDnsAsync, onError: ex => SetDnsStatus(ex.Message, false));
        InstallServiceCommand = new RelayCommand(InstallService);
        UninstallServiceCommand = new RelayCommand(UninstallService);
        RelaunchElevatedCommand = new RelayCommand(() => StartupManager.RelaunchElevated());

        ApplyFirewallCommand = new RelayCommand(ApplyFirewall, () => FirewallManager.CanManage);
        RemoveFirewallCommand = new RelayCommand(RemoveFirewall, () => FirewallManager.CanManage);
        ImportPublicTlsCommand = new RelayCommand(ImportPublicTlsCertificate);
        ImportPublicTlsPemCommand = new RelayCommand(ImportPublicTlsPemCertificate);
        ClearPublicTlsCommand = new RelayCommand(ClearPublicTlsCertificate, () => !string.IsNullOrWhiteSpace(_draft.PublicTlsCertificatePath));
    }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand TestDnsCommand { get; }

    public RelayCommand InstallServiceCommand { get; }

    public RelayCommand UninstallServiceCommand { get; }

    public RelayCommand RelaunchElevatedCommand { get; }

    public RelayCommand ApplyFirewallCommand { get; }

    public RelayCommand RemoveFirewallCommand { get; }

    public RelayCommand ImportPublicTlsCommand { get; }

    public RelayCommand ImportPublicTlsPemCommand { get; }

    public RelayCommand ClearPublicTlsCommand { get; }

    // ---- Firewall ------------------------------------------------------------------

    public bool CanManageFirewall => FirewallManager.CanManage;

    public bool FirewallRulesExist => FirewallManager.RulesExist();

    /// <summary>
    /// Exactly what would be opened, listed before anything changes. Approving an invisible set
    /// of firewall changes is not a decision anyone can actually make.
    /// </summary>
    public string FirewallPlan => string.Join("\n", FirewallManager.Plan(_draft)
        .Select(rule => $"  •  {rule.Protocol} {rule.Ports}  —  {rule.Name}"));

    private void ApplyFirewall()
    {
        var (ok, message) = FirewallManager.Apply(_draft);
        SetMessage(message, !ok);
        Raise(nameof(FirewallRulesExist));
    }

    private void RemoveFirewall()
    {
        var (ok, message) = FirewallManager.Remove();
        SetMessage(message, !ok);
        Raise(nameof(FirewallRulesExist));
    }

    // ---- Identity and public addresses ---------------------------------------

    public string ServerName
    {
        get => _draft.ServerName;
        set { _draft.ServerName = value; Raise(); }
    }

    public string PublicHost
    {
        get => _draft.PublicHost;
        set { _draft.PublicHost = value; Raise(); }
    }

    public string BaseDomain
    {
        get => _draft.BaseDomain;
        set { _draft.BaseDomain = value; Raise(); }
    }

    public string ControlPort
    {
        get => _draft.ControlPort.ToString();
        set { if (int.TryParse(value, out var port)) { _draft.ControlPort = port; } Raise(); }
    }

    // ---- Listeners --------------------------------------------------------------

    public bool SinglePortMode
    {
        get => _draft.SinglePortMode;
        set
        {
            _draft.SinglePortMode = value;
            Raise();
            Raise(nameof(ShowsIndividualListeners));
            Raise(nameof(FirewallPlan));
        }
    }

    /// <summary>The per-protocol listener settings only mean something when ports are separate.</summary>
    public bool ShowsIndividualListeners => !_draft.SinglePortMode;

    public bool EnableStandardHttpsPort
    {
        get => _draft.EnableStandardHttpsPort;
        set
        {
            _draft.EnableStandardHttpsPort = value;
            Raise();
            Raise(nameof(FirewallPlan));
        }
    }

    public bool AutomaticTlsEnabled
    {
        get => _draft.AutomaticTlsEnabled;
        set { _draft.AutomaticTlsEnabled = value; Raise(); }
    }

    public string AutomaticTlsEmail
    {
        get => _draft.AutomaticTlsEmail;
        set { _draft.AutomaticTlsEmail = value; Raise(); }
    }

    public bool EnableHttpRouter
    {
        get => _draft.EnableHttpRouter;
        set { _draft.EnableHttpRouter = value; Raise(); }
    }

    public string HttpPort
    {
        get => _draft.HttpPort.ToString();
        set { if (int.TryParse(value, out var port)) { _draft.HttpPort = port; } Raise(); }
    }

    public string HttpsPort
    {
        get => _draft.HttpsPort.ToString();
        set { if (int.TryParse(value, out var port)) { _draft.HttpsPort = port; } Raise(); }
    }

    public bool EnableMinecraftRouter
    {
        get => _draft.EnableMinecraftRouter;
        set { _draft.EnableMinecraftRouter = value; Raise(); }
    }

    public string MinecraftPort
    {
        get => _draft.MinecraftPort.ToString();
        set { if (int.TryParse(value, out var port)) { _draft.MinecraftPort = port; } Raise(); }
    }

    public string PortRangeStart
    {
        get => _draft.TcpPortRangeStart.ToString();
        set { if (int.TryParse(value, out var port)) { _draft.TcpPortRangeStart = port; } Raise(); }
    }

    public string PortRangeEnd
    {
        get => _draft.TcpPortRangeEnd.ToString();
        set { if (int.TryParse(value, out var port)) { _draft.TcpPortRangeEnd = port; } Raise(); }
    }

    // ---- Public HTTPS certificate -------------------------------------------------

    public string PublicTlsCertificatePassword
    {
        get => _draft.PublicTlsCertificatePassword;
        set
        {
            _draft.PublicTlsCertificatePassword = value;
            Raise();
            Raise(nameof(PublicTlsCertificateSummary));
        }
    }

    public bool HasImportedPublicTlsCertificate => !string.IsNullOrWhiteSpace(_draft.PublicTlsCertificatePath);

    public string PublicTlsCertificateSummary
    {
        get
        {
            if (!HasImportedPublicTlsCertificate)
            {
                return Loc.Get("PublicTlsAutomaticCertificate");
            }

            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    _draft.PublicTlsCertificatePath,
                    string.IsNullOrEmpty(_draft.PublicTlsCertificatePassword) ? null : _draft.PublicTlsCertificatePassword,
                    X509KeyStorageFlags.EphemeralKeySet);

                return Loc.Format("PublicTlsCertificateLoaded", certificate.Subject, certificate.NotAfter.ToShortDateString());
            }
            catch
            {
                return Path.GetFileName(_draft.PublicTlsCertificatePath);
            }
        }
    }

    private void ImportPublicTlsCertificate()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("ImportPublicTlsCertificate"),
            Filter = "PKCS#12 certificate (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                dialog.FileName,
                string.IsNullOrEmpty(_draft.PublicTlsCertificatePassword) ? null : _draft.PublicTlsCertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);

            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException(Loc.Get("PublicTlsPrivateKeyRequired"));
            }

            Directory.CreateDirectory(ServerPaths.DataDirectory);
            var destination = ServerPaths.ImportedPublicCertificateFile;
            if (!Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(dialog.FileName, destination, overwrite: true);
            }

            _draft.PublicTlsCertificatePath = destination;
            Raise(nameof(HasImportedPublicTlsCertificate));
            Raise(nameof(PublicTlsCertificateSummary));
            ClearPublicTlsCommand.RaiseCanExecuteChanged();
            SetMessage(Loc.Get("PublicTlsImportSuccess"), false);
        }
        catch (Exception ex)
        {
            SetMessage(Loc.Format("PublicTlsImportFailed", ex.Message), true);
        }
    }

    private void ImportPublicTlsPemCertificate()
    {
        var certificateDialog = new OpenFileDialog
        {
            Title = Loc.Get("ImportPublicTlsPemCertificate"),
            Filter = "PEM certificate (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (certificateDialog.ShowDialog() != true)
        {
            return;
        }

        var keyDialog = new OpenFileDialog
        {
            Title = Loc.Get("ImportPublicTlsPrivateKey"),
            Filter = "PEM private key (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (keyDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            using var certificate = X509Certificate2.CreateFromPemFile(certificateDialog.FileName, keyDialog.FileName);
            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException(Loc.Get("PublicTlsPrivateKeyRequired"));
            }

            Directory.CreateDirectory(ServerPaths.DataDirectory);
            var destination = ServerPaths.ImportedPublicCertificateFile;
            var exportPassword = string.IsNullOrEmpty(_draft.PublicTlsCertificatePassword)
                ? Convert.ToHexString(Guid.NewGuid().ToByteArray())
                : _draft.PublicTlsCertificatePassword;

            var pfxBytes = certificate.Export(X509ContentType.Pfx, exportPassword);
            File.WriteAllBytes(destination, pfxBytes);

            _draft.PublicTlsCertificatePath = destination;
            _draft.PublicTlsCertificatePassword = exportPassword;
            Raise(nameof(PublicTlsCertificatePassword));
            Raise(nameof(HasImportedPublicTlsCertificate));
            Raise(nameof(PublicTlsCertificateSummary));
            ClearPublicTlsCommand.RaiseCanExecuteChanged();
            SetMessage(Loc.Get("PublicTlsPemImportSuccess"), false);
        }
        catch (Exception ex)
        {
            SetMessage(Loc.Format("PublicTlsImportFailed", ex.Message), true);
        }
    }

    private void ClearPublicTlsCertificate()
    {
        _draft.PublicTlsCertificatePath = "";
        _draft.PublicTlsCertificatePassword = "";
        Raise(nameof(PublicTlsCertificatePassword));
        Raise(nameof(HasImportedPublicTlsCertificate));
        Raise(nameof(PublicTlsCertificateSummary));
        ClearPublicTlsCommand.RaiseCanExecuteChanged();
        SetMessage(Loc.Get("PublicTlsCleared"), false);
    }

    // ---- DNS ---------------------------------------------------------------------

    /// <summary>0 = manual, 1 = Cloudflare.</summary>
    public int DnsProviderIndex
    {
        get => _draft.Dns.Provider == DnsProviderKind.Cloudflare ? 1 : 0;
        set
        {
            _draft.Dns.Provider = value == 1 ? DnsProviderKind.Cloudflare : DnsProviderKind.None;
            Raise();
            Raise(nameof(IsCloudflare));
        }
    }

    public bool IsCloudflare => _draft.Dns.Provider == DnsProviderKind.Cloudflare;

    public string CloudflareToken
    {
        get => _draft.Dns.CloudflareApiToken;
        set { _draft.Dns.CloudflareApiToken = value; Raise(); }
    }

    public string CloudflareZoneId
    {
        get => _draft.Dns.CloudflareZoneId;
        set { _draft.Dns.CloudflareZoneId = value; Raise(); }
    }

    public bool ProxyRecords
    {
        get => _draft.Dns.ProxyRecords;
        set { _draft.Dns.ProxyRecords = value; Raise(); }
    }

    public string? DnsStatus
    {
        get => _dnsStatus;
        private set => Set(ref _dnsStatus, value);
    }

    public bool DnsOk
    {
        get => _dnsOk;
        private set => Set(ref _dnsOk, value);
    }

    // ---- Security ------------------------------------------------------------------

    public bool RequireApproval
    {
        get => _draft.RequireManualApproval;
        set { _draft.RequireManualApproval = value; Raise(); }
    }

    public bool AutoStartGateway
    {
        get => _draft.AutoStartGateway;
        set { _draft.AutoStartGateway = value; Raise(); }
    }

    // ---- Startup and service -----------------------------------------------------------

    public StartupState StartupState { get; private set; }

    public bool StartWithWindows
    {
        get => StartupState != StartupState.Disabled;
        set => ApplyStartup(enabled: value, elevated: StartElevated);
    }

    public bool StartElevated
    {
        get => StartupState == StartupState.ElevatedTask;
        set => ApplyStartup(enabled: StartWithWindows || value, elevated: value);
    }

    public bool IsElevated => StartupManager.IsElevated;

    public bool ServiceInstalled => WindowsServiceManager.IsInstalled;

    public string ServiceStatusLabel => WindowsServiceManager.IsInstalled
        ? WindowsServiceManager.IsRunning ? "installed, running" : "installed, stopped"
        : "not installed";

    public int LanguageIndex
    {
        get => (int)Loc.Current.Language;
        set
        {
            Loc.Current.Language = (AppLanguage)Math.Clamp(value, 0, 2);
            Raise();
        }
    }

    private void ApplyStartup(bool enabled, bool elevated)
    {
        StartupManager.SetRunKey(StartupAppName, enabled: false, startHidden: false);

        if (enabled && elevated)
        {
            var (ok, message) = StartupManager.SetElevatedTask(StartupTaskName, enabled: true, startHidden: false);
            SetMessage(message, !ok);
        }
        else
        {
            StartupManager.SetElevatedTask(StartupTaskName, enabled: false, false);

            if (enabled && !StartupManager.SetRunKey(StartupAppName, enabled: true, startHidden: false))
            {
                SetMessage("Windows would not accept the startup entry.", true);
            }
        }

        StartupState = StartupManager.GetState(StartupAppName, StartupTaskName);
        Raise(nameof(StartupState));
        Raise(nameof(StartWithWindows));
        Raise(nameof(StartElevated));
    }

    private void InstallService()
    {
        var (ok, message) = WindowsServiceManager.Install(StartupManager.CurrentExecutablePath);
        SetMessage(message, !ok);

        if (ok)
        {
            WindowsServiceManager.Start();
        }

        Raise(nameof(ServiceInstalled));
        Raise(nameof(ServiceStatusLabel));
    }

    private void UninstallService()
    {
        var (ok, message) = WindowsServiceManager.Uninstall();
        SetMessage(message, !ok);

        Raise(nameof(ServiceInstalled));
        Raise(nameof(ServiceStatusLabel));
    }

    // ---- Apply ---------------------------------------------------------------------------

    private async Task SaveAsync()
    {
        var problems = _draft.Validate();
        if (problems.Count > 0)
        {
            SetMessage(string.Join("  ", problems), true);
            return;
        }

        // The key is not editable here, so it is carried across rather than taken from the draft.
        _draft.EnrollmentKey = _shell.Config.EnrollmentKey;
        _draft.KeyGeneration = _shell.Config.KeyGeneration;
        _draft.RetiredKeys = _shell.Config.RetiredKeys;

        await _shell.ApplyConfigAsync(_draft);
        _draft = Clone(_shell.Config);

        SetMessage(Loc.Get(_server.IsRunning ? "RestartRequired" : "SettingsSaved"), false);
    }

    private async Task TestDnsAsync()
    {
        if (!IsCloudflare)
        {
            SetDnsStatus(Loc.Get("DnsManualHint"), true);
            return;
        }

        using var provider = new Server.Dns.CloudflareDnsProvider(
            _draft.Dns.CloudflareApiToken,
            _draft.Dns.CloudflareZoneId,
            _draft.Dns.ProxyRecords,
            _draft.Dns.RecordTtl);

        var status = await provider.TestAsync();
        SetDnsStatus(status.Message, status.Ok);
    }

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    private void SetMessage(string? message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }

    private void SetDnsStatus(string? message, bool ok)
    {
        DnsOk = ok;
        DnsStatus = message;
    }

    /// <summary>
    /// A deep copy so editing the form cannot mutate the running configuration before Save.
    /// Round-tripping through JSON is not elegant, but it is exactly the shape already persisted
    /// and keeps this from silently missing a field added later.
    /// </summary>
    private static ServerConfig Clone(ServerConfig config)
        => JsonSerializer.Deserialize<ServerConfig>(
            JsonSerializer.Serialize(config, JsonProtocol.Options), JsonProtocol.Options)!;
}
