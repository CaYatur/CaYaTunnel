using System.Collections.ObjectModel;
using System.Windows;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Gateway;
using CaYaTunnel.Server.Registry;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Server.App.ViewModels;

public enum ServerPage
{
    Overview,
    Tunnels,
    Devices,
    Clients,
    Settings,
    Log,
}

/// <summary>
/// The admin app's state. Drives the same <see cref="TunnelServer"/> the Windows service runs,
/// so anything done here behaves identically headless.
/// </summary>
public sealed class ServerShellViewModel : ViewModelBase
{
    private readonly ServerConfigStore _configStore;
    private readonly TunnelRegistry _registry;
    private readonly GatewayLog _log;
    private readonly TunnelServer _server;
    private readonly bool _previewMode;

    private ServerPage _page = ServerPage.Overview;
    private string? _message;
    private bool _messageIsError;
    private bool _keyRevealed;

    public ServerShellViewModel(bool previewMode = false)
    {
        _previewMode = previewMode;

        _configStore = new ServerConfigStore();
        Config = _configStore.Load();

        _log = new GatewayLog(previewMode ? null : ServerPaths.LogDirectory);
        _registry = new TunnelRegistry();
        _server = new TunnelServer(Config, _registry, _log);

        _server.StateChanged += () => OnUiThread(Refresh);

        // A listener that could not bind is shown, not just logged: from the outside it looks
        // identical to a broken tunnel, and the cause is usually another service holding the port.
        _server.ListenerFailed += message => OnUiThread(() => SetMessage(message, true));
        _log.Entry += entry => OnUiThread(() => AppendLog(entry));

        Settings = new ServerSettingsViewModel(this, _configStore, _server);
        ClientBuilder = new ClientBuilderViewModel(this, _server, _registry);

        StartGatewayCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning, ReportError);
        StopGatewayCommand = new AsyncRelayCommand(StopAsync, () => IsRunning, ReportError);
        GoToCommand = new RelayCommand(p =>
        {
            if (p is string name && Enum.TryParse<ServerPage>(name, out var page))
            {
                Page = page;
            }
        });

        ToggleKeyCommand = new RelayCommand(() => KeyRevealed = !KeyRevealed);
        CopyKeyCommand = new RelayCommand(() => CopyToClipboard(Config.EnrollmentKey));
        CopyFingerprintCommand = new RelayCommand(() => CopyToClipboard(_server.ControlCertificateFingerprint));
        RotateKeyCommand = new RelayCommand(RotateKey);
        ClearLogCommand = new RelayCommand(() => LogEntries.Clear());

        DeleteTunnelCommand = new AsyncRelayCommand(DeleteTunnelAsync, p => p is ServerTunnelRow, ReportError);
        EditTunnelCommand = new RelayCommand(
            p => { if (p is ServerTunnelRow row) { EditTunnelRequested?.Invoke(row); } },
            p => p is ServerTunnelRow);
        ToggleTunnelCommand = new AsyncRelayCommand(ToggleTunnelAsync, p => p is ServerTunnelRow, ReportError);
        ApproveDeviceCommand = new RelayCommand(ApproveDevice, p => p is ServerDeviceRow);
        RevokeDeviceCommand = new AsyncRelayCommand(RevokeDeviceAsync, p => p is ServerDeviceRow, ReportError);
        RemoveDeviceCommand = new AsyncRelayCommand(RemoveDeviceAsync, p => p is ServerDeviceRow, ReportError);

