using System.Buffers;
using System.Globalization;
using System.Text;
using CaYaTunnel.Core.Protocol;

namespace CaYaTunnel.Server.Routing;

/// <summary>
/// Rewrites the <c>Host</c> header of every request passing through, leaving everything else
/// byte for byte the same.
/// <para>
/// Many local-only services check the Host header and refuse anything that is not localhost —
/// it is how they defend against DNS rebinding. Tunnelled traffic arrives carrying the public
/// hostname, so those services reject it, usually with a 400 that says nothing useful.
/// Presenting the address the service expects makes it answer normally.
/// </para>
/// <para>
/// Requests are rewritten one after another rather than only the first, because a keep-alive
/// connection carries many and fixing only the first produces a page that half works. After an
/// <c>Upgrade</c> the bytes stop being HTTP, so from that point everything is passed through
/// untouched.
/// </para>
/// </summary>
public sealed class HostRewritingStream(Stream inner, string replacementHost) : Stream, IHalfClosable
{
    /// <summary>A request head larger than this is not rewritten; it is passed through as-is.</summary>
    private const int MaxHeadSize = 32 * 1024;

    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly string _replacementHost = replacementHost;

    private readonly MemoryStream _pending = new();
    private long _pendingOffset;

    private Mode _mode = Mode.ExpectingHead;
    private long _bodyRemaining;
    private readonly List<byte> _head = [];

    private enum Mode
    {
        ExpectingHead,
        Body,
        Chunked,

        /// <summary>The connection stopped being HTTP; never look at it again.</summary>
        Passthrough,
    }

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
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Anything already rewritten goes out first.
        if (TryDrainPending(buffer.Span, out var drained))
        {
            return drained;
        }

        switch (_mode)
        {
            case Mode.Passthrough:
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            case Mode.Body:
                {
                    var take = (int)Math.Min(_bodyRemaining, buffer.Length);
                    var read = await _inner.ReadAsync(buffer[..take], cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        return 0;
                    }

                    _bodyRemaining -= read;
                    if (_bodyRemaining == 0)
                    {
                        _mode = Mode.ExpectingHead;
                    }

                    return read;
                }

            case Mode.Chunked:
                return await ReadChunkedAsync(buffer, cancellationToken).ConfigureAwait(false);

            default:
                return await ReadHeadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Accumulates a request head, rewrites it, and queues it for the caller.</summary>
    private async ValueTask<int> ReadHeadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        _head.Clear();
        var single = new byte[1];

        while (true)
        {
            var read = await _inner.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // Connection ended mid-head. Forward whatever arrived so the peer sees the same
                // truncation it would have without this in the way.
                if (_head.Count == 0)
                {
                    return 0;
                }

                _mode = Mode.Passthrough;
                Queue([.. _head]);
                return TryDrainPending(buffer.Span, out var flushed) ? flushed : 0;
            }

            _head.Add(single[0]);

            if (EndsWithHeaderTerminator(_head))
            {
                break;
            }

            if (_head.Count >= MaxHeadSize)
            {
                // Not something worth parsing. Passing it through unchanged is safer than
                // guessing at it.
                _mode = Mode.Passthrough;
                Queue([.. _head]);
                return TryDrainPending(buffer.Span, out var flushed) ? flushed : 0;
            }
        }

        var rewritten = RewriteHead(Encoding.ASCII.GetString([.. _head]));
        Queue(Encoding.ASCII.GetBytes(rewritten));

