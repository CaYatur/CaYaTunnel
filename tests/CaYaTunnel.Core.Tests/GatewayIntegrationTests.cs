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
/// The real thing: a gateway and one or more agents, over real sockets, with real TLS and real
/// authentication. These are the tests that would catch a regression a user would actually hit.
/// </summary>
public class GatewayIntegrationTests : IAsyncLifetime
{
    private string _dataDirectory = null!;
    private ServerConfig _config = null!;
    private TunnelRegistry _registry = null!;
    private TunnelServer _server = null!;
    private readonly List<TunnelClient> _clients = [];

    /// <summary>
    /// Captured so a failing assertion can show what the gateway and agents actually said.
    /// A bare "expected Online, got Reconnecting" tells you nothing about why.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _diagnostics = new();

    private string Diagnostics => string.Join(Environment.NewLine, _diagnostics);

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "cayatunnel-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDirectory);

        // The range is picked first so every fixed port can be kept clear of it; the gateway
        // refuses to start if one of its own listeners sits inside the span it hands out.
        var rangeStart = TestPorts.RangeStart();

        _config = new ServerConfig
        {
            ServerName = "Test Gateway",
            ControlPort = TestPorts.Free(),
            ControlBindAddress = "127.0.0.1",
            PublicHost = "127.0.0.1",
            BaseDomain = "tunnel.example.test",
            EnableHttpRouter = true,
            HttpPort = TestPorts.Free(),
            HttpsPort = TestPorts.Free(),
            EnableMinecraftRouter = true,
            MinecraftPort = TestPorts.Free(),
            EnrollmentKey = EnrollmentKey.Generate(),
            KeyGeneration = 1,
            TcpPortRangeStart = rangeStart,
            TcpPortRangeEnd = rangeStart + TestPorts.RangeSize,
            TlsCertificatePath = Path.Combine(_dataDirectory, "control.pfx"),
            PublicTlsCertificatePath = Path.Combine(_dataDirectory, "public.pfx"),
        };

        _registry = new TunnelRegistry(Path.Combine(_dataDirectory, "registry.json"));

        var log = new GatewayLog { VerboseEnabled = true };
        log.Entry += entry => _diagnostics.Enqueue($"server {entry}");