        Refresh();
    }

    /// <summary>Builds a shell populated with sample data, for screenshots.</summary>
    public static ServerShellViewModel CreatePreview()
    {
        var shell = new ServerShellViewModel(previewMode: true);
        shell.LoadPreview();
        return shell;
    }

    public ServerConfig Config { get; private set; }

    public ServerSettingsViewModel Settings { get; }

    public ClientBuilderViewModel ClientBuilder { get; }

    public ObservableCollection<ServerTunnelRow> Tunnels { get; } = [];

    public ObservableCollection<ServerDeviceRow> Devices { get; } = [];

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public AsyncRelayCommand StartGatewayCommand { get; }

    public AsyncRelayCommand StopGatewayCommand { get; }

    public RelayCommand GoToCommand { get; }

    public RelayCommand ToggleKeyCommand { get; }

    public RelayCommand CopyKeyCommand { get; }

    public RelayCommand CopyFingerprintCommand { get; }

    public RelayCommand RotateKeyCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public AsyncRelayCommand DeleteTunnelCommand { get; }

    public RelayCommand EditTunnelCommand { get; }

    /// <summary>Raised when the operator asks to edit a tunnel; the view owns the window.</summary>
    public event Action<ServerTunnelRow>? EditTunnelRequested;

    /// <summary>Applies an edit through the same path a client's request takes.</summary>
    public Task<Core.Models.TunnelDefinition> UpdateTunnelAsync(Core.Protocol.Messages.UpdateTunnelRequest request)
        => _server.UpdateTunnelAsync(request, "server admin");

    public AsyncRelayCommand ToggleTunnelCommand { get; }

    public RelayCommand ApproveDeviceCommand { get; }

    public AsyncRelayCommand RevokeDeviceCommand { get; }

    public AsyncRelayCommand RemoveDeviceCommand { get; }

    // ---- Presentation -------------------------------------------------------

    public ServerPage Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                // The views switch on PageName, not on Page. Without this the selected page
                // never changes: the nav button highlights and nothing else happens.
                Raise(nameof(PageName));
            }
        }
    }

    /// <summary>
    /// The selected page as a string. Settable so the navigation buttons can bind their checked
    /// state to it two-way, which keeps the sidebar showing where you actually are.
    /// </summary>
    public string PageName
    {
        get => Page.ToString();
        set
        {
            if (Enum.TryParse<ServerPage>(value, out var page))
            {
                Page = page;
            }
        }
    }

    public bool IsRunning => _previewMode || _server.IsRunning;

    public string GatewayStatusLabel => Loc.Get(IsRunning ? "GatewayRunning" : "GatewayStopped");

    public int OnlineDeviceCount => Devices.Count(d => d.Online);

    public int DeviceCount => Devices.Count;

    public int TunnelCount => Tunnels.Count;

    public int ActiveConnectionCount => Tunnels.Sum(t => t.ActiveConnections);

    public string ControlEndpoint => $"{Config.PublicHost}:{Config.ControlPort}";

    public string CertificateFingerprint =>
        CertificateManager.FormatFingerprint(_previewMode
            ? new string('a', 64)
            : _server.ControlCertificateFingerprint);

    public bool KeyRevealed
    {
        get => _keyRevealed;
        private set
        {
            if (Set(ref _keyRevealed, value))
            {
                Raise(nameof(EnrollmentKeyDisplay));
            }
        }
    }

    /// <summary>
    /// Masked until asked for. The admin app is often on screen while sharing a desktop, and
    /// this one string is enough to enrol a machine.
    /// </summary>
    public string EnrollmentKeyDisplay => KeyRevealed
        ? Config.EnrollmentKey
        : new string('•', Math.Min(48, Math.Max(16, Config.EnrollmentKey.Length)));

    public string KeyGenerationLabel => $"generation {Config.KeyGeneration}";

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

    public string DataDirectory => ServerPaths.DataDirectory;

    /// <summary>Raised after any refresh, so the tray icon can follow the gateway's state.</summary>
    public event Action? StateChanged;

    // ---- Gateway ---------------------------------------------------------------

    private async Task StartAsync()
    {
        // The Windows service runs in another logon session, where the single-instance mutex
        // cannot reach it — but it binds the same ports from the same data directory. Asking the
        // service control manager is the one check that works across sessions, and it turns a
        // confusing "address already in use" into a sentence that says what to do.
        if (WindowsServiceManager.IsRunning)
        {
            SetMessage(Loc.Get("ServiceAlreadyRunning"), true);
            return;
        }

        try
        {
            await _server.StartAsync();
            SetMessage(null, false);
        }
        catch (InvalidOperationException ex)
        {
            // Configuration problems are expected and already phrased for a person.
            SetMessage(ex.Message, true);
            Page = ServerPage.Settings;
        }

        Refresh();
    }

    private async Task StopAsync()
    {
        await _server.StopAsync();
        Refresh();
    }

    /// <summary>
    /// Stops the gateway. Every await here is <c>ConfigureAwait(false)</c>: shutdown is driven
    /// from the UI thread, and resuming on a thread that is waiting for this method to finish is
    /// a deadlock — the window closes and the process lives on, unreachable.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Applies edited settings and restarts the listeners if the gateway is up.</summary>
    public async Task ApplyConfigAsync(ServerConfig config)
    {
        Config = config;
        _configStore.Save(config);
        await _server.ApplyConfigAsync(config);

        Raise(nameof(Config));
        Raise(nameof(ControlEndpoint));
        Raise(nameof(EnrollmentKeyDisplay));
        Raise(nameof(KeyGenerationLabel));
        Refresh();
    }

    private void RotateKey()
    {
        var confirmed = MessageBox.Show(
            Loc.Get("ConfirmRotateKey"),
            Loc.Get("RotateKey"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        _configStore.RotateEnrollmentKey(Config);
        KeyRevealed = true;

        Raise(nameof(EnrollmentKeyDisplay));
        Raise(nameof(KeyGenerationLabel));
        SetMessage(
            "Key rotated. Every existing client build is now refused — build and hand out new ones.",
            false);
    }

    // ---- Registry actions --------------------------------------------------------

    private async Task DeleteTunnelAsync(object? parameter)
    {
        if (parameter is not ServerTunnelRow row)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            Loc.Get("ConfirmDeleteTunnel"),
            Loc.Get("Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (confirmed)
        {
            await _server.DeleteTunnelAsync(row.Id, "server admin");
        }
    }

    private async Task ToggleTunnelAsync(object? parameter)
    {
        if (parameter is not ServerTunnelRow row)
        {
            return;
        }

        await _server.UpdateTunnelAsync(
            new Core.Protocol.Messages.UpdateTunnelRequest { TunnelId = row.Id, Enabled = !row.Enabled },
            "server admin");
    }

    private void ApproveDevice(object? parameter)
    {
        if (parameter is ServerDeviceRow row)
        {
            _server.ApproveDevice(row.Id, "server admin");
        }
    }

    private async Task RevokeDeviceAsync(object? parameter)
    {
        if (parameter is not ServerDeviceRow row)
        {
            return;
        }

        await _server.SetDeviceRevokedAsync(row.Id, !row.Revoked, "server admin");
    }

    private async Task RemoveDeviceAsync(object? parameter)
    {
        if (parameter is not ServerDeviceRow row)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            Loc.Get("ConfirmRemoveDevice"),
            Loc.Get("Remove"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (confirmed)
        {
            await _server.RemoveDeviceAsync(row.Id, "server admin");
        }
    }

    // ---- Refresh ------------------------------------------------------------------

    private void Refresh()
    {
        var devices = _registry.Devices;
        var tunnels = _registry.Tunnels;
        var server = _server.BuildServerInfo();

        var byId = devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var counts = tunnels.GroupBy(t => t.DeviceId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        Devices.Clear();
        foreach (var device in devices.OrderByDescending(d => d.Online).ThenBy(d => d.Name))
        {
            Devices.Add(new ServerDeviceRow(
                device,
                counts.GetValueOrDefault(device.Id),
                _registry.FindDevice(device.Id)?.HasDeviceKey ?? false));
        }

        Tunnels.Clear();
        foreach (var tunnel in tunnels.OrderBy(t => byId.GetValueOrDefault(t.DeviceId)?.Name ?? "").ThenBy(t => t.Name))
        {
            var device = byId.GetValueOrDefault(tunnel.DeviceId);
            Tunnels.Add(new ServerTunnelRow(tunnel, device?.Name ?? Loc.Get("Unknown"), device?.Online ?? false, server));
        }

        ClientBuilder.RefreshDevices(Devices);

        Raise(nameof(IsRunning));
        Raise(nameof(GatewayStatusLabel));
        Raise(nameof(OnlineDeviceCount));
        Raise(nameof(DeviceCount));
        Raise(nameof(TunnelCount));
        Raise(nameof(ActiveConnectionCount));
        Raise(nameof(CertificateFingerprint));

        StartGatewayCommand.RaiseCanExecuteChanged();
        StopGatewayCommand.RaiseCanExecuteChanged();

        StateChanged?.Invoke();
    }

    private void AppendLog(LogEntry entry)
    {
        LogEntries.Insert(0, entry);
        while (LogEntries.Count > 500)
        {
            LogEntries.RemoveAt(LogEntries.Count - 1);
        }
    }

    private void LoadPreview()
    {
        Config.ServerName = "CaYaDev Gateway";
        Config.PublicHost = "203.0.113.42";
        Config.BaseDomain = "tunnel.example.com";

        var snapshot = PreviewData.Build();
        var server = Config.ToServerInfo(true);

        Devices.Clear();
        foreach (var device in snapshot.Devices)
        {
            Devices.Add(new ServerDeviceRow(
                device,
                snapshot.Tunnels.Count(t => t.DeviceId == device.Id),
                hasDeviceKey: device.Name == "CAGAN-PC"));
        }

        Tunnels.Clear();
        foreach (var tunnel in snapshot.Tunnels)
        {
            var device = snapshot.Devices.FirstOrDefault(d => d.Id == tunnel.DeviceId);
            Tunnels.Add(new ServerTunnelRow(tunnel, device?.Name ?? "?", device?.Online ?? false, server));
        }

        foreach (var entry in ServerPreview.SampleLog())
        {
            LogEntries.Add(entry);
        }

        ClientBuilder.RefreshDevices(Devices);

        Raise(nameof(IsRunning));
        Raise(nameof(GatewayStatusLabel));
        Raise(nameof(OnlineDeviceCount));
        Raise(nameof(DeviceCount));
        Raise(nameof(TunnelCount));
        Raise(nameof(ActiveConnectionCount));
        Raise(nameof(ControlEndpoint));
    }

    // ---- Helpers ---------------------------------------------------------------------

    public void SetMessage(string? message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }

    private void ReportError(Exception exception) => OnUiThread(() => SetMessage(exception.Message, true));

    private void CopyToClipboard(string value)
    {
        try
        {
            Clipboard.SetText(value);
            SetMessage(Loc.Get("Copied"), false);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process is holding the clipboard.
        }
    }
}
