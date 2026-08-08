using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Server.Registry;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Server.App.ViewModels;

/// <summary>
/// Edits an existing tunnel from the admin side. Mirrors the client's editor, and deliberately
/// leaves the public address alone for the same reason: changing it is a new endpoint, not an
/// edit, and would silently break anyone already using the old one.
/// </summary>
public sealed class EditTunnelViewModel : ViewModelBase
{
    private readonly ServerShellViewModel _shell;
    private readonly ServerTunnelRow _row;

    private string _name;
    private string _targetHost;
    private string _targetPort;
    private bool _enabled;
    private int _transportIndex;
    private int _httpAccessIndex;
    private string? _error;

    public EditTunnelViewModel(ServerShellViewModel shell, ServerTunnelRow row)
    {
        _shell = shell;
        _row = row;

        var tunnel = row.Model;
        _name = tunnel.Name;
        _targetHost = tunnel.TargetHost;
        _targetPort = tunnel.TargetPort.ToString();
        _enabled = tunnel.Enabled;

        _transportIndex = tunnel.Transports switch
        {
            TransportProtocols.Udp => 1,
            TransportProtocols.Both => 2,
            _ => 0,
        };

        _httpAccessIndex = tunnel.HttpAccess switch
        {
            HttpAccess.HttpsOnly => 1,
            HttpAccess.HttpOnly => 2,
            HttpAccess.RedirectToHttps => 3,
            _ => 0,
        };

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave, ex => Error = ex.Message);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public event Action? Completed;

    public string PublicEndpoint => _row.Endpoint;

    public string KindLabel => _row.KindLabel;

    public string DeviceName => _row.DeviceName;

    public bool IsPortKind => _row.Model.Kind == TunnelKind.PortForward;

    public bool IsHttpKind => _row.Model.Kind == TunnelKind.HttpHost;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string TargetHost
    {
        get => _targetHost;
        set
        {
            if (Set(ref _targetHost, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TargetPort
    {
        get => _targetPort;
        set
        {
            if (Set(ref _targetPort, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public int TransportIndex
    {
        get => _transportIndex;
        set => Set(ref _transportIndex, value);
    }

    public int HttpAccessIndex
    {
        get => _httpAccessIndex;
        set => Set(ref _httpAccessIndex, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(TargetHost)
        && int.TryParse(TargetPort, out var port)
        && port is > 0 and <= 65535;

    private async Task SaveAsync()
    {
        Error = null;

        if (!int.TryParse(TargetPort, out var targetPort) || targetPort is < 1 or > 65535)
        {
            Error = "Target port must be a number between 1 and 65535.";
            return;
        }

        var request = new UpdateTunnelRequest
        {
            TunnelId = _row.Id,
            Name = Name.Trim(),
            TargetHost = TargetHost.Trim(),
            TargetPort = targetPort,
            Enabled = Enabled,
        };

        if (IsPortKind)
        {
            request.Transports = TransportIndex switch
            {
                1 => TransportProtocols.Udp,
                2 => TransportProtocols.Both,
                _ => TransportProtocols.Tcp,
            };
        }

        if (IsHttpKind)
        {
            request.HttpAccess = HttpAccessIndex switch
            {
                1 => HttpAccess.HttpsOnly,
                2 => HttpAccess.HttpOnly,
                3 => HttpAccess.RedirectToHttps,
                _ => HttpAccess.HttpAndHttps,
            };
        }

        try
        {
            await _shell.UpdateTunnelAsync(request);
            Completed?.Invoke();
        }
        catch (TunnelValidationException ex)
        {
            // Already phrased for a person by the registry.
            Error = ex.Message;
        }
    }
}