        _server = new TunnelServer(_config, _registry, log);

        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        await _server.DisposeAsync();

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Windows sometimes holds the pfx briefly; the temp folder is disposable anyway.
        }
    }

    // ---- Connecting ---------------------------------------------------------

    [Fact]
    public async Task A_client_with_the_right_key_comes_online_and_is_listed_as_a_device()
    {
        var client = await ConnectClientAsync("CAGAN-PC");

        Assert.Equal(ClientState.Online, client.State);
        Assert.NotEmpty(client.DeviceId);

        var device = Assert.Single(_registry.Devices);
        Assert.Equal("CAGAN-PC", device.Name);
        Assert.True(device.Online);
    }

    [Fact]
    public async Task A_client_with_the_wrong_key_is_refused_and_stops_retrying()
    {
        var client = CreateClient("INTRUDER", key: EnrollmentKey.Generate());
        _clients.Add(client);
        client.Start();

        await WaitForAsync(() => client.State == ClientState.Unauthorized, TimeSpan.FromSeconds(20));

        Assert.Equal(ClientState.Unauthorized, client.State);
        Assert.Equal(AuthFailureReason.InvalidKey, client.RefusalReason);
        Assert.Empty(_registry.Devices);
    }

    [Fact]
    public async Task Rotating_the_server_key_locks_out_a_build_that_carries_the_old_one()
    {
        var oldKey = _config.EnrollmentKey;

        var store = new ServerConfigStore(Path.Combine(_dataDirectory, "config.json"));
        store.RotateEnrollmentKey(_config);

        Assert.NotEqual(oldKey, _config.EnrollmentKey);
        Assert.Equal(2, _config.KeyGeneration);

        var stale = CreateClient("OLD-BUILD", key: oldKey);
        _clients.Add(stale);
        stale.Start();

        await WaitForAsync(() => stale.State == ClientState.Unauthorized, TimeSpan.FromSeconds(20));

        // The distinct reason is what lets the client say "ask for a new build" instead of
        // "check your key", which is the difference between a useful message and a support call.
        Assert.Equal(AuthFailureReason.KeyRotated, stale.RefusalReason);

        var fresh = await ConnectClientAsync("NEW-BUILD", key: _config.EnrollmentKey);
        Assert.Equal(ClientState.Online, fresh.State);
    }

    [Fact]
    public async Task A_client_pinning_the_wrong_certificate_refuses_to_connect()
    {
        var client = CreateClient("SUSPICIOUS", fingerprint: new string('a', 64));
        _clients.Add(client);
        client.Start();

        // It must never reach Online — a mismatched pin is exactly the case pinning exists for.
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.NotEqual(ClientState.Online, client.State);
        Assert.Empty(_registry.Devices);
    }

    // ---- Carrying traffic ------------------------------------------------------

    [Fact]
    public async Task A_dedicated_port_tunnel_carries_real_bytes_to_a_local_service()
    {
        await using var service = EchoServer.Start();
        var client = await ConnectClientAsync("CAGAN-PC");

        var result = await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Name = "Minecraft",
            Kind = TunnelKind.PortForward,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        Assert.True(result.Ok, result.Error);

        var tunnel = Assert.Single(_registry.Tunnels);
        Assert.NotNull(tunnel.PublicPort);
        Assert.InRange(tunnel.PublicPort!.Value, _config.TcpPortRangeStart, _config.TcpPortRangeEnd);

        var echoed = await RoundTripAsync(tunnel.PublicPort.Value, "hello through the tunnel");
        Assert.Equal("hello through the tunnel", echoed);
    }

    [Fact]
    public async Task An_http_tunnel_routes_by_host_header_on_a_shared_port()
    {
        await using var siteA = HttpEchoServer.Start("site-a");
        await using var siteB = HttpEchoServer.Start("site-b");

        var client = await ConnectClientAsync("CAGAN-PC");

        var a = await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.HttpHost,
            Subdomain = "panel",
            TargetHost = "127.0.0.1",
            TargetPort = siteA.Port,
        });
        var b = await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.HttpHost,
            Subdomain = "dev",
            TargetHost = "127.0.0.1",
            TargetPort = siteB.Port,
        });

        Assert.True(a.Ok, a.Error);
        Assert.True(b.Ok, b.Error);

        // Both tunnels share one public port and are told apart purely by the Host header.
        var fromA = await HttpGetAsync(_config.HttpPort, "panel.tunnel.example.test");
        var fromB = await HttpGetAsync(_config.HttpPort, "dev.tunnel.example.test");

        Assert.Contains("site-a", fromA);
        Assert.Contains("site-b", fromB);
    }

    [Fact]
    public async Task An_unknown_hostname_gets_a_404_rather_than_a_dead_connection()
    {
        await ConnectClientAsync("CAGAN-PC");

        var response = await HttpGetAsync(_config.HttpPort, "nothing-here.tunnel.example.test");

        Assert.Contains("404", response);
    }

    [Fact]
    public async Task Minecraft_tunnels_share_one_port_and_split_on_the_handshake_hostname()
    {
        await using var serverA = EchoServer.Start();
        await using var serverB = EchoServer.Start();

        var client = await ConnectClientAsync("CAGAN-PC");

        await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.TcpHostAware,
            Subdomain = "survival",
            Protocol = HostAwareProtocols.MinecraftJava,
            TargetHost = "127.0.0.1",
            TargetPort = serverA.Port,
        });

        await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.TcpHostAware,
            Subdomain = "creative",
            Protocol = HostAwareProtocols.MinecraftJava,
            TargetHost = "127.0.0.1",
            TargetPort = serverB.Port,
        });

        // Both handshakes hit the same public port; only the address inside them differs.
        var toA = ProtocolSnifferTests.BuildMinecraftHandshake("survival.tunnel.example.test", 25565, 767, 2);
        var toB = ProtocolSnifferTests.BuildMinecraftHandshake("creative.tunnel.example.test", 25565, 767, 2);

        var echoedA = await SendRawAsync(_config.MinecraftPort, toA);
        var echoedB = await SendRawAsync(_config.MinecraftPort, toB);

        // The echo services hand the handshake straight back, proving each reached its own target.
        Assert.Equal(toA, echoedA);
        Assert.Equal(toB, echoedB);
    }

    [Fact]
    public async Task A_tunnel_whose_device_is_offline_reports_502_instead_of_hanging()
    {
        var client = await ConnectClientAsync("CAGAN-PC");

        // Port 1 on loopback: registered, but nothing is listening behind it.
        var created = await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.HttpHost,
            Subdomain = "broken",
            TargetHost = "127.0.0.1",
            TargetPort = 1,
        });
        Assert.True(created.Ok, created.Error);

        var response = await HttpGetAsync(_config.HttpPort, "broken.tunnel.example.test");

        Assert.Contains("502", response);
    }

    // ---- Realtime behaviour ------------------------------------------------------

    [Fact]
    public async Task A_tunnel_created_on_one_device_appears_on_another_without_polling()
    {
        await using var service = EchoServer.Start();

        var cagan = await ConnectClientAsync("CAGAN-PC");
        var tuf = await ConnectClientAsync("TUF-A16");

        var seenByTuf = new TaskCompletionSource<TunnelDefinition>(TaskCreationOptions.RunContinuationsAsynchronously);
        tuf.SnapshotChanged += snapshot =>
        {
            var match = snapshot.Tunnels.FirstOrDefault(t => t.Name == "shared-view");
            if (match is not null)
            {
                seenByTuf.TrySetResult(match);
            }
        };

        await cagan.CreateTunnelAsync(new CreateTunnelRequest
        {
            Name = "shared-view",
            Kind = TunnelKind.PortForward,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        // Two seconds is generous for a push; a polling implementation would routinely miss it.
        var tunnel = await seenByTuf.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("shared-view", tunnel.Name);
        Assert.Equal(cagan.DeviceId, tunnel.DeviceId);
    }

    [Fact]
    public async Task Deleting_another_devices_tunnel_warns_the_device_that_owned_it()
    {
        await using var service = EchoServer.Start();

        var cagan = await ConnectClientAsync("CAGAN-PC");
        var tuf = await ConnectClientAsync("TUF-A16");

        var notified = new TaskCompletionSource<NoticeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        cagan.NoticeReceived += notice => notified.TrySetResult(notice);

        var created = await cagan.CreateTunnelAsync(new CreateTunnelRequest
        {
            Name = "doomed",
            Kind = TunnelKind.PortForward,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });
        Assert.True(created.Ok, created.Error);

        var tunnelId = _registry.Tunnels.Single(t => t.Name == "doomed").Id;

        // TUF-A16 removes a tunnel that belongs to CAGAN-PC.
        var deleted = await tuf.DeleteTunnelAsync(tunnelId);
        Assert.True(deleted.Ok, deleted.Error);

        var notice = await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Tunnel removed remotely", notice.Title);
        Assert.Equal(tunnelId, notice.SubjectId);
        Assert.Contains("TUF-A16", notice.Body);
    }

    [Fact]
    public async Task Revoking_a_device_disconnects_it_immediately()
    {
        var client = await ConnectClientAsync("CAGAN-PC");
        var deviceId = client.DeviceId;

        await _server.SetDeviceRevokedAsync(deviceId, revoked: true, "operator");

        await WaitForAsync(() => client.State is ClientState.Unauthorized or ClientState.Reconnecting,
            TimeSpan.FromSeconds(15));

        Assert.NotEqual(ClientState.Online, client.State);
    }

    [Fact]
    public async Task A_port_that_is_already_taken_is_rejected_with_a_readable_message()
    {
        await using var service = EchoServer.Start();
        var client = await ConnectClientAsync("CAGAN-PC");

        var first = await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.PortForward,
            PublicPort = _config.TcpPortRangeStart,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });
        Assert.True(first.Ok, first.Error);

        var second = await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.PortForward,
            PublicPort = _config.TcpPortRangeStart,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        Assert.False(second.Ok);
        Assert.Contains("already taken", second.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_duplicate_hostname_is_rejected_with_a_readable_message()
    {
        await using var service = EchoServer.Start();
        var client = await ConnectClientAsync("CAGAN-PC");

        await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.HttpHost,
            Subdomain = "panel",
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        var duplicate = await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Kind = TunnelKind.HttpHost,
            Subdomain = "panel",
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        Assert.False(duplicate.Ok);
        Assert.Contains("already in use", duplicate.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Traffic_counters_reflect_what_actually_moved()
    {
        await using var service = EchoServer.Start();
        var client = await ConnectClientAsync("CAGAN-PC");

        await client.CreateTunnelAsync(new CreateTunnelRequest
        {
            Name = "counted",
            Kind = TunnelKind.PortForward,
            TargetHost = "127.0.0.1",
            TargetPort = service.Port,
        });

        var tunnel = _registry.Tunnels.Single();
        var payload = new string('x', 4096);
        await RoundTripAsync(tunnel.PublicPort!.Value, payload);

        await WaitForAsync(() => _registry.FindTunnel(tunnel.Id)!.BytesIn >= payload.Length, TimeSpan.FromSeconds(10));

        var updated = _registry.FindTunnel(tunnel.Id)!;
        Assert.True(updated.BytesIn >= payload.Length, $"expected >= {payload.Length} in, got {updated.BytesIn}");
        Assert.True(updated.BytesOut >= payload.Length, $"expected >= {payload.Length} out, got {updated.BytesOut}");
        Assert.Equal(0, updated.ActiveConnections);
        Assert.True(updated.TotalConnections >= 1);
    }

    // ---- Helpers ---------------------------------------------------------------

    private async Task<TunnelClient> ConnectClientAsync(string deviceName, string? key = null)
    {
        var client = CreateClient(deviceName, key);
        _clients.Add(client);
        client.Start();

        await WaitForAsync(() => client.State == ClientState.Online, TimeSpan.FromSeconds(30));

        Assert.True(
            client.State == ClientState.Online,
            $"'{deviceName}' never came online (state {client.State}, status '{client.StatusMessage}').{Environment.NewLine}{Diagnostics}");

        return client;
    }

    private TunnelClient CreateClient(string deviceName, string? key = null, string? fingerprint = null)
    {
        var store = new ClientSettingsStore(Path.Combine(_dataDirectory, $"client-{deviceName}-{Guid.NewGuid():n}.json"));
        var settings = new ClientSettings { DeviceName = deviceName };

        var profile = new ClientConnectionProfile(
            "127.0.0.1",
            _config.ControlPort,
            key ?? _config.EnrollmentKey,
            fingerprint ?? _server.ControlCertificateFingerprint,
            "Test Gateway",
            Provisioned: true);

        var client = new TunnelClient(store, settings, profile);
        client.LogMessage += message => _diagnostics.Enqueue($"{deviceName} {message}");
        client.StateChanged += (state, message) => _diagnostics.Enqueue($"{deviceName} -> {state}: {message}");
        return client;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static async Task<string> RoundTripAsync(int port, string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        var echoed = await SendRawAsync(port, payload);
        return Encoding.UTF8.GetString(echoed);
    }

    private static async Task<byte[]> SendRawAsync(int port, byte[] payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(15));

        var stream = client.GetStream();
        await stream.WriteAsync(payload);
        await stream.FlushAsync();

        var buffer = new byte[payload.Length];
        var total = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (total < payload.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer[..total];
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

/// <summary>A minimal HTTP server that identifies itself, standing in for a local web app.</summary>
internal sealed class HttpEchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private HttpEchoServer(TcpListener listener, string identity)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _loop = AcceptLoopAsync(identity, _cts.Token);
    }

    public int Port { get; }

    public static HttpEchoServer Start(string identity)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new HttpEchoServer(listener, identity);
    }

    private async Task AcceptLoopAsync(string identity, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            // One read is enough: the tests only ever send a small request head.
                            _ = await stream.ReadAsync(buffer, cancellationToken);

                            var body = $"hello from {identity}";
                            var response = "HTTP/1.1 200 OK\r\n"
                                + "Content-Type: text/plain\r\n"
                                + $"Content-Length: {body.Length}\r\n"
                                + "Connection: close\r\n\r\n"
                                + body;

                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
                            await stream.FlushAsync(cancellationToken);
                            client.Client.Shutdown(SocketShutdown.Send);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                        {
                            // Client vanished.
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

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
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