        return TryDrainPending(buffer.Span, out var served) ? served : 0;
    }

    /// <summary>
    /// Replaces the Host header and works out where this request's body ends, so the next one can
    /// be rewritten too.
    /// </summary>
    private string RewriteHead(string head)
    {
        var lines = head.Split("\r\n");
        var output = new StringBuilder(head.Length + 32);

        var contentLength = 0L;
        var chunked = false;
        var upgrade = false;

        // The head always ends with a blank line, so splitting leaves one empty element past it.
        // Emitting that too would add a stray CRLF, which shifts the body by two bytes and
        // corrupts every request that has one.
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i];

            if (i == 0 || line.Length == 0)
            {
                output.Append(line).Append("\r\n");
                continue;
            }

            var separator = line.IndexOf(':');
            var name = separator > 0 ? line[..separator].Trim() : string.Empty;

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("Host: ").Append(_replacementHost).Append("\r\n");
                continue;
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                contentLength = parsed;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && line[(separator + 1)..].Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = true;
            }

            output.Append(line).Append("\r\n");
        }

        // An upgrade turns the connection into something else — WebSocket, most often — so this
        // is the last request that can be understood.
        _mode = upgrade ? Mode.Passthrough
            : chunked ? Mode.Chunked
            : contentLength > 0 ? Mode.Body
            : Mode.ExpectingHead;

        _bodyRemaining = contentLength;
        return output.ToString();
    }

    /// <summary>
    /// Walks a chunked body so the request after it is still found. Every byte is passed through
    /// exactly as it arrived; only the boundaries are tracked.
    /// </summary>
    private async ValueTask<int> ReadChunkedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var collected = new List<byte>();
        var single = new byte[1];

        while (true)
        {
            // Chunk size line.
            var line = new List<byte>();
            while (true)
            {
                var read = await _inner.ReadAsync(single, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    _mode = Mode.Passthrough;
                    collected.AddRange(line);
                    return Serve(collected, buffer.Span);
                }

                line.Add(single[0]);
                if (line.Count >= 2 && line[^2] == (byte)'\r' && line[^1] == (byte)'\n')
                {
                    break;
                }

                if (line.Count > 64)
                {
                    _mode = Mode.Passthrough;
                    collected.AddRange(line);
                    return Serve(collected, buffer.Span);
                }
            }

            collected.AddRange(line);

            var text = Encoding.ASCII.GetString([.. line]).Trim();
            var semicolon = text.IndexOf(';');
            if (semicolon >= 0)
            {
                text = text[..semicolon];
            }

            if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
            {
                _mode = Mode.Passthrough;
                return Serve(collected, buffer.Span);
            }

            // Chunk data plus its trailing CRLF.
            var toRead = size + 2;
            var chunk = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                var total = 0;
                while (total < toRead)
                {
                    var read = await _inner.ReadAsync(chunk.AsMemory(total, toRead - total), cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0)
                    {
                        _mode = Mode.Passthrough;
                        collected.AddRange(chunk[..total]);
                        return Serve(collected, buffer.Span);
                    }

                    total += read;
                }

                collected.AddRange(chunk[..total]);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            if (size == 0)
            {
                // Terminal chunk: the next bytes are a new request.
                _mode = Mode.ExpectingHead;
                return Serve(collected, buffer.Span);
            }

            // Hand back what we have rather than buffering an entire upload in memory.
            if (collected.Count >= 16 * 1024)
            {
                return Serve(collected, buffer.Span);
            }
        }
    }

    private int Serve(List<byte> bytes, Span<byte> destination)
    {
        Queue([.. bytes]);
        return TryDrainPending(destination, out var served) ? served : 0;
    }

    private void Queue(byte[] bytes)
    {
        _pending.Position = _pending.Length;
        _pending.Write(bytes);
    }

    private bool TryDrainPending(Span<byte> destination, out int written)
    {
        var available = _pending.Length - _pendingOffset;
        if (available <= 0)
        {
            written = 0;
            return false;
        }

        var take = (int)Math.Min(available, destination.Length);
        _pending.Position = _pendingOffset;

        var slice = destination[..take];
        for (var i = 0; i < take; i++)
        {
            slice[i] = (byte)_pending.ReadByte();
        }

        _pendingOffset += take;

        if (_pendingOffset >= _pending.Length)
        {
            _pending.SetLength(0);
            _pendingOffset = 0;
        }

        written = take;
        return true;
    }

    private static bool EndsWithHeaderTerminator(List<byte> bytes)
        => bytes.Count >= 4
            && bytes[^4] == (byte)'\r' && bytes[^3] == (byte)'\n'
            && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n';

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public ValueTask CompleteWriteAsync(CancellationToken cancellationToken = default)
        => StreamPump.HalfCloseAsync(_inner);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pending.Dispose();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _pending.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
