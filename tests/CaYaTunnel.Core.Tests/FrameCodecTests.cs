using System.Buffers.Binary;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;
using Xunit;

namespace CaYaTunnel.Tests;

public class FrameCodecTests
{
    [Theory]
    [InlineData(FrameType.StreamData, 1u, 0)]
    [InlineData(FrameType.StreamData, 7u, 1)]
    [InlineData(FrameType.StreamData, 4294967295u, 4096)]
    [InlineData(FrameType.Control, 0u, 100)]
    [InlineData(FrameType.StreamOpen, 12345u, ProtocolConstants.DataChunkSize)]
    public async Task Frame_survives_a_write_read_round_trip(FrameType type, uint streamId, int payloadLength)
    {
        var payload = new byte[payloadLength];
        Random.Shared.NextBytes(payload);

        await using var channel = await LoopbackChannel.CreateAsync();
        var writer = new FrameWriter(channel.Left);
        var reader = new FrameReader(channel.Right);

        await writer.WriteAsync(type, streamId, payload, FrameFlags.Fin);

        var frame = await reader.ReadAsync();

        Assert.NotNull(frame);
        using var received = frame.Value;
        Assert.Equal(type, received.Type);
        Assert.Equal(streamId, received.StreamId);
        Assert.True(received.HasFin);
        Assert.Equal(payload, received.PayloadSpan.ToArray());
    }

    [Fact]
    public async Task Back_to_back_frames_are_decoded_in_order()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var writer = new FrameWriter(channel.Left);
        var reader = new FrameReader(channel.Right);

        for (var i = 0; i < 50; i++)
        {
            await writer.WriteAsync(FrameType.StreamData, (uint)(i + 1), BitConverter.GetBytes(i));
        }

        for (var i = 0; i < 50; i++)
        {
            var frame = await reader.ReadAsync();
            Assert.NotNull(frame);
            using var received = frame.Value;
            Assert.Equal((uint)(i + 1), received.StreamId);
            Assert.Equal(i, BitConverter.ToInt32(received.PayloadSpan));
        }
    }

    [Fact]
    public async Task Clean_close_on_a_frame_boundary_reads_as_null()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var writer = new FrameWriter(channel.Left);
        var reader = new FrameReader(channel.Right);

        await writer.WriteAsync(FrameType.Ping, 0, new byte[8]);
        channel.Left.Dispose();

        var first = await reader.ReadAsync();
        Assert.NotNull(first);
        first.Value.Dispose();

        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Close_mid_header_is_a_protocol_error()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var reader = new FrameReader(channel.Right);

        // Half a header, then hang up.
        await channel.Left.WriteAsync(new byte[] { (byte)FrameType.StreamData, 0, 0, 0 });
        await channel.Left.FlushAsync();
        channel.Left.Dispose();

        await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public async Task Close_mid_payload_is_a_protocol_error()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var reader = new FrameReader(channel.Right);

        var header = new byte[ProtocolConstants.HeaderSize];
        header[0] = (byte)FrameType.StreamData;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2, 4), 5u);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), 1000u);

        await channel.Left.WriteAsync(header);
        await channel.Left.WriteAsync(new byte[10]); // promised 1000, delivered 10
        await channel.Left.FlushAsync();
        channel.Left.Dispose();

        await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public async Task Payload_larger_than_the_limit_is_rejected_without_allocating_it()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var reader = new FrameReader(channel.Right);

        var header = new byte[ProtocolConstants.HeaderSize];
        header[0] = (byte)FrameType.StreamData;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), uint.MaxValue);

        await channel.Left.WriteAsync(header);
        await channel.Left.FlushAsync();

        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync());
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Writer_refuses_a_payload_over_the_frame_limit()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var writer = new FrameWriter(channel.Left);

        var tooBig = new byte[ProtocolConstants.MaxPayloadSize + 1];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await writer.WriteAsync(FrameType.StreamData, 1, tooBig));
    }

    [Fact]
    public async Task Concurrent_writers_never_interleave_a_frame()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var writer = new FrameWriter(channel.Left);
        var reader = new FrameReader(channel.Right);

        const int streams = 8;
        const int perStream = 40;

        var readBack = Task.Run(async () =>
        {
            var seen = new Dictionary<uint, int>();
            for (var i = 0; i < streams * perStream; i++)
            {
                var frame = await reader.ReadAsync();
                Assert.NotNull(frame);
                using var received = frame.Value;

                // Every frame must arrive whole: payload is the stream id repeated 512 times.
                Assert.Equal(512, received.PayloadSpan.Length);
                foreach (var b in received.PayloadSpan)
                {
                    Assert.Equal((byte)received.StreamId, b);
                }

                seen[received.StreamId] = seen.GetValueOrDefault(received.StreamId) + 1;
            }

            return seen;
        });

        await Parallel.ForEachAsync(
            Enumerable.Range(1, streams),
            async (id, ct) =>
            {
                var payload = new byte[512];
                Array.Fill(payload, (byte)id);
                for (var i = 0; i < perStream; i++)
                {
                    await writer.WriteAsync(FrameType.StreamData, (uint)id, payload, FrameFlags.None, ct);
                }
            });

        var counts = await readBack.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(streams, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(perStream, count));
    }

    [Fact]
    public async Task Json_frames_round_trip_through_the_envelope()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var writer = new FrameWriter(channel.Left);
        var reader = new FrameReader(channel.Right);

        var request = new CreateTunnelRequest
        {
            Name = "Minecraft",
            Kind = Core.Models.TunnelKind.PortForward,
            TargetHost = "192.168.1.20",
            TargetPort = 25565,
        };

        await writer.WriteJsonAsync(FrameType.Control, 0, ControlEnvelope.Create(ControlMessageTypes.CreateTunnel, request, "req-1"));

        var frame = await reader.ReadAsync();
        Assert.NotNull(frame);
        using var received = frame.Value;

        var envelope = JsonProtocol.DeserializeRequired<ControlEnvelope>(received.PayloadSpan);
        Assert.Equal(ControlMessageTypes.CreateTunnel, envelope.Type);
        Assert.Equal("req-1", envelope.Id);

        var body = envelope.ReadRequired<CreateTunnelRequest>();
        Assert.Equal("Minecraft", body.Name);
        Assert.Equal(Core.Models.TunnelKind.PortForward, body.Kind);
        Assert.Equal("192.168.1.20", body.TargetHost);
        Assert.Equal(25565, body.TargetPort);
    }
}
