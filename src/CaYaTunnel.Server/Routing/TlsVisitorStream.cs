using System.Net.Security;
using CaYaTunnel.Core.Protocol;

namespace CaYaTunnel.Server.Routing;

/// <summary>
/// A TLS-terminated visitor connection that can still be half-closed.
/// <para>
/// <see cref="SslStream"/> alone is not enough: shutting it down sends close_notify but leaves
/// the TCP send side open, and it does not expose the stream underneath, so the pump has no way
/// to reach the socket. Holding both together lets a finished response actually end.
/// </para>
/// </summary>
public sealed class TlsVisitorStream(SslStream tls, Stream underlying) : Stream, IHalfClosable
{
    private readonly SslStream _tls = tls ?? throw new ArgumentNullException(nameof(tls));
    private readonly Stream _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _tls.ReadAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _tls.Read(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _tls.WriteAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _tls.Write(buffer, offset, count);

    public override void Flush() => _tls.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _tls.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public async ValueTask CompleteWriteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _tls.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Peer already gone, or the handshake never finished.
        }

        await StreamPump.HalfCloseAsync(_underlying).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tls.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _tls.DisposeAsync();
}
