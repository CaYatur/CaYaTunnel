using CaYaTunnel.Core.Models;
using CaYaTunnel.Server.Configuration;
using Xunit;

namespace CaYaTunnel.Tests;

/// <summary>
/// The address shown to the user has to be the address that works.
/// <para>
/// A tunnel served on a shared port once displayed as plain https://host, which is copyable,
/// looks correct, and quietly goes to whatever else is on 443 on that machine.
/// </para>
/// </summary>
public class EndpointDisplayTests
{
    [Fact]
    public void A_website_on_the_shared_port_shows_that_port()
    {
        var server = SinglePort(48771).ToServerInfo(false);
        var tunnel = Website("panel.tunnel.example.com");

        Assert.Equal("https://panel.tunnel.example.com:48771", tunnel.PublicEndpoint(server));
    }

    [Fact]
    public void A_website_on_the_standard_port_shows_no_port()
    {
        // 443 is implied by https, and spelling it out would be noise.
        var server = Separate(httpsPort: 443).ToServerInfo(false);
        var tunnel = Website("panel.tunnel.example.com");

        Assert.Equal("https://panel.tunnel.example.com", tunnel.PublicEndpoint(server));
    }

    [Fact]
    public void An_http_only_website_is_measured_against_port_80()
    {
        var standard = Separate(httpPort: 80).ToServerInfo(false);
        var moved = Separate(httpPort: 8080).ToServerInfo(false);

        var tunnel = Website("plain.tunnel.example.com");
        tunnel.HttpAccess = HttpAccess.HttpOnly;

        Assert.Equal("http://plain.tunnel.example.com", tunnel.PublicEndpoint(standard));
        Assert.Equal("http://plain.tunnel.example.com:8080", tunnel.PublicEndpoint(moved));
    }

    [Fact]
    public void Single_port_mode_reports_the_shared_port_for_every_protocol()
    {
        var info = SinglePort(48771).ToServerInfo(false);

        Assert.Equal(48771, info.HttpPort);
        Assert.Equal(48771, info.HttpsPort);
        Assert.Equal(48771, info.MinecraftPort);
    }

    [Fact]
    public void Separate_listeners_report_their_own_ports()
    {
        var config = Separate(httpsPort: 443);
        config.MinecraftPort = 25565;

        var info = config.ToServerInfo(false);

        Assert.Equal(443, info.HttpsPort);
        Assert.Equal(25565, info.MinecraftPort);
    }

    private static ServerConfig SinglePort(int controlPort) => new()
    {
        SinglePortMode = true,
        ControlPort = controlPort,
        PublicHost = "203.0.113.10",
        BaseDomain = "tunnel.example.com",
    };

    private static ServerConfig Separate(int httpsPort = 443, int httpPort = 80) => new()
    {
        SinglePortMode = false,
        ControlPort = 48771,
        HttpPort = httpPort,
        HttpsPort = httpsPort,
        PublicHost = "203.0.113.10",
        BaseDomain = "tunnel.example.com",
    };

    private static TunnelDefinition Website(string hostname) => new()
    {
        Id = "t1",
        Name = "Panel",
        Kind = TunnelKind.HttpHost,
        DeviceId = "d1",
        Hostname = hostname,
        TargetHost = "127.0.0.1",
        TargetPort = 3000,
    };
}
