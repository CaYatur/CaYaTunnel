using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;
using Xunit;

namespace CaYaTunnel.Tests;

/// <summary>
/// Exercises the whole transport: two links over a real socket pair, streams dialled through to
/// a real TCP service. If these pass, bytes genuinely move end to end.
/// </summary>
public class MuxLinkTests : IAsyncLifetime
{
    private LoopbackChannel _channel = null!;
    private MuxLink _gateway = null!;
    private MuxLink _agent = null!;
    private Task _gatewayLoop = null!;
    private Task _agentLoop = null!;

    public async Task InitializeAsync()
    {
        _channel = await LoopbackChannel.CreateAsync();
        _gateway = new MuxLink(_channel.Left, MuxRole.Server);
        _agent = new MuxLink(_channel.Right, MuxRole.Client);

        _gatewayLoop = _gateway.RunAsync();
        _agentLoop = _agent.RunAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.DisposeAsync();
        await _agent.DisposeAsync();
        await Task.WhenAny(Task.WhenAll(_gatewayLoop, _agentLoop), Task.Delay(2000));
        await _channel.DisposeAsync();
    }

    [Fact]
    public async Task Bytes_travel_from_the_gateway_through_the_agent_to_a_real_service()
    {
        await using var echo = EchoServer.Start();
        _agent.TargetDialer = DialLoopback;

        await using var stream = await _gateway.OpenStreamAsync(new StreamOpenMessage
        {
            TunnelId = "t1",
            TargetHost = "127.0.0.1",
            TargetPort = echo.Port,
            RemoteEndpoint = "203.0.113.9:51000",
        });

        var payload = "hello from the public internet"u8.ToArray();
        await stream.WriteAsync(payload);
        await stream.CompleteWriteAsync();

        var received = await ReadToEndAsync(stream, payload.Length);

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task A_transfer_larger_than_the_window_completes_intact()
    {
        await using var echo = EchoServer.Start();
        _agent.TargetDialer = DialLoopback;

        // Comfortably more than InitialStreamWindow, so this only passes if WindowUpdate
        // frames are actually flowing back and unblocking the sender.
        var payload = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        Assert.True(payload.Length > ProtocolConstants.InitialStreamWindow * 4);

        await using var stream = await _gateway.OpenStreamAsync(new StreamOpenMessage
        {
            TunnelId = "t-big",
            TargetHost = "127.0.0.1",
            TargetPort = echo.Port,
        });

        var reader = ReadToEndAsync(stream, payload.Length);

        await stream.WriteAsync(payload);
        await stream.CompleteWriteAsync();

        var received = await reader.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(payload.Length, received.Length);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(received));
    }

    [Fact]
    public async Task Concurrent_streams_do_not_block_each_other()
    {
        await using var echo = EchoServer.Start();
        _agent.TargetDialer = DialLoopback;

        var big = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);

        // A large transfer that will spend most of its life waiting on window credit...
        var slow = Task.Run(async () =>
        {
            await using var stream = await _gateway.OpenStreamAsync(new StreamOpenMessage
            {
                TunnelId = "bulk",
                TargetHost = "127.0.0.1",
                TargetPort = echo.Port,
            });

            var reader = ReadToEndAsync(stream, big.Length);
            await stream.WriteAsync(big);
            await stream.CompleteWriteAsync();
            return (await reader).Length;
        });

        // ...must not stop a small one sharing the same link from finishing quickly.
        for (var i = 0; i < 5; i++)
        {
            await using var quick = await _gateway.OpenStreamAsync(new StreamOpenMessage
            {
                TunnelId = "interactive",
                TargetHost = "127.0.0.1",
                TargetPort = echo.Port,
            });

            var ping = "ping"u8.ToArray();
            await quick.WriteAsync(ping);
            await quick.CompleteWriteAsync();

            var pong = await ReadToEndAsync(quick, ping.Length).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(ping, pong);
        }

