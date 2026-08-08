using System.Diagnostics;
using System.IO;
using System.Windows;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Client.App.ViewModels;

/// <summary>
/// The settings screen. Connection fields are read-only for a provisioned build — those values
/// came from the server and editing them would only break the link.
/// </summary>
public sealed class ClientSettingsViewModel : ViewModelBase
{
    private const string StartupAppName = "CaYaTunnel Client";
    private const string StartupTaskName = "CaYaTunnel Client Startup";

    private readonly ShellViewModel _shell;
    private readonly ClientSettingsStore _store;
    private readonly ClientSettings _settings;

    private string _serverHost;
    private string _controlPort;
    private string _enrollmentKey;
    private string _fingerprint;
    private string _deviceName;
    private string? _message;
    private bool _messageIsError;

    public ClientSettingsViewModel(
        ShellViewModel shell,
        ClientSettingsStore store,
        ClientSettings settings,
        ClientConnectionProfile profile)
    {
        _shell = shell;
        _store = store;
        _settings = settings;

        IsProvisioned = profile.Provisioned;
        _serverHost = profile.ServerHost;
        _controlPort = profile.ControlPort > 0 ? profile.ControlPort.ToString() : "";
        _enrollmentKey = profile.Provisioned ? "" : profile.EnrollmentKey;
        _fingerprint = profile.CertificateFingerprint ?? "";
        _deviceName = settings.DeviceName ?? Environment.MachineName;

        StartupState = StartupManager.GetState(StartupAppName, StartupTaskName);

        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => SetMessage(ex.Message, isError: true));
        ForgetCommand = new AsyncRelayCommand(ForgetAsync);
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        RelaunchElevatedCommand = new RelayCommand(() => StartupManager.RelaunchElevated());
    }

    public bool IsProvisioned { get; }

    public bool IsManual => !IsProvisioned;

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand ForgetCommand { get; }

    public RelayCommand OpenSettingsFolderCommand { get; }

    public RelayCommand RelaunchElevatedCommand { get; }

    // ---- Connection ---------------------------------------------------------

    public string ServerHost
    {
        get => _serverHost;
        set => Set(ref _serverHost, value);
    }

    public string ControlPort
    {
        get => _controlPort;
        set => Set(ref _controlPort, value);
    }

    public string EnrollmentKey
    {
        get => _enrollmentKey;
        set => Set(ref _enrollmentKey, value);
    }

    public string CertificateFingerprint
    {
        get => _fingerprint;
        set => Set(ref _fingerprint, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => Set(ref _deviceName, value);
    }

    // ---- Behaviour ------------------------------------------------------------

    public bool ConnectOnLaunch
    {
        get => _settings.ConnectOnLaunch;
        set
        {
            _settings.ConnectOnLaunch = value;
            Raise();
            Persist();
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            _settings.CloseToTray = value;
            Raise();
            Persist();
        }
    }

    public bool ShowNotifications
    {
        get => _settings.ShowNotifications;
        set
        {
            _settings.ShowNotifications = value;
            Raise();
            Persist();
        }
    }

    public int LanguageIndex
    {
        get => Math.Clamp(_settings.Language, 0, 2);
        set
        {
            _settings.Language = Math.Clamp(value, 0, 2);
            Loc.Current.Language = (AppLanguage)_settings.Language;
            Raise();
            Persist();
        }
    }

    // ---- Startup ---------------------------------------------------------------

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

    public bool StartMinimised
    {
        get => _settings.StartMinimised;
        set
        {
            _settings.StartMinimised = value;
            Raise();
            Persist();

            // The hidden flag lives in the startup command line, so an existing entry has to be
            // rewritten for the change to take effect at the next sign-in.
            if (StartupState != StartupState.Disabled)
            {
                ApplyStartup(enabled: true, elevated: StartElevated);
            }
        }
    }

    public bool CanElevate => StartupManager.IsElevated;

    private void ApplyStartup(bool enabled, bool elevated)
    {
        // Only one mechanism should ever be registered, or the app would launch twice.
        StartupManager.SetRunKey(StartupAppName, enabled: false, startHidden: false);

        if (enabled && elevated)
        {
            var (ok, message) = StartupManager.SetElevatedTask(StartupTaskName, enabled: true, _settings.StartMinimised);
            SetMessage(message, isError: !ok);
        }
        else
        {
            StartupManager.SetElevatedTask(StartupTaskName, enabled: false, false);

            if (enabled)
            {
                var ok = StartupManager.SetRunKey(StartupAppName, enabled: true, _settings.StartMinimised);
                if (!ok)
                {
                    SetMessage("Windows would not accept the startup entry.", isError: true);
                }
            }
        }

        StartupState = StartupManager.GetState(StartupAppName, StartupTaskName);
        Raise(nameof(StartupState));
        Raise(nameof(StartWithWindows));
        Raise(nameof(StartElevated));
    }

    // ---- Identity ----------------------------------------------------------------

    public string SettingsPath => _store.Path;

    public string StorageModeLabel => Loc.Get(_store.IsPortable ? "PortableMode" : "AppDataMode");

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

    private async Task SaveAsync()
    {
        var name = DeviceName.Trim();
        _settings.DeviceName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;

        if (IsManual)
        {
            if (!int.TryParse(ControlPort, out var port) || port is < 1 or > 65535)
            {
                SetMessage("Control port must be a number between 1 and 65535.", isError: true);
                return;
            }

            _settings.ManualServerHost = ServerHost.Trim();
            _settings.ManualControlPort = port;
            _settings.ManualEnrollmentKey = EnrollmentKey.Trim();
            _settings.ManualCertificateFingerprint = CertificateFingerprint.Replace(":", "").Trim();
        }

        Persist();

        var profile = ClientConnectionProfile.Resolve(
            _settings,
            Core.Provisioning.ClientConfigBlob.ReadFromCurrentProcess());

        await _shell.ReconfigureAsync(profile);
        SetMessage(Loc.Get("SettingsSaved"), isError: false);
    }

    private async Task ForgetAsync()
    {
        var confirmed = MessageBox.Show(
            Loc.Get("ConfirmForgetDevice"),
            Loc.Get("ForgetDevice"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        await _shell.DisconnectAsync();

        _settings.DeviceId = null;
        Persist();

        SetMessage(Loc.Get("SettingsSaved"), isError: false);
    }

    private void OpenSettingsFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(_store.Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            SetMessage(ex.Message, isError: true);
        }
    }

    private void Persist() => _store.Save(_settings);

    private void SetMessage(string? message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }
}
