using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol.Messages;

namespace CaYaTunnel.Ui;

/// <summary>
/// A plausible registry used to render the UI for screenshots and to eyeball layout without
/// standing up a gateway. Mirrors the scenario in the README: two machines, a web app, a
/// Minecraft server and a service on another box on the LAN.
/// </summary>
public static class PreviewData
{
    public const string CaganDeviceId = "device-cagan";
    public const string TufDeviceId = "device-tuf";

    public static RegistrySnapshot Build() => new()
    {
        Server = new ServerInfo
        {
            ServerName = "CaYaDev Gateway",
            PublicHost = "203.0.113.42",
            BaseDomain = "tunnel.example.com",
            HttpPort = 80,
            HttpsPort = 443,
            MinecraftPort = 25565,
            TcpPortRangeStart = 32000,
            TcpPortRangeEnd = 32999,
            DnsAutomationEnabled = true,
        },
        Devices =
        [
            new DeviceInfo
            {
                Id = CaganDeviceId,
                Name = "CAGAN-PC",
                Online = true,
                RemoteAddress = "88.230.14.7",
                LocalAddresses = ["192.168.1.34"],
                OperatingSystem = "Windows 11 Pro",
                ClientVersion = "1.0.0",
                LatencyMs = 24,
                ConnectedAt = DateTimeOffset.UtcNow.AddHours(-6),
                LastSeenAt = DateTimeOffset.UtcNow,
            },
            new DeviceInfo
            {
                Id = TufDeviceId,
                Name = "TUF-A16",
                Online = true,
                RemoteAddress = "88.230.14.7",
                LocalAddresses = ["192.168.1.51"],
                OperatingSystem = "Windows 11 Pro",
                ClientVersion = "1.0.0",
                LatencyMs = 31,
                ConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-42),
                LastSeenAt = DateTimeOffset.UtcNow,
            },
        ],
        Tunnels =
        [
            new TunnelDefinition
            {
                Id = "t-panel",
                Name = "Panel",
                Kind = TunnelKind.HttpHost,
                DeviceId = CaganDeviceId,
                Hostname = "panel.tunnel.example.com",
                TargetHost = "127.0.0.1",
                TargetPort = 3000,
                BytesIn = 48_233_984,
                BytesOut = 1_204_233_984,
                TotalConnections = 1284,
                ActiveConnections = 3,
                LastActiveAt = DateTimeOffset.UtcNow.AddSeconds(-14),
            },
            new TunnelDefinition
            {
                Id = "t-mc",
                Name = "Minecraft",
                Kind = TunnelKind.TcpHostAware,
                Protocol = HostAwareProtocols.MinecraftJava,
                DeviceId = CaganDeviceId,
                Hostname = "minecraft.tunnel.example.com",
                PublicPort = 25565,
                TargetHost = "127.0.0.1",
                TargetPort = 25565,
                BytesIn = 802_233_984,
                BytesOut = 2_940_233_984,
                TotalConnections = 96,
                ActiveConnections = 5,
                LastActiveAt = DateTimeOffset.UtcNow.AddSeconds(-2),
            },
            new TunnelDefinition
            {
                Id = "t-game",
                Name = "Valheim",
                Kind = TunnelKind.PortForward,
                Transports = TransportProtocols.Both,
                DeviceId = CaganDeviceId,
                PublicPort = 2456,
                TargetHost = "127.0.0.1",
                TargetPort = 2456,
                BytesIn = 412_233_984,
                BytesOut = 688_233_984,
                TotalConnections = 233,
                ActiveConnections = 2,
                LastActiveAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            },
            new TunnelDefinition
            {
                Id = "t-lan",
                Name = "Home Assistant",
                Kind = TunnelKind.PortForward,
                DeviceId = CaganDeviceId,
                PublicPort = 32001,
                TargetHost = "192.168.1.20",
                TargetPort = 8123,
                BytesIn = 12_233_984,
                BytesOut = 88_233_984,
                TotalConnections = 42,
                LastActiveAt = DateTimeOffset.UtcNow.AddMinutes(-9),
            },
            new TunnelDefinition
            {
                Id = "t-dev",
                Name = "Vite dev server",
                Kind = TunnelKind.HttpHost,
                HttpAccess = HttpAccess.RedirectToHttps,
                DeviceId = TufDeviceId,
                Hostname = "dev.tunnel.example.com",
                TargetHost = "127.0.0.1",
                TargetPort = 5173,
                BytesIn = 2_233_984,
                BytesOut = 18_233_984,
                TotalConnections = 17,
                ActiveConnections = 1,
                LastActiveAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
            new TunnelDefinition
            {
                Id = "t-off",
                Name = "Old API",
                Kind = TunnelKind.PortForward,
                DeviceId = TufDeviceId,
                PublicPort = 32004,
                TargetHost = "127.0.0.1",
                TargetPort = 9000,
                Enabled = false,
                BytesIn = 233_984,
                BytesOut = 1_233_984,
                TotalConnections = 4,
                LastActiveAt = DateTimeOffset.UtcNow.AddDays(-2),
            },
        ],
    };
}
