using System.Net;
using System.Net.Sockets;
using System.Text;
using CaYaTunnel.Client;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Core.Security;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Gateway;
using CaYaTunnel.Server.Registry;
using Xunit;

namespace CaYaTunnel.Tests;

/// <summary>
/// One open port carrying agent links and public traffic at once.
/// <para>
/// This is what makes a deployment behind a home router practical: everything that announces
/// where it is going can share a port, so only one has to be forwarded.
/// </para>
/// </summary>
public class SinglePortModeTests : IAsyncLifetime
{
    private string _dataDirectory = null!;
    private ServerConfig _config = null!;
    private TunnelRegistry _registry = null!;
    private TunnelServer _server = null!;
    private TunnelClient _client = null!;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "cayatunnel-single", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDirectory);

        var rangeStart = TestPorts.RangeStart();

        _config = new ServerConfig
        {
            ServerName = "Single Port Gateway",
            SinglePortMode = true,
            ControlPort = TestPorts.Free(),
            ControlBindAddress = "127.0.0.1",
            PublicHost = "127.0.0.1",
            BaseDomain = "tunnel.example.test",
            EnrollmentKey = EnrollmentKey.Generate(),
            TcpPortRangeStart = rangeStart,
            TcpPortRangeEnd = rangeStart + TestPorts.RangeSize,
            TlsCertificatePath = Path.Combine(_dataDirectory, "control.pfx"),
            PublicTlsCertificatePath = Path.Combine(_dataDirectory, "public.pfx"),
        };

        _registry = new TunnelRegistry(Path.Combine(_dataDirectory, "registry.json"));
        _server = new TunnelServer(_config, _registry, new GatewayLog());
        await _server.StartAsync();

        _client = new TunnelClient(
            new ClientSettingsStore(Path.Combine(_dataDirectory, "client.json")),
            new ClientSettings { DeviceName = "CAGAN-PC" },
            new ClientConnectionProfile("127.0.0.1", _config.ControlPort, _config.EnrollmentKey,
                _server.ControlCertificateFingerprint, "Single Port Gateway", Provisioned: true));

        _client.Start();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (_client.State != ClientState.Online && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder.
        }
    }

    [Fact]
    public void An_agent_connects_on_the_shared_port()
    {
        // The agent's TLS server name is what identifies it, so it is not mistaken for a browser.
        Assert.Equal(ClientState.Online, _client.State);
        Assert.Single(_registry.Devices);
    }

    [Fact]
    public async Task A_website_is_served_on_the_same_port_the_agent_connected_to()
    {
        await using var site = HttpEchoServer.Start("shared-port-site");

        var created = await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.HttpHost,
            Subdomain = "panel",
            TargetHost = "127.0.0.1",
            TargetPort = site.Port,
        });
        Assert.True(created.Ok, created.Error);

        // Same port number the agent is already using for its control link.
        var response = await HttpGetAsync(_config.ControlPort, "panel.tunnel.example.test");

        Assert.Contains("shared-port-site", response);
        Assert.Equal(ClientState.Online, _client.State);
    }

    [Fact]
    public async Task Minecraft_is_served_on_that_port_too()
    {
        await using var mc = EchoServer.Start();

        var created = await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.TcpHostAware,
            Subdomain = "survival",
            Protocol = HostAwareProtocols.MinecraftJava,
            TargetHost = "127.0.0.1",
            TargetPort = mc.Port,
        });
        Assert.True(created.Ok, created.Error);

        var handshake = ProtocolSnifferTests.BuildMinecraftHandshake("survival.tunnel.example.test", 25565, 767, 2);

        using var visitor = new TcpClient();
        await visitor.ConnectAsync(IPAddress.Loopback, _config.ControlPort).WaitAsync(TimeSpan.FromSeconds(15));

        var stream = visitor.GetStream();
        await stream.WriteAsync(handshake);
        await stream.FlushAsync();

        var buffer = new byte[handshake.Length];
        var total = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        Assert.Equal(handshake, buffer[..total]);
    }

    [Fact]
    public async Task Everything_shares_the_port_at_the_same_time()
    {
        await using var site = HttpEchoServer.Start("still-here");

        await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.HttpHost,
            Subdomain = "panel",
            TargetHost = "127.0.0.1",
            TargetPort = site.Port,
        });

        // Several visitors at once, while the agent link stays up on the same port throughout.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => HttpGetAsync(_config.ControlPort, "panel.tunnel.example.test")));

        Assert.All(responses, r => Assert.Contains("still-here", r));
        Assert.Equal(ClientState.Online, _client.State);
    }

    [Fact]
    public async Task The_gateway_binds_nothing_but_the_shared_port()
    {
        // Nothing should be listening on the conventional ports: that is the entire point, and
        // binding them anyway would defeat a deployment that only forwarded one.
        Assert.False(await IsListening(_config.HttpPort), "HTTP port should not be bound in single-port mode");
        Assert.False(await IsListening(_config.HttpsPort), "HTTPS port should not be bound in single-port mode");
        Assert.False(await IsListening(_config.MinecraftPort), "Minecraft port should not be bound in single-port mode");
        Assert.True(await IsListening(_config.ControlPort), "the shared port must be bound");
    }

    [Fact]
    public void A_port_tunnel_still_needs_a_port_of_its_own()
    {
        // Stated as a test because it is a real limit, not an oversight: a protocol that carries
        // no destination cannot be told apart from any other on a shared port.
        Assert.Single(_config.ReservedPorts());
        Assert.Contains(_config.ReservedPorts(), r => r.Name == "control port");
    }

    private static async Task<bool> IsListening(int port)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return probe.Connected;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<string> HttpGetAsync(int port, string host)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(15));

        var stream = client.GetStream();
        var request = $"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await reader.ReadToEndAsync(timeout.Token);
    }
}
