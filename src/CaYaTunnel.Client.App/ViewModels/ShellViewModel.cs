using System.Collections.ObjectModel;
using System.Windows;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Client.App.ViewModels;

public enum ClientPage
{
    Tunnels,
    Devices,
    Settings,
}

/// <summary>
/// The client's whole UI state. Owns the <see cref="TunnelClient"/> and republishes its events
/// onto the UI thread — every one of them arrives from the link's read loop.
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly ClientSettingsStore _store;
    private readonly TunnelClient _client;

    private ClientPage _page = ClientPage.Tunnels;
    private bool _showAllDevices = true;
    private string _statusLabel = "";
    private string? _statusDetail;
    private string? _banner;
    private string? _bannerDetail;
    private string _bannerSeverity = "info";
    private string? _toast;

    public ShellViewModel(ClientSettingsStore store, ClientSettings settings, ClientConnectionProfile profile)
    {
        _store = store;
        Settings = settings;
        Profile = profile;

        Loc.Current.Language = (AppLanguage)Math.Clamp(settings.Language, 0, 2);

        _client = new TunnelClient(store, settings, profile);
        _client.StateChanged += (state, message) => OnUiThread(() => ApplyState(state, message));
        _client.SnapshotChanged += snapshot => OnUiThread(() => ApplySnapshot(snapshot));
        _client.NoticeReceived += notice => OnUiThread(() => ShowNotice(notice));
        _client.LogMessage += message => OnUiThread(() => AddActivity(message));

        SettingsPage = new ClientSettingsViewModel(this, store, settings, profile);

        ConnectCommand = new RelayCommand(Connect, () => !IsOnline);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsOnline);
        RefreshCommand = new AsyncRelayCommand(async () => await _client.RefreshAsync());
        NewTunnelCommand = new RelayCommand(() => NewTunnelRequested?.Invoke(), () => IsOnline);
        DeleteTunnelCommand = new AsyncRelayCommand(DeleteTunnelAsync, p => p is TunnelRow && IsOnline, ReportError);
        ToggleTunnelCommand = new AsyncRelayCommand(ToggleTunnelAsync, p => p is TunnelRow && IsOnline, ReportError);
        CopyEndpointCommand = new RelayCommand(CopyEndpoint, p => p is TunnelRow);
        DismissBannerCommand = new RelayCommand(() => Banner = null);
        GoToCommand = new RelayCommand(p =>
        {
            if (p is string name && Enum.TryParse<ClientPage>(name, out var page))
            {
                Page = page;
            }
        });

        ApplyState(_client.State, _client.StatusMessage);
    }

    public ClientSettings Settings { get; }

    public ClientConnectionProfile Profile { get; private set; }

    public ClientSettingsViewModel SettingsPage { get; }

    public ObservableCollection<TunnelRow> Tunnels { get; } = [];

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public ObservableCollection<string> Activity { get; } = [];

    /// <summary>Raised when the user asks for the new-tunnel dialog; the view owns the window.</summary>
    public event Action? NewTunnelRequested;

    public RelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand NewTunnelCommand { get; }

    public AsyncRelayCommand DeleteTunnelCommand { get; }

    public AsyncRelayCommand ToggleTunnelCommand { get; }

    public RelayCommand CopyEndpointCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    public RelayCommand GoToCommand { get; }

    // ---- Presentation state -------------------------------------------------

    public ClientPage Page
    {
        get => _page;
        set => Set(ref _page, value);
    }

    public string PageName => Page.ToString();

    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set
        {
            if (Set(ref _showAllDevices, value))
            {
                RebuildTunnels();
            }
        }
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => Set(ref _statusLabel, value);
    }

    public string? StatusDetail
    {
        get => _statusDetail;
        private set => Set(ref _statusDetail, value);
    }

    public bool IsOnline => _client.State == ClientState.Online;

    public bool IsBusy => _client.State is ClientState.Connecting or ClientState.Authenticating or ClientState.Reconnecting;

    public bool IsUnauthorized => _client.State == ClientState.Unauthorized;

    public bool IsConfigured => Profile.IsUsable;

    public string ServerName => Profile.ServerName;

    public string DeviceName => _client.DeviceName;

    public string LatencyLabel => _client.LatencyMs is { } ms ? $"{ms} ms" : "—";

    public string? Banner
    {
        get => _banner;
        private set => Set(ref _banner, value);
    }

    public string? BannerDetail
    {
        get => _bannerDetail;
        private set => Set(ref _bannerDetail, value);
    }

    public string BannerSeverity
    {
        get => _bannerSeverity;
        private set => Set(ref _bannerSeverity, value);
    }

    /// <summary>Short-lived confirmation, e.g. "Copied to clipboard".</summary>
    public string? Toast
    {
        get => _toast;
        private set => Set(ref _toast, value);
    }

    public int TunnelCount => Tunnels.Count;

    public int OnlineDeviceCount => Devices.Count(d => d.Online);

    // ---- Lifecycle ------------------------------------------------------------

    public void Connect() => _client.Start();

    public async Task DisconnectAsync() => await _client.StopAsync();

    public async Task ShutdownAsync()
    {
        _store.Save(Settings);
        await _client.DisposeAsync();
    }

    /// <summary>Re-points the client at a different server after the user edits the settings.</summary>
    public async Task ReconfigureAsync(ClientConnectionProfile profile)
    {
        Profile = profile;
        Raise(nameof(Profile));
        Raise(nameof(IsConfigured));
        Raise(nameof(ServerName));

        await _client.ReconfigureAsync(Settings, profile);
    }

    public Task<ControlResult> CreateTunnelAsync(CreateTunnelRequest request) => _client.CreateTunnelAsync(request);

    public string ThisDeviceId => _client.DeviceId;

    /// <summary>
    /// Last registry the UI rendered. Held here rather than read back from the client so the
    /// capture mode can populate the screens without a live connection.
    /// </summary>
    public RegistrySnapshot? Snapshot { get; private set; }

    public ServerInfo? ServerInfo => Snapshot?.Server;

    public IReadOnlyList<DeviceInfo> KnownDevices => Snapshot?.Devices ?? [];

    // ---- Event plumbing ---------------------------------------------------------

    private void ApplyState(ClientState state, string? message)
    {
        StatusLabel = Loc.Get(state switch
        {
            ClientState.Online => "StateOnline",
            ClientState.Connecting => "StateConnecting",
            ClientState.Authenticating => "StateAuthenticating",
            ClientState.Reconnecting => "StateReconnecting",
            ClientState.Unauthorized => "StateUnauthorized",
            _ => "StateOffline",
        });

        StatusDetail = message;

        if (state == ClientState.Unauthorized)
        {
            BannerSeverity = "error";
            Banner = Loc.Get("UnauthorizedTitle");
            BannerDetail = _client.RefusalReason switch
            {
                AuthFailureReason.KeyRotated => Loc.Get("UnauthorizedKeyRotated"),
                AuthFailureReason.DeviceRevoked => Loc.Get("UnauthorizedRevoked"),
                _ => message,
            };
        }
        else if (state == ClientState.Online && BannerSeverity == "error")
        {
            Banner = null;
        }

        Raise(nameof(IsOnline));
        Raise(nameof(IsBusy));
        Raise(nameof(IsUnauthorized));
        Raise(nameof(LatencyLabel));

        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        NewTunnelCommand.RaiseCanExecuteChanged();
        DeleteTunnelCommand.RaiseCanExecuteChanged();
        ToggleTunnelCommand.RaiseCanExecuteChanged();

        StateChanged?.Invoke();
    }

    public event Action? StateChanged;

    /// <summary>
    /// Loads a registry without a server, for screenshots and layout checks. Only the capture
    /// mode calls this — the running app always gets its state from the gateway.
    /// </summary>
    public void LoadPreviewSnapshot(RegistrySnapshot snapshot)
    {
        StatusLabel = Loc.Get("StateOnline");
        StatusDetail = null;
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(RegistrySnapshot snapshot)
    {
        Snapshot = snapshot;
        RebuildDevices(snapshot);
        RebuildTunnels(snapshot);
        Raise(nameof(LatencyLabel));
        Raise(nameof(ServerInfo));
    }

    private void RebuildDevices(RegistrySnapshot snapshot)
    {
        var tunnelCounts = snapshot.Tunnels
            .GroupBy(t => t.DeviceId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        Devices.Clear();
        foreach (var device in snapshot.Devices.OrderByDescending(d => d.Online).ThenBy(d => d.Name))
        {
            Devices.Add(new DeviceRow(
                device,
                tunnelCounts.GetValueOrDefault(device.Id),
                isThisDevice: device.Id == _client.DeviceId));
        }

        Raise(nameof(OnlineDeviceCount));
    }

    private void RebuildTunnels(RegistrySnapshot? snapshot = null)
    {
        snapshot ??= Snapshot;
        if (snapshot is null)
        {
            return;
        }

        var devices = snapshot.Devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        Tunnels.Clear();
        var ordered = snapshot.Tunnels
            .Where(t => ShowAllDevices || t.DeviceId == _client.DeviceId)
            .OrderBy(t => devices.GetValueOrDefault(t.DeviceId)?.Name ?? "")
            .ThenBy(t => t.Name);

        foreach (var tunnel in ordered)
        {
            var device = devices.GetValueOrDefault(tunnel.DeviceId);
            Tunnels.Add(new TunnelRow(
                tunnel,
                device?.Name ?? Loc.Get("Unknown"),
                device?.Online ?? false,
                tunnel.DeviceId == _client.DeviceId,
                snapshot.Server));
        }

        Raise(nameof(TunnelCount));
    }

    private void ShowNotice(NoticeMessage notice)
    {
        BannerSeverity = notice.Severity;
        Banner = notice.Title;
        BannerDetail = notice.Body;
        AddActivity(notice.Body is null ? notice.Title : $"{notice.Title} — {notice.Body}");

        NoticeRaised?.Invoke(notice);
    }

    /// <summary>Lets the tray icon raise a balloon for the same notice the banner shows.</summary>
    public event Action<NoticeMessage>? NoticeRaised;

    private void AddActivity(string message)
    {
        Activity.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {message}");
        while (Activity.Count > 300)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
    }

    // ---- Commands ---------------------------------------------------------------

    private async Task DeleteTunnelAsync(object? parameter)
    {
        if (parameter is not TunnelRow row)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            Loc.Get("ConfirmDeleteTunnel"),
            Loc.Get("Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        var result = await _client.DeleteTunnelAsync(row.Id);
        if (!result.Ok)
        {
            ShowError(result.Error);
        }
    }

    private async Task ToggleTunnelAsync(object? parameter)
    {
        if (parameter is not TunnelRow row)
        {
            return;
        }

        var result = await _client.UpdateTunnelAsync(new UpdateTunnelRequest
        {
            TunnelId = row.Id,
            Enabled = !row.Enabled,
        });

        if (!result.Ok)
        {
            ShowError(result.Error);
        }
    }

    private void CopyEndpoint(object? parameter)
    {
        if (parameter is not TunnelRow row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Endpoint);
            FlashToast(Loc.Get("Copied"));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process had the clipboard open; not worth an error dialog.
        }
    }

    public void FlashToast(string message)
    {
        Toast = message;

        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => OnUiThread(() =>
        {
            if (Toast == message)
            {
                Toast = null;
            }
        }), TaskScheduler.Default);
    }

    public void ShowError(string? message)
    {
        BannerSeverity = "error";
        Banner = message ?? "Something went wrong.";
        BannerDetail = null;
    }

    private void ReportError(Exception exception) => OnUiThread(() => ShowError(exception.Message));
}