        Assert.Equal(big.Length, await slow.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task A_target_the_agent_cannot_reach_surfaces_as_TargetUnreachable()
    {
        _agent.TargetDialer = DialLoopback;

        // Port 1 on loopback: nothing is listening, so the dial is refused.
        var ex = await Assert.ThrowsAsync<TargetUnreachableException>(async () =>
            await _gateway.OpenStreamAsync(new StreamOpenMessage
            {
                TunnelId = "dead",
                TargetHost = "127.0.0.1",
                TargetPort = 1,
            }));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task Control_messages_cross_the_link_in_both_directions()
    {
        var toAgent = new TaskCompletionSource<ControlEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toGateway = new TaskCompletionSource<ControlEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agent.ControlHandler = (envelope, _) =>
        {
            toAgent.TrySetResult(envelope);
            return Task.CompletedTask;
        };
        _gateway.ControlHandler = (envelope, _) =>
        {
            toGateway.TrySetResult(envelope);
            return Task.CompletedTask;
        };

        await _gateway.SendControlAsync(ControlEnvelope.Create(
            ControlMessageTypes.Notice,
            new NoticeMessage { Severity = "warning", Title = "Tunnel remotely deleted", SubjectId = "t1" }));

        await _agent.SendControlAsync(ControlEnvelope.Create(
            ControlMessageTypes.CreateTunnel,
            new CreateTunnelRequest { TargetPort = 3000 },
            "req-9"));

        var notice = (await toAgent.Task.WaitAsync(TimeSpan.FromSeconds(10))).ReadRequired<NoticeMessage>();
        Assert.Equal("Tunnel remotely deleted", notice.Title);
        Assert.Equal("t1", notice.SubjectId);

        var request = await toGateway.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("req-9", request.Id);
        Assert.Equal(3000, request.ReadRequired<CreateTunnelRequest>().TargetPort);
    }

    [Fact]
    public async Task Stream_ids_from_the_two_ends_never_collide()
    {
        await using var echo = EchoServer.Start();
        _agent.TargetDialer = DialLoopback;
        _gateway.TargetDialer = DialLoopback;

        await using var fromGateway = await _gateway.OpenStreamAsync(new StreamOpenMessage
        {
            TunnelId = "a",
            TargetHost = "127.0.0.1",
            TargetPort = echo.Port,
        });

        await using var fromAgent = await _agent.OpenStreamAsync(new StreamOpenMessage
        {
            TunnelId = "b",
            TargetHost = "127.0.0.1",
            TargetPort = echo.Port,
        });

        Assert.Equal(1u, fromGateway.Id % 2); // server end allocates odd ids
        Assert.Equal(0u, fromAgent.Id % 2);   // client end allocates even ids
        Assert.NotEqual(fromGateway.Id, fromAgent.Id);
    }

    [Fact]
    public async Task Losing_the_link_faults_the_streams_it_carried_rather_than_faking_an_eof()
    {
        await using var echo = EchoServer.Start();
        _agent.TargetDialer = DialLoopback;

        var stream = await _gateway.OpenStreamAsync(new StreamOpenMessage
        {
            TunnelId = "t1",
            TargetHost = "127.0.0.1",
            TargetPort = echo.Port,
        });

        // Simulate the network dropping underneath us.
        _channel.Right.Dispose();

        // A dead link must not read as a clean end of stream: a half-delivered HTTP response
        // that looks complete is far worse than a visible error, so readers get an IOException.
        var buffer = new byte[64];
        await Assert.ThrowsAsync<IOException>(async () =>
            await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        // Writers see it too, instead of blocking forever on window credit that will never come.
        await Assert.ThrowsAnyAsync<IOException>(async () => await stream.WriteAsync(new byte[16]));
    }

    [Fact]
    public async Task A_deliberate_remote_close_reads_as_a_clean_eof()
    {
        await using var echo = EchoServer.Start();
        _agent.TargetDialer = DialLoopback;

        await using var stream = await _gateway.OpenStreamAsync(new StreamOpenMessage
        {
            TunnelId = "t1",
            TargetHost = "127.0.0.1",
            TargetPort = echo.Port,
        });

        // Echo server closes its side once we half-close, which travels back as FIN.
        await stream.CompleteWriteAsync();

        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, read);
    }

    private static async Task<Stream> DialLoopback(StreamOpenMessage request, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(request.TargetHost), request.TargetPort, cancellationToken);
        return client.GetStream();
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream, int expected)
    {
        var buffer = new byte[expected];
        var total = 0;
        while (total < expected)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer[..total];
    }
}
