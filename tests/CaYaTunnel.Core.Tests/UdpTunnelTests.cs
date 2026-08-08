using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CaYaTunnel.Client;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Core.Security;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Gateway;
using CaYaTunnel.Server.Registry;
using Xunit;

namespace CaYaTunnel.Tests;

public class DatagramFramingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(1400)]
    [InlineData(DatagramFraming.MaxDatagramSize)]
    public async Task A_datagram_survives_the_round_trip_at_its_exact_length(int length)
    {
        var datagram = RandomNumberGenerator.GetBytes(length);

        await using var channel = await LoopbackChannel.CreateAsync();
        await DatagramFraming.WriteAsync(channel.Left, datagram, default);
        await channel.Left.FlushAsync();

        var received = await DatagramFraming.ReadAsync(channel.Right, default);

        Assert.NotNull(received);
        Assert.Equal(datagram, received);
    }

    [Fact]
    public async Task Boundaries_are_preserved_across_a_byte_stream()
    {
        // The whole reason UDP cannot simply reuse the stream pump: two 100-byte datagrams must
        // not arrive as one 200-byte read.
        byte[][] datagrams =
        [
            Encoding.UTF8.GetBytes(new string('a', 100)),
            Encoding.UTF8.GetBytes(new string('b', 100)),
            [],
            Encoding.UTF8.GetBytes("last"),
        ];

        await using var channel = await LoopbackChannel.CreateAsync();

        foreach (var datagram in datagrams)
        {
            await DatagramFraming.WriteAsync(channel.Left, datagram, default);
        }

        await channel.Left.FlushAsync();

        foreach (var expected in datagrams)
        {
            var received = await DatagramFraming.ReadAsync(channel.Right, default);
            Assert.NotNull(received);
            Assert.Equal(expected, received);
        }
    }

    [Fact]
    public async Task End_of_stream_reads_as_null_rather_than_an_empty_datagram()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        channel.Left.Dispose();

        Assert.Null(await DatagramFraming.ReadAsync(channel.Right, default));
    }

    [Fact]
    public async Task A_truncated_datagram_reads_as_null_rather_than_a_short_one()
    {
        await using var channel = await LoopbackChannel.CreateAsync();

        // Announce 100 bytes, deliver 10, hang up.
        await channel.Left.WriteAsync(new byte[] { 0x00, 0x64 });
        await channel.Left.WriteAsync(new byte[10]);
        await channel.Left.FlushAsync();
        channel.Left.Dispose();

        Assert.Null(await DatagramFraming.ReadAsync(channel.Right, default));
    }
}

/// <summary>
/// UDP end to end: a real UDP service behind an agent, reached through a real public UDP port.
/// </summary>
public class UdpTunnelTests : IAsyncLifetime
{
    private string _dataDirectory = null!;
    private ServerConfig _config = null!;
    private TunnelRegistry _registry = null!;
    private TunnelServer _server = null!;
    private TunnelClient _client = null!;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "cayatunnel-udp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDirectory);

        var rangeStart = TestPorts.RangeStart();

        _config = new ServerConfig
        {
            ServerName = "UDP Test Gateway",

            // Dedicated public ports, so the separate-listener mode is what is under test here.
            SinglePortMode = false,
            ControlPort = TestPorts.Free(),
            ControlBindAddress = "127.0.0.1",
            PublicHost = "127.0.0.1",
            BaseDomain = "",
            EnableHttpRouter = false,
            EnableMinecraftRouter = false,
            EnrollmentKey = EnrollmentKey.Generate(),
            TcpPortRangeStart = rangeStart,
            TcpPortRangeEnd = rangeStart + TestPorts.RangeSize,
            TlsCertificatePath = Path.Combine(_dataDirectory, "control.pfx"),
            PublicTlsCertificatePath = Path.Combine(_dataDirectory, "public.pfx"),
        };

        _registry = new TunnelRegistry(Path.Combine(_dataDirectory, "registry.json"));
        _server = new TunnelServer(_config, _registry, new GatewayLog());
        await _server.StartAsync();

        var store = new ClientSettingsStore(Path.Combine(_dataDirectory, "client.json"));
        _client = new TunnelClient(
            store,
            new ClientSettings { DeviceName = "CAGAN-PC" },
            new ClientConnectionProfile("127.0.0.1", _config.ControlPort, _config.EnrollmentKey,
                _server.ControlCertificateFingerprint, "UDP Test Gateway", Provisioned: true));

