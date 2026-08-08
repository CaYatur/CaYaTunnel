using System.Collections.ObjectModel;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Client.App.ViewModels;

/// <summary>
/// Edits an existing tunnel.
/// <para>
/// The public address is deliberately not editable. Changing a hostname or a public port is not
/// an edit — it is a different endpoint, with different DNS and a different listener, and anyone
/// already using the old one would silently lose it. Delete and recreate says that out loud.
/// </para>
/// </summary>
public sealed class EditTunnelViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly TunnelRow _row;

    private string _name;
    private string _targetHost;
    private string _targetPort;
    private bool _enabled;
    private int _transportIndex;
    private int _httpAccessIndex;
    private bool _rewriteHostHeader;
    private string? _error;

    public EditTunnelViewModel(ShellViewModel shell, TunnelRow row)
    {
        _shell = shell;
        _row = row;

        var tunnel = row.Model;
        _name = tunnel.Name;
        _targetHost = tunnel.TargetHost;
        _targetPort = tunnel.TargetPort.ToString();
        _enabled = tunnel.Enabled;
        _rewriteHostHeader = tunnel.RewriteHostHeader;

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

        foreach (var address in TunnelClient.GetLocalAddresses())
        {
            LocalAddresses.Add(address);
        }

        UseLocalAddressCommand = new RelayCommand(p =>
        {
            if (p is string address)
            {
                TargetHost = address;
            }
        });

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave, ex => Error = ex.Message);
    }

    public ObservableCollection<string> LocalAddresses { get; } = [];

    public RelayCommand UseLocalAddressCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public event Action? Completed;

    public string PublicEndpoint => _row.Endpoint;

    public string KindLabel => _row.KindLabel;

    public bool IsPortKind => _row.Model.Kind == TunnelKind.PortForward;

    public bool IsHttpKind => _row.Model.Kind == TunnelKind.HttpHost;

    public bool ShowsUdpNote => IsPortKind && TransportIndex is 1 or 2;

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
        set
        {
            if (Set(ref _transportIndex, value))
            {
                Raise(nameof(ShowsUdpNote));
            }
        }
    }

    public int HttpAccessIndex
    {
        get => _httpAccessIndex;
        set => Set(ref _httpAccessIndex, value);
    }

    public bool RewriteHostHeader
    {
        get => _rewriteHostHeader;
        set => Set(ref _rewriteHostHeader, value);
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

        // Only sent for the kind it applies to, so an HTTP tunnel never carries a transport
        // change and a port tunnel never carries a scheme change.
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

            request.RewriteHostHeader = RewriteHostHeader;
        }

        var result = await _shell.UpdateTunnelAsync(request);

        if (result.Ok)
        {
            Completed?.Invoke();
        }
        else
        {
            Error = result.Error;
        }
    }
}
