using CaYaTunnel.Core.Protocol;

namespace CaYaTunnel.Server.Routing;

/// <summary>
/// Replays bytes that were already read off a socket before handing the rest through.
/// <para>
/// Routing has to look at the first few bytes of a connection to learn where it should go — the
/// TLS SNI, the HTTP Host header, the Minecraft handshake. Those bytes still belong to the
/// protocol, so the tunnel has to receive them too. Wrapping the socket in this makes the peeked
/// prefix invisible to everything downstream.
/// </para>
/// </summary>
public sealed class PrefixedStream(Stream inner, ReadOnlyMemory<byte> prefix) : Stream, IHalfClosable
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private ReadOnlyMemory<byte> _prefix = prefix;

    public Stream Inner => _inner;

    /// <summary>
    /// Passes the half-close down to the socket underneath. Without this the wrapper would
    /// swallow the signal and a visitor waiting on an HTTP response would hang after the body
    /// had already arrived.
    /// </summary>
    public ValueTask CompleteWriteAsync(CancellationToken cancellationToken = default)
        => StreamPump.HalfCloseAsync(_inner);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_prefix.IsEmpty)
        {
            var take = Math.Min(_prefix.Length, buffer.Length);
            _prefix[..take].CopyTo(buffer);
            _prefix = _prefix[take..];
            return take;
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => _inner.Write(buffer, offset, count);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>Reads a prefix off a stream without consuming it from the caller's point of view.</summary>
public static class StreamPeeker
{
    /// <summary>
    /// Reads until <paramref name="isComplete"/> accepts what has arrived, the limit is hit, or
    /// the peer stops sending. Returns everything read so it can be replayed downstream.
    /// </summary>
    public static async Task<byte[]> PeekAsync(
        Stream stream,
        int maxBytes,
        Func<ReadOnlyMemory<byte>, bool> isComplete,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes];
        var total = 0;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (total < maxBytes)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(total), timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break; // slow client: route on what we have
            }

            if (read == 0)
            {
                break;
            }

            total += read;
            if (isComplete(buffer.AsMemory(0, total)))
            {
                break;
            }
        }

        return buffer[..total];
    }
}
