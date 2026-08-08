using System.Buffers;
using System.IO.Pipelines;

namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// One logical connection riding the multiplexed link, exposed as a <see cref="Stream"/> so it
/// can be handed straight to <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/> against
/// a real socket.
/// </summary>
public sealed class MuxStream : Stream
{
    private readonly MuxLink _link;
    private readonly Pipe _inbound;
    private readonly SendWindow _sendWindow;
    private readonly CancellationTokenSource _lifetime = new();

    private int _unackedBytes;
    private bool _readCompleted;
    private bool _writeCompleted;
    private bool _closeSent;
    private int _disposed;

    /// <summary>
    /// Non-null once the link died under this stream. Tearing a stream down cancels
    /// <see cref="_lifetime"/> *and* completes the inbound pipe with an exception, and those two
    /// signals race. Recording the cause here makes the outcome deterministic: a faulted stream
    /// always throws, a locally disposed one always reads as EOF.
    /// </summary>
    private volatile Exception? _fault;

    internal MuxStream(MuxLink link, uint id, string tunnelId, int initialSendWindow)
    {
        _link = link;
        Id = id;
        TunnelId = tunnelId;
        _sendWindow = new SendWindow(initialSendWindow);
        _inbound = new Pipe(new PipeOptions(
            // Sized well above the advertised window: credits are the real bound, this is only a
            // backstop so a misbehaving peer that ignores the window cannot exhaust memory.
            pauseWriterThreshold: ProtocolConstants.InitialStreamWindow * 4,
            resumeWriterThreshold: ProtocolConstants.InitialStreamWindow));
    }

    public uint Id { get; }

    public string TunnelId { get; }

    /// <summary>Where this stream's traffic came from, for the live connection list.</summary>
    public string? RemoteEndpoint { get; internal set; }

    public long BytesRead { get; private set; }

    public long BytesWritten { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    // ---- Reading ---------------------------------------------------------

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);

        while (true)
        {
            ReadResult result;
            try
            {
                result = await _inbound.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Torn down locally: a clean EOF. Torn down by a dead link: an error, because a
                // truncated response must never be mistaken for a complete one.
                if (_fault is { } fault)
                {
                    throw AsIoException(fault);
                }

                return 0;
            }

            var sequence = result.Buffer;
            if (sequence.Length > 0)
            {
                var take = (int)Math.Min(sequence.Length, buffer.Length);
                sequence.Slice(0, take).CopyTo(buffer.Span);
                _inbound.Reader.AdvanceTo(sequence.GetPosition(take));

                BytesRead += take;
                await ReleaseWindowAsync(take, cancellationToken).ConfigureAwait(false);
                return take;
            }

            _inbound.Reader.AdvanceTo(sequence.Start, sequence.End);

            if (result.IsCompleted)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Hands credit back to the peer once the application has consumed bytes — batched so a
    /// chatty stream does not spend the link on window updates.
    /// </summary>
    private async ValueTask ReleaseWindowAsync(int consumed, CancellationToken cancellationToken)
    {
        _unackedBytes += consumed;
        if (_unackedBytes < ProtocolConstants.WindowUpdateThreshold)
        {
            return;
        }

        var credit = _unackedBytes;
        _unackedBytes = 0;
        try
        {
            await _link.SendWindowUpdateAsync(Id, credit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Link is gone; the read loop will tear this stream down on its own.
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    // ---- Writing ---------------------------------------------------------

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_writeCompleted, this);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);

        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            var want = Math.Min(remaining.Length, ProtocolConstants.DataChunkSize);
            var granted = await _sendWindow.AcquireAsync(want, linked.Token).ConfigureAwait(false);

            var chunk = remaining[..granted];
            await _link.SendDataAsync(Id, chunk, FrameFlags.None, linked.Token).ConfigureAwait(false);

            BytesWritten += granted;
            remaining = remaining[granted..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Signals that no more data will be written, without tearing down the read direction.
    /// Needed by protocols where the client half-closes and waits for the rest of the response.
    /// </summary>
    public async ValueTask CompleteWriteAsync(CancellationToken cancellationToken = default)
    {
        if (_writeCompleted)
        {
            return;
        }

        _writeCompleted = true;
        try
        {
            await _link.SendDataAsync(Id, ReadOnlyMemory<byte>.Empty, FrameFlags.Fin, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Peer already gone.
        }
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    // ---- Called by the link's read loop ----------------------------------

    /// <summary>
    /// Called from the link's read loop with the payload of a StreamData frame.
    /// <para>
    /// The flush is awaited on purpose. In normal operation it completes synchronously, because
    /// flow-control credits keep the buffered amount well under the pipe's pause threshold. If a
    /// peer ignores the window, awaiting here stalls the read loop instead of growing the buffer
    /// without bound — backpressure is the safe failure mode, running out of memory is not.
    /// </para>
    /// </summary>
    internal async ValueTask OnDataAsync(ReadOnlyMemory<byte> payload, bool fin, CancellationToken cancellationToken)
    {
        if (_readCompleted)
        {
            return;
        }

        if (!payload.IsEmpty)
        {
            _inbound.Writer.Write(payload.Span);
            await _inbound.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (fin)
        {
            CompleteInbound(null);
        }
    }

    internal void OnWindowUpdate(int credit) => _sendWindow.Add(credit);

    /// <summary>
    /// The peer closed this stream. Completing the inbound pipe (rather than cancelling the
    /// stream's lifetime) is deliberate: bytes already buffered here must still be readable.
    /// Cancelling would abandon them, silently truncating the tail of a transfer.
    /// </summary>
    internal void OnRemoteClose(string? reason)
    {
        CompleteInbound(null);
        _closeSent = true; // peer initiated: no need to echo a close back
        _sendWindow.Fault(new IOException(reason is null
            ? "Remote peer closed the stream."
            : $"Remote peer closed the stream: {reason}"));
    }

    /// <summary>
    /// The whole link died. Same reasoning as <see cref="OnRemoteClose"/>: the reader drains
    /// whatever already arrived and only then sees the error, so no delivered byte is lost.
    /// </summary>
    internal void OnLinkFailure(Exception exception)
    {
        var fault = AsIoException(exception);
        _fault = fault;
        CompleteInbound(fault);
        _closeSent = true; // link is gone; nothing to send a close over
        _sendWindow.Fault(fault);
    }

    private void CompleteInbound(Exception? exception)
    {
        if (_readCompleted)
        {
            return;
        }

        _readCompleted = true;
        _inbound.Writer.Complete(exception);
    }

    /// <summary>
    /// Normalises every teardown cause to <see cref="IOException"/> so callers can catch one
    /// type regardless of whether the link died on a socket error, a protocol violation or a
    /// disposal, and never see a raw <see cref="ObjectDisposedException"/> from the internals.
    /// </summary>
    private static IOException AsIoException(Exception exception) => exception as IOException
        ?? new IOException(exception.Message, exception);

    // ---- Teardown --------------------------------------------------------

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (!_closeSent)
        {
            _closeSent = true;
            try
            {
                await _link.SendStreamCloseAsync(Id, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Link already down.
            }
        }

        _link.RemoveStream(Id);
        CompleteInbound(null);
        _inbound.Reader.Complete();
        _sendWindow.Fault(new ObjectDisposedException(nameof(MuxStream)));

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override string ToString() => $"MuxStream #{Id} (tunnel {TunnelId})";
}
