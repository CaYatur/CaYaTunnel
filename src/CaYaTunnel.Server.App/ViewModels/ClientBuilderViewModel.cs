using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CaYaTunnel.Server.Gateway;
using CaYaTunnel.Server.Provisioning;
using CaYaTunnel.Server.Registry;
using CaYaTunnel.Ui;
using Microsoft.Win32;

namespace CaYaTunnel.Server.App.ViewModels;

/// <summary>
/// Turns the deployment into a client executable the user can just run. The whole point is that
/// nothing has to be typed on the other machine — no address, no key, no certificate fingerprint.
/// </summary>
public sealed class ClientBuilderViewModel : ViewModelBase
{
    private readonly ServerShellViewModel _shell;
    private readonly TunnelServer _server;
    private readonly TunnelRegistry _registry;

    private string? _stubPath;
    private DeviceChoice? _device;
    private string? _message;
    private bool _messageIsError;
    private string? _lastBuiltPath;

    public ClientBuilderViewModel(ServerShellViewModel shell, TunnelServer server, TunnelRegistry registry)
    {
        _shell = shell;
        _server = server;
        _registry = registry;

        _stubPath = ClientBuilder.FindStub();
        Devices.Add(DeviceChoice.Shared);
        _device = Devices[0];

        ChooseStubCommand = new RelayCommand(ChooseStub);
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => HasStub, ex => SetMessage(ex.Message, true));
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => _lastBuiltPath is not null);
    }

    public ObservableCollection<DeviceChoice> Devices { get; } = [];

    public RelayCommand ChooseStubCommand { get; }

    public AsyncRelayCommand BuildCommand { get; }

    public RelayCommand ShowInFolderCommand { get; }

    public bool HasStub => !string.IsNullOrWhiteSpace(_stubPath) && File.Exists(_stubPath);

    public string StubPathLabel => HasStub ? _stubPath! : Loc.Get("StubMissing");

    public DeviceChoice? Device
    {
        get => _device;
        set => Set(ref _device, value);
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

    public bool HasBuilt => _lastBuiltPath is not null;

    /// <summary>Keeps the device list in step with the registry.</summary>
    public void RefreshDevices(IEnumerable<ServerDeviceRow> devices)
    {
        var selectedId = Device?.Id;

        Devices.Clear();
        Devices.Add(DeviceChoice.Shared);

        foreach (var device in devices.Where(d => !d.Revoked))
        {
            Devices.Add(new DeviceChoice(device.Id, device.Name));
        }

        Device = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices[0];
    }

    private void ChooseStub()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("ImportStub"),
            Filter = "CaYaTunnel client (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _stubPath = dialog.FileName;
        Raise(nameof(HasStub));
        Raise(nameof(StubPathLabel));
        BuildCommand.RaiseCanExecuteChanged();
    }

    private async Task BuildAsync()
    {
        if (!HasStub)
        {
            SetMessage(Loc.Get("StubMissing"), true);
            return;
        }

        var deviceId = Device is { IsShared: false } ? Device.Id : null;
        var deviceName = Device is { IsShared: false } ? Device.Name : null;

        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("BuildClient"),
            Filter = "Application (*.exe)|*.exe",
            FileName = ClientBuilder.SuggestFileName(deviceName),
            InitialDirectory = Configuration.ServerPaths.ProvisionedDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var builder = new ClientBuilder(_shell.Config, _registry, _server.ControlCertificateFingerprint);

        var result = await builder.BuildAsync(
            _stubPath!,
            dialog.FileName,
            deviceId,
            note: $"built {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");

        _lastBuiltPath = result.Path;

        var keyKind = result.PerDeviceKey
            ? $"per-device key for {result.DeviceName}"
            : "shared enrollment key";

        SetMessage($"{Loc.Get("BuildSucceeded")} {keyKind}, {ByteSizeConverter.Format(result.SizeBytes)}.", false);

        Raise(nameof(HasBuilt));
        ShowInFolderCommand.RaiseCanExecuteChanged();
    }

    private void ShowInFolder()
    {
        if (_lastBuiltPath is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastBuiltPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            SetMessage(ex.Message, true);
        }
    }

    private void SetMessage(string? message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }
}

/// <summary>A build target: one registered device, or any machine using the shared key.</summary>
public sealed record DeviceChoice(string Id, string Name)
{
    public static DeviceChoice Shared { get; } = new("", Loc.Get("BuildGeneric"));

    public bool IsShared => string.IsNullOrEmpty(Id);

    public string Display => Name;
}
