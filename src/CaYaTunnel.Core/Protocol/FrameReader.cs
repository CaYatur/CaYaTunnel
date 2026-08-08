using System.Buffers;
using System.Buffers.Binary;

namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// Decodes frames off a duplex stream. Single-consumer: exactly one task may call
/// <see cref="ReadAsync"/> at a time (the link's read loop).
/// </summary>
public sealed class FrameReader(Stream stream)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly byte[] _header = new byte[ProtocolConstants.HeaderSize];

    /// <summary>
    /// Reads the next frame, or returns <c>null</c> when the peer closed the connection
    /// cleanly on a frame boundary. A close mid-frame throws <see cref="ProtocolException"/>.
    /// </summary>
    public async ValueTask<Frame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var got = await ReadAtMostAsync(_header, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null; // clean EOF on a frame boundary
        }

        if (got < ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException(
                $"Connection closed mid-header ({got}/{ProtocolConstants.HeaderSize} bytes).");
        }

        var type = (FrameType)_header[0];
        var flags = (FrameFlags)_header[1];
        var streamId = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(2, 4));
        var length = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(6, 4));

        if (length > ProtocolConstants.MaxPayloadSize)
        {
            throw new ProtocolException(
                $"Frame payload of {length} bytes exceeds the {ProtocolConstants.MaxPayloadSize} byte limit.");
        }

        if (length == 0)
        {
            return new Frame(type, flags, streamId, null, 0);
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            await _stream.ReadExactlyAsync(buffer.AsMemory(0, (int)length), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw new ProtocolException("Connection closed mid-payload.", ex);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return new Frame(type, flags, streamId, buffer, (int)length);
    }

    /// <summary>
    /// Fills <paramref name="destination"/>, stopping early only at end of stream. Returns how
    /// many bytes were actually read so the caller can tell clean EOF (0) from truncation.
    /// </summary>
    private async ValueTask<int> ReadAtMostAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await _stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
