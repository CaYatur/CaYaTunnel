using System.Buffers.Binary;

namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// A bidirectional datagram endpoint — one end of a UDP conversation.
/// <para>
/// UDP cannot reuse <see cref="Stream"/>: a stream loses message boundaries, and for UDP the
/// boundaries <em>are</em> the protocol. Two 100-byte datagrams and one 200-byte datagram mean
/// different things to the application on the other end.
/// </para>
/// </summary>
public interface IDatagramChannel : IAsyncDisposable
{
    /// <summary>Waits for the next datagram, or returns null when the channel is finished.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);

    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default);
}

/// <summary>
/// Carries datagrams across a multiplexed stream by length-prefixing each one, so the boundaries
/// survive the reliable byte stream in the middle.
/// </summary>
public static class DatagramFraming
{
    /// <summary>Largest payload a single UDP datagram can carry over IPv4.</summary>
    public const int MaxDatagramSize = 65507;

    private const int PrefixLength = 2;

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
        if (datagram.Length > MaxDatagramSize)
        {
            // Cannot happen from a real socket; guards a caller bug rather than the network.
            throw new ArgumentOutOfRangeException(nameof(datagram),
                $"Datagram of {datagram.Length} bytes exceeds the {MaxDatagramSize} byte maximum.");
        }

        var buffer = new byte[PrefixLength + datagram.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)datagram.Length);
        datagram.Span.CopyTo(buffer.AsSpan(PrefixLength));

        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one framed datagram, or returns null at end of stream.</summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[PrefixLength];
        if (!await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length == 0)
        {
            return [];
        }

        var payload = new byte[length];
        return await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false)
            ? payload
            : null;
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}

/// <summary>
/// Pumps datagrams between a multiplexed stream and a real UDP endpoint. The UDP counterpart of
/// <see cref="StreamPump"/>, used unchanged at both ends of the tunnel.
/// </summary>
public static class DatagramPump
{
    /// <summary>
    /// Runs until either side finishes. Returns bytes moved
    /// (<c>ToChannel</c> = mux -&gt; UDP, <c>FromChannel</c> = UDP -&gt; mux).
    /// </summary>
    public static async Task<(long ToChannel, long FromChannel)> RunAsync(
        MuxStream mux,
        IDatagramChannel channel,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var toChannel = PumpToChannelAsync(mux, channel, linked.Token);
        var fromChannel = PumpFromChannelAsync(mux, channel, linked.Token);

        // UDP has no close, so a flow ends when either direction stops — usually the idle
        // timeout on the listener side.
        await Task.WhenAny(toChannel, fromChannel).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(toChannel, fromChannel).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // Both sides tear down through cancellation; that is the normal ending.
        }

        return (Result(toChannel), Result(fromChannel));
    }

    private static long Result(Task<long> task) => task.IsCompletedSuccessfully ? task.Result : 0;

    private static async Task<long> PumpToChannelAsync(MuxStream mux, IDatagramChannel channel, CancellationToken cancellationToken)
    {
        long total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? datagram;
            try
            {
                datagram = await DatagramFraming.ReadAsync(mux, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                break;
            }

            if (datagram is null)
            {
                break;
            }

            try
            {
                await channel.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                break;
            }

            total += datagram.Length;
        }

        return total;
    }

    private static async Task<long> PumpFromChannelAsync(MuxStream mux, IDatagramChannel channel, CancellationToken cancellationToken)
    {
        long total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? datagram;
            try
            {
                datagram = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                break;
            }

            if (datagram is null)
            {
                break;
            }

            try
            {
                await DatagramFraming.WriteAsync(mux, datagram, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                break;
            }

            total += datagram.Length;
        }

        return total;
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or OperationCanceledException;
}
