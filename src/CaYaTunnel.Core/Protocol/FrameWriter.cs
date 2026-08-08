using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// Encodes frames onto a duplex stream. Safe for concurrent use: every multiplexed stream
/// writes through the same instance, so writes are serialised by an internal lock. Header and
/// payload go out in a single write so a frame never straddles two TLS records.
/// </summary>
public sealed class FrameWriter(Stream stream) : IDisposable
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public async ValueTask WriteAsync(
        FrameType type,
        uint streamId,
        ReadOnlyMemory<byte> payload = default,
        FrameFlags flags = FrameFlags.None,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > ProtocolConstants.MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"Payload of {payload.Length} bytes exceeds the {ProtocolConstants.MaxPayloadSize} byte frame limit.");
        }

        var total = ProtocolConstants.HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            buffer[0] = (byte)type;
            buffer[1] = (byte)flags;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(2, 4), streamId);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(6, 4), (uint)payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize));

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await _stream.WriteAsync(buffer.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Serialises <paramref name="value"/> as JSON and sends it as a single frame.</summary>
    public ValueTask WriteJsonAsync<T>(
        FrameType type,
        uint streamId,
        T value,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonProtocol.Options);
        return WriteAsync(type, streamId, json, FrameFlags.None, cancellationToken);
    }

    public ValueTask WriteWindowUpdateAsync(uint streamId, int credit, CancellationToken cancellationToken = default)
    {
        if (credit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(credit), "Window credit must be positive.");
        }

        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)credit);
        return WriteAsync(FrameType.StreamWindowUpdate, streamId, payload, FrameFlags.None, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }
}
