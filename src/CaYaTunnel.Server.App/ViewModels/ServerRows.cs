using System.Windows.Media;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Server.App.ViewModels;

/// <summary>A tunnel as the admin list renders it.</summary>
public sealed class ServerTunnelRow(TunnelDefinition tunnel, string deviceName, bool deviceOnline, ServerInfo server)
    : ViewModelBase
{
    public TunnelDefinition Model { get; } = tunnel;

    public string Id => Model.Id;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Endpoint : Model.Name;

    public string DeviceName { get; } = deviceName;

    public string Endpoint => Model.PublicEndpoint(server);

    public string TargetEndpoint => Model.TargetEndpoint();

    public string KindLabel => Model.Kind switch
    {
        TunnelKind.HttpHost => "HTTP",
        TunnelKind.TcpHostAware => "MC",
        _ => "TCP",
    };

    public string Traffic => $"↓ {ByteSizeConverter.Format(Model.BytesIn)}   ↑ {ByteSizeConverter.Format(Model.BytesOut)}";

    public int ActiveConnections => Model.ActiveConnections;

    public bool HasActiveConnections => Model.ActiveConnections > 0;

    public long TotalConnections => Model.TotalConnections;

    public bool Enabled => Model.Enabled;

    public string StatusLabel => !Model.Enabled
        ? Loc.Get("TunnelDisabled")
        : !deviceOnline
            ? Loc.Get("DeviceOffline")
            : Loc.Get("Online");

    public Brush StatusBrush => !Model.Enabled
        ? StatusBrushes.Muted
        : !deviceOnline
            ? StatusBrushes.Warning
            : StatusBrushes.Online;

    public Brush StatusWashBrush => !Model.Enabled
        ? StatusBrushes.MutedWash
        : !deviceOnline
            ? StatusBrushes.WarningWash
            : StatusBrushes.OnlineWash;
}

/// <summary>A device as the admin list renders it, with the operator actions it supports.</summary>
public sealed class ServerDeviceRow(DeviceInfo device, int tunnelCount, bool hasDeviceKey) : ViewModelBase
{
    public DeviceInfo Model { get; } = device;

    public string Id => Model.Id;

    public string Name => Model.Name;

    public bool Online => Model.Online;

    public bool Revoked => Model.Revoked;

    public bool PendingApproval => !Model.Approved && !Model.Revoked;

    /// <summary>
    /// True when this device has its own key, which is what makes revoking it meaningful.
    /// Shared-key devices can only really be cut off by rotating the server key.
    /// </summary>
    public bool HasDeviceKey { get; } = hasDeviceKey;

    public string KeyKindLabel => HasDeviceKey ? "per-device key" : "shared key";

    public int TunnelCount { get; } = tunnelCount;

    public string RemoteAddress => Model.RemoteAddress ?? "—";

    public string LocalAddresses => Model.LocalAddresses.Count == 0 ? "—" : string.Join(", ", Model.LocalAddresses);

    public string OperatingSystem => Model.OperatingSystem ?? Loc.Get("Unknown");

    public string ClientVersion => Model.ClientVersion ?? "—";

    public string Latency => Model.LatencyMs is { } ms ? $"{ms} ms" : "—";

    public string LastSeen => Model.LastSeenAt is { } moment ? RelativeTimeConverter.Format(moment) : Loc.Get("Never");

    public string StatusLabel => Model.Revoked
        ? Loc.Get("Revoked")
        : PendingApproval
            ? Loc.Get("PendingApproval")
            : Model.Online
                ? Loc.Get("Online")
                : Loc.Get("Offline");

    public Brush StatusBrush => Model.Revoked
        ? StatusBrushes.Danger
        : PendingApproval
            ? StatusBrushes.Warning
            : Model.Online
                ? StatusBrushes.Online
                : StatusBrushes.Muted;

    public Brush StatusWashBrush => Model.Revoked
        ? StatusBrushes.DangerWash
        : PendingApproval
            ? StatusBrushes.WarningWash
            : Model.Online
                ? StatusBrushes.OnlineWash
                : StatusBrushes.MutedWash;
}

/// <summary>Frozen status brushes, resolved once and shared.</summary>
public static class StatusBrushes
{
    public static readonly Brush Online = Freeze("#FF2ED573");
    public static readonly Brush OnlineWash = Freeze("#1F2ED573");
    public static readonly Brush Muted = Freeze("#FF6B6B78");
    public static readonly Brush MutedWash = Freeze("#14FFFFFF");
    public static readonly Brush Warning = Freeze("#FFF5A524");
    public static readonly Brush WarningWash = Freeze("#1FF5A524");
    public static readonly Brush Danger = Freeze("#FFF04747");
    public static readonly Brush DangerWash = Freeze("#1FF04747");

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