        _client.Start();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (_client.State != ClientState.Online && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(ClientState.Online, _client.State);
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
            // Temp folder; not worth failing a test over.
        }
    }

    [Fact]
    public async Task A_udp_tunnel_carries_datagrams_to_a_local_service_and_back()
    {
        await using var service = UdpEchoServer.Start();

        var created = await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Name = "Game server",
            Kind = TunnelKind.PortForward,
            Transports = TransportProtocols.Udp,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        Assert.True(created.Ok, created.Error);

        var tunnel = Assert.Single(_registry.Tunnels);
        Assert.True(tunnel.ServesUdp);
        Assert.False(tunnel.ServesTcp);

        using var visitor = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Loopback, tunnel.PublicPort!.Value);

        var payload = Encoding.UTF8.GetBytes("ping through the tunnel");
        await visitor.SendAsync(payload, endpoint);

        var reply = await ReceiveWithTimeoutAsync(visitor, TimeSpan.FromSeconds(20));

        Assert.Equal(payload, reply);
    }

    [Fact]
    public async Task Datagram_boundaries_survive_the_tunnel()
    {
        await using var service = UdpEchoServer.Start();

        var created = await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.PortForward,
            Transports = TransportProtocols.Udp,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });
        Assert.True(created.Ok, created.Error);

        var port = _registry.Tunnels.Single().PublicPort!.Value;
        using var visitor = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        // Three separate datagrams must come back as three, not as one merged blob.
        byte[][] sent =
        [
            Encoding.UTF8.GetBytes(new string('a', 200)),
            Encoding.UTF8.GetBytes(new string('b', 50)),
            Encoding.UTF8.GetBytes(new string('c', 1000)),
        ];

        foreach (var datagram in sent)
        {
            await visitor.SendAsync(datagram, endpoint);
            var reply = await ReceiveWithTimeoutAsync(visitor, TimeSpan.FromSeconds(20));
            Assert.Equal(datagram.Length, reply.Length);
            Assert.Equal(datagram, reply);
        }
    }

    [Fact]
    public async Task A_tunnel_can_serve_tcp_and_udp_on_the_same_public_port()
    {
        // One service answering both transports on one port number — exactly the shape of a game
        // server, and the reason a tunnel has to cover both rather than forcing two tunnels.
        await using var service = DualEchoServer.Start();

        var created = await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Name = "Both",
            Kind = TunnelKind.PortForward,
            Transports = TransportProtocols.Both,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });
        Assert.True(created.Ok, created.Error);

        var tunnel = _registry.Tunnels.Single();
        Assert.True(tunnel.ServesTcp);
        Assert.True(tunnel.ServesUdp);
        Assert.Equal("TCP + UDP", tunnel.TransportLabel);

        var port = tunnel.PublicPort!.Value;

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(15));
            var stream = tcp.GetStream();
            var message = Encoding.UTF8.GetBytes("over tcp");
            await stream.WriteAsync(message);
            await stream.FlushAsync();

            var buffer = new byte[message.Length];
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

            Assert.Equal(message, buffer[..total]);
        }

        using var visitor = new UdpClient();
        var payload = Encoding.UTF8.GetBytes("over udp");
        await visitor.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port));

        var reply = await ReceiveWithTimeoutAsync(visitor, TimeSpan.FromSeconds(20));
        Assert.Equal(payload, reply);
    }

    [Fact]
    public async Task Adding_udp_to_an_existing_tcp_tunnel_rebinds_the_port()
    {
        await using var service = DualEchoServer.Start();

        var created = await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.PortForward,
            Transports = TransportProtocols.Tcp,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });
        Assert.True(created.Ok, created.Error);

        var tunnel = _registry.Tunnels.Single();
        var port = tunnel.PublicPort!.Value;

        // Switching transports has to release and rebind the port. If the old listener were
        // still holding it, the new one would fail to bind and UDP would silently never work.
        var updated = await _client.UpdateTunnelAsync(new UpdateTunnelRequest
        {
            TunnelId = tunnel.Id,
            Transports = TransportProtocols.Both,
        });
        Assert.True(updated.Ok, updated.Error);

        using var visitor = new UdpClient();
        var payload = Encoding.UTF8.GetBytes("udp after rebind");
        await visitor.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port));

        var reply = await ReceiveWithTimeoutAsync(visitor, TimeSpan.FromSeconds(20));
        Assert.Equal(payload, reply);
    }

    [Fact]
    public async Task Two_visitors_get_their_own_flows_rather_than_each_others_replies()
    {
        await using var service = UdpEchoServer.Start(prefixWithSender: true);

        var created = await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.PortForward,
            Transports = TransportProtocols.Udp,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });
        Assert.True(created.Ok, created.Error);

        var endpoint = new IPEndPoint(IPAddress.Loopback, _registry.Tunnels.Single().PublicPort!.Value);

        using var first = new UdpClient();
        using var second = new UdpClient();

        await first.SendAsync("first"u8.ToArray(), endpoint);
        await second.SendAsync("second"u8.ToArray(), endpoint);

        var firstReply = Encoding.UTF8.GetString(await ReceiveWithTimeoutAsync(first, TimeSpan.FromSeconds(20)));
        var secondReply = Encoding.UTF8.GetString(await ReceiveWithTimeoutAsync(second, TimeSpan.FromSeconds(20)));

        Assert.EndsWith("first", firstReply);
        Assert.EndsWith("second", secondReply);
    }

    [Fact]
    public async Task Traffic_counters_include_udp()
    {
        await using var service = UdpEchoServer.Start();

        await _client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.PortForward,
            Transports = TransportProtocols.Udp,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        var tunnel = _registry.Tunnels.Single();
        using var visitor = new UdpClient();
        var payload = Encoding.UTF8.GetBytes(new string('x', 512));

        await visitor.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, tunnel.PublicPort!.Value));
        await ReceiveWithTimeoutAsync(visitor, TimeSpan.FromSeconds(20));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (_registry.FindTunnel(tunnel.Id)!.ActiveConnections == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var updated = _registry.FindTunnel(tunnel.Id)!;
        Assert.True(updated.TotalConnections >= 1, "the UDP flow should count as a connection");
    }

    private static async Task<byte[]> ReceiveWithTimeoutAsync(UdpClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var result = await client.ReceiveAsync(cts.Token);
        return result.Buffer;
    }

}

