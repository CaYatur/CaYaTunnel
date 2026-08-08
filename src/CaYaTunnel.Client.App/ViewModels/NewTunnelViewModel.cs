using System.Collections.ObjectModel;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Client.App.ViewModels;

/// <summary>
/// The new-tunnel dialog. The three kinds are presented in the user's terms — a website, a
/// Minecraft server, anything else — because "shared listener with SNI routing" is an
/// implementation detail, while "does it get its own port" is the actual consequence.
/// </summary>
public sealed class NewTunnelViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    private TunnelKind _kind = TunnelKind.HttpHost;
    private string _name = "";
    private string _subdomain = "";
    private string _targetHost = "127.0.0.1";
    private string _targetPort = "";
    private string _publicPort = "";
    private bool _terminateTls = true;
    private string? _error;
    private DeviceChoice? _device;
    private int _transportIndex;
    private int _httpAccessIndex;
    private bool _useSharedPort;
    private bool _rewriteHostHeader;

    public NewTunnelViewModel(ShellViewModel shell)
    {
        _shell = shell;

        foreach (var device in shell.KnownDevices.Where(d => d.Online && !d.Revoked).OrderBy(d => d.Name))
        {
            Devices.Add(new DeviceChoice(device.Id, device.Name, device.Id == shell.ThisDeviceId));
        }

        _device = Devices.FirstOrDefault(d => d.IsThisDevice) ?? Devices.FirstOrDefault();

        foreach (var address in TunnelClient.GetLocalAddresses())
        {
            LocalAddresses.Add(address);
        }

        RandomiseCommand = new RelayCommand(() => Subdomain = TunnelNameGenerator.NewLabel());
        UseLocalAddressCommand = new RelayCommand(p =>
        {
            if (p is string address)
            {
                TargetHost = address;
            }
        });

        CreateCommand = new AsyncRelayCommand(CreateAsync, () => CanCreate, ex => Error = ex.Message);
    }

    public ObservableCollection<DeviceChoice> Devices { get; } = [];

    public ObservableCollection<string> LocalAddresses { get; } = [];

    public RelayCommand RandomiseCommand { get; }

    public RelayCommand UseLocalAddressCommand { get; }

    public AsyncRelayCommand CreateCommand { get; }

    /// <summary>Set when the tunnel was created, so the dialog can close itself.</summary>
    public event Action? Completed;

    public bool HostnamesAvailable => _shell.ServerInfo?.HostnameTunnelsAvailable ?? false;

    public string BaseDomainSuffix => HostnamesAvailable ? "." + _shell.ServerInfo!.BaseDomain : "";

    public TunnelKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                Raise(nameof(IsHostnameKind));
                Raise(nameof(IsPortKind));
                Raise(nameof(IsHttpKind));
                Raise(nameof(SuggestedPortHint));
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsHttp
    {
        get => Kind == TunnelKind.HttpHost;
        set
        {
            if (value)
            {
                Kind = TunnelKind.HttpHost;
            }
        }
    }

    public bool IsMinecraft
    {
        get => Kind == TunnelKind.TcpHostAware;
        set
        {
            if (value)
            {
                Kind = TunnelKind.TcpHostAware;
            }
        }
    }

    public bool IsTcp
    {
        get => Kind == TunnelKind.PortForward;
        set
        {
            if (value)
            {
                Kind = TunnelKind.PortForward;
            }
        }
    }

    public bool IsHostnameKind => Kind is TunnelKind.HttpHost or TunnelKind.TcpHostAware;

    public bool IsPortKind => Kind == TunnelKind.PortForward;

    public bool IsHttpKind => Kind == TunnelKind.HttpHost;

    /// <summary>0 = TCP, 1 = UDP, 2 = both. Bound to a combo box by index.</summary>
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

    public bool ShowsUdpNote => IsPortKind && TransportIndex is 1 or 2;

    public TransportProtocols Transports => TransportIndex switch
    {
        1 => TransportProtocols.Udp,
        2 => TransportProtocols.Both,
        _ => TransportProtocols.Tcp,
    };

    /// <summary>0 = both schemes, 1 = HTTPS only, 2 = HTTP only, 3 = redirect to HTTPS.</summary>
    public int HttpAccessIndex
    {
        get => _httpAccessIndex;
        set => Set(ref _httpAccessIndex, value);
    }

    public HttpAccess HttpAccess => HttpAccessIndex switch
    {
        1 => HttpAccess.HttpsOnly,
        2 => HttpAccess.HttpOnly,
        3 => HttpAccess.RedirectToHttps,
        _ => HttpAccess.HttpAndHttps,
    };

    public string SuggestedPortHint => Kind switch
    {
        TunnelKind.HttpHost => "3000",
        TunnelKind.TcpHostAware => "25565",
        _ => "8080",
    };

    public DeviceChoice? Device
    {
        get => _device;
        set
        {
            if (Set(ref _device, value))
            {
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Subdomain
    {
        get => _subdomain;
        set => Set(ref _subdomain, value);
    }

    public string TargetHost
    {
        get => _targetHost;
        set
        {
            if (Set(ref _targetHost, value))
            {
                CreateCommand.RaiseCanExecuteChanged();
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
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PublicPort
    {
        get => _publicPort;
        set => Set(ref _publicPort, value);
    }

    /// <summary>
    /// Ride the gateway's shared port instead of taking one of its own — the difference between
    /// forwarding one port and forwarding several.
    /// </summary>
    public bool UseSharedPort
    {
        get => _useSharedPort;
        set
        {
            if (Set(ref _useSharedPort, value))
            {
                Raise(nameof(ShowsPublicPortField));
            }
        }
    }

    public bool ShowsPublicPortField => !UseSharedPort;

    public bool TerminateTls
    {
        get => _terminateTls;
        set => Set(ref _terminateTls, value);
    }

    /// <summary>Present the target's own address as the Host header; see the tunnel model.</summary>
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

    public bool CanCreate =>
        Device is not null
        && !string.IsNullOrWhiteSpace(TargetHost)
        && int.TryParse(TargetPort, out var port)
        && port is > 0 and <= 65535
        && (!IsHostnameKind || HostnamesAvailable);

    private async Task CreateAsync()
    {
        Error = null;

        if (!int.TryParse(TargetPort, out var targetPort))
        {
            Error = "Target port must be a number.";
            return;
        }

        int? publicPort = null;
        if (IsPortKind && !string.IsNullOrWhiteSpace(PublicPort))
        {
            if (!int.TryParse(PublicPort, out var parsed) || parsed is < 1 or > 65535)
            {
                Error = "Public port must be a number between 1 and 65535.";
                return;
            }

            publicPort = parsed;
        }

        var result = await _shell.CreateTunnelAsync(new CreateTunnelRequest
        {
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            Kind = Kind,
            DeviceId = Device?.Id,
            TargetHost = TargetHost.Trim(),
            TargetPort = targetPort,
            Subdomain = IsHostnameKind && !string.IsNullOrWhiteSpace(Subdomain) ? Subdomain.Trim() : null,
            PublicPort = publicPort,
            Protocol = Kind == TunnelKind.TcpHostAware ? HostAwareProtocols.MinecraftJava : null,
            TerminateTls = TerminateTls,
            HttpAccess = HttpAccess,
            RewriteHostHeader = IsHttpKind && RewriteHostHeader,
            Transports = Transports,
            UseSharedPort = IsPortKind && UseSharedPort,
        });

        if (result.Ok)
        {
            Completed?.Invoke();
        }
        else
        {
            // Server-side rejections are written for people ("that hostname is already in use"),
            // so they are shown as-is rather than translated into something vaguer.
            Error = result.Error;
        }
    }
}

public sealed record DeviceChoice(string Id, string Name, bool IsThisDevice)
{
    public string Display => IsThisDevice ? $"{Name} ({Loc.Get("ThisDevice")})" : Name;
}
