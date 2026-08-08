using System.Windows.Media;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Client.App.ViewModels;

/// <summary>One tunnel as the list renders it, with the device name already resolved.</summary>
public sealed class TunnelRow(TunnelDefinition tunnel, string deviceName, bool deviceOnline, bool isThisDevice, ServerInfo server)
    : ViewModelBase
{
    public TunnelDefinition Model { get; } = tunnel;

    public string Id => Model.Id;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Endpoint : Model.Name;

    public string DeviceName { get; } = deviceName;

    public bool IsThisDevice { get; } = isThisDevice;

    public bool DeviceOnline { get; } = deviceOnline;

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

    public string LastActive => Model.LastActiveAt is { } moment
        ? RelativeTimeConverter.Format(moment)
        : Loc.Get("Never");

    public bool Enabled => Model.Enabled;

    /// <summary>
    /// Three states rather than two: a tunnel can be perfectly configured and still not work
    /// because the machine carrying it is asleep, and that is the single most common reason an
    /// endpoint stops responding.
    /// </summary>
    public string StatusLabel => !Model.Enabled
        ? Loc.Get("TunnelDisabled")
        : !DeviceOnline
            ? Loc.Get("DeviceOffline")
            : Loc.Get("Online");

    public Brush StatusBrush => !Model.Enabled || !DeviceOnline
        ? (Brush)ThemeBrushes.Muted
        : ThemeBrushes.Online;

    public Brush StatusWashBrush => !Model.Enabled
        ? ThemeBrushes.MutedWash
        : !DeviceOnline
            ? ThemeBrushes.WarningWash
            : ThemeBrushes.OnlineWash;
}

/// <summary>One device as the list renders it.</summary>
public sealed class DeviceRow(DeviceInfo device, int tunnelCount, bool isThisDevice) : ViewModelBase
{
    public DeviceInfo Model { get; } = device;

    public string Id => Model.Id;

    public string Name => Model.Name;

    public bool IsThisDevice { get; } = isThisDevice;

    public bool Online => Model.Online;

    public bool Revoked => Model.Revoked;

    public bool PendingApproval => !Model.Approved && !Model.Revoked;

    public int TunnelCount { get; } = tunnelCount;

    public string RemoteAddress => Model.RemoteAddress ?? "—";

    public string LocalAddresses => Model.LocalAddresses.Count == 0
        ? "—"
        : string.Join(", ", Model.LocalAddresses);

    public string OperatingSystem => Model.OperatingSystem ?? Loc.Get("Unknown");

    public string ClientVersion => Model.ClientVersion ?? "—";

    public string Latency => Model.LatencyMs is { } ms ? $"{ms} ms" : "—";

    public string LastSeen => Model.LastSeenAt is { } moment
        ? RelativeTimeConverter.Format(moment)
        : Loc.Get("Never");

    public string StatusLabel => Model.Revoked
        ? Loc.Get("Revoked")
        : PendingApproval
            ? Loc.Get("PendingApproval")
            : Model.Online
                ? Loc.Get("Online")
                : Loc.Get("Offline");

    public Brush StatusBrush => Model.Revoked
        ? ThemeBrushes.Danger
        : PendingApproval
            ? ThemeBrushes.Warning
            : Model.Online
                ? ThemeBrushes.Online
                : ThemeBrushes.Muted;

    public Brush StatusWashBrush => Model.Revoked
        ? ThemeBrushes.DangerWash
        : PendingApproval
            ? ThemeBrushes.WarningWash
            : Model.Online
                ? ThemeBrushes.OnlineWash
                : ThemeBrushes.MutedWash;
}

/// <summary>
/// Status colours resolved once. Rows expose brushes directly rather than exposing a state enum
/// plus a converter per colour, which would be three times the XAML for the same result.
/// </summary>
public static class ThemeBrushes
{
    public static readonly Brush Online = Freeze("#FF2ED573");
    public static readonly Brush OnlineWash = Freeze("#1F2ED573");
    public static readonly Brush Muted = Freeze("#FF6B6B78");
    public static readonly Brush MutedWash = Freeze("#14FFFFFF");
    public static readonly Brush Warning = Freeze("#FFF5A524");
    public static readonly Brush WarningWash = Freeze("#1FF5A524");
    public static readonly Brush Danger = Freeze("#FFF04747");
    public static readonly Brush DangerWash = Freeze("#1FF04747");
    public static readonly Brush Accent = Freeze("#FFE8232A");

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze(); // shared across threads and never mutated
        return brush;
    }
}