/// <summary>
/// Echoes on TCP and UDP from the same port number, the way a game server does. Binding both is
/// legal because they are different protocols.
/// </summary>
internal sealed class DualEchoServer : IAsyncDisposable
{
    private readonly TcpListener _tcp;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _tcpLoop;
    private readonly Task _udpLoop;

    private DualEchoServer(TcpListener tcp, UdpClient udp, int port)
    {
        _tcp = tcp;
        _udp = udp;
        Port = port;
        _tcpLoop = TcpLoopAsync(_cts.Token);
        _udpLoop = UdpLoopAsync(_cts.Token);
    }

    public int Port { get; }

    public static DualEchoServer Start()
    {
        // A number both protocols can actually bind. Windows reserves whole UDP ranges for
        // Hyper-V and WinNAT, and binding one gives access-denied rather than address-in-use —
        // so a port that TCP accepted says nothing about whether UDP will.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;

            try
            {
                var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                return new DualEchoServer(tcp, udp, port);
            }
            catch (SocketException)
            {
                tcp.Stop();
            }
        }

        throw new InvalidOperationException("Could not find a port free for both TCP and UDP.");
    }

    private async Task TcpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            await stream.CopyToAsync(stream, cancellationToken);
                            client.Client.Shutdown(SocketShutdown.Send);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                        {
                            // Visitor left.
                        }
                    }
                }, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private async Task UdpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await _udp.ReceiveAsync(cancellationToken);
                await _udp.SendAsync(received.Buffer, received.RemoteEndPoint, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _tcp.Stop();
        _udp.Dispose();

        try
        {
            await Task.WhenAll(_tcpLoop, _udpLoop);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cts.Dispose();
    }
}

/// <summary>A UDP service that echoes datagrams, standing in for a game server.</summary>
internal sealed class UdpEchoServer : IAsyncDisposable
{
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private UdpEchoServer(UdpClient socket, bool prefixWithSender)
    {
        _socket = socket;
        Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        _loop = RunAsync(prefixWithSender, _cts.Token);
    }

    public int Port { get; }

    public static UdpEchoServer Start(bool prefixWithSender = false)
    {
        var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return new UdpEchoServer(socket, prefixWithSender);
    }

    private async Task RunAsync(bool prefixWithSender, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(cancellationToken);

                var reply = prefixWithSender
                    ? Encoding.UTF8.GetBytes($"[{received.RemoteEndPoint.Port}]").Concat(received.Buffer).ToArray()
                    : received.Buffer;

                await _socket.SendAsync(reply, received.RemoteEndPoint, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _socket.Dispose();

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cts.Dispose();
    }
}
