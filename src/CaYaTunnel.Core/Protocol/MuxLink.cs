using System.Buffers.Binary;
using System.Collections.Concurrent;
using CaYaTunnel.Core.Protocol.Messages;

namespace CaYaTunnel.Core.Protocol;

/// <summary>Which end of the link this instance is, used for stream id parity.</summary>
public enum MuxRole
{
    Server,
    Client,
}

/// <summary>
/// The single multiplexed connection between one client and the server: a control channel plus
/// any number of concurrent data streams, all over one TLS socket. A brief network drop kills
/// exactly this object; the client rebuilds a new one and the tunnels come back.
/// </summary>
public sealed class MuxLink : IAsyncDisposable
{
    private readonly Stream _transport;
    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;
    private readonly ConcurrentDictionary<uint, MuxStream> _streams = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<StreamOpenAckMessage>> _pendingOpens = new();
    private readonly CancellationTokenSource _shutdown = new();

    private uint _nextStreamId;
    private long _lastFrameTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int _disposed;

    public MuxLink(Stream transport, MuxRole role)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _reader = new FrameReader(transport);
        _writer = new FrameWriter(transport);
        Role = role;
        // Parity keeps the two ends from ever picking the same id: server odd, client even.
        _nextStreamId = role == MuxRole.Server ? 1u : 2u;
    }

    public MuxRole Role { get; }

    /// <summary>Set when the peer sent a GoAway, so the owner can report why the link ended.</summary>
    public GoAwayMessage? GoAwayReceived { get; private set; }

    /// <summary>Latest keep-alive round trip.</summary>
    public int? LatencyMs { get; private set; }

    public int ActiveStreamCount => _streams.Count;

    /// <summary>Invoked for every control-channel message that is not part of the handshake.</summary>
    public Func<ControlEnvelope, CancellationToken, Task>? ControlHandler { get; set; }

    /// <summary>
    /// Called when the peer opens a stream towards us. Implementations dial the requested target
    /// and return the connected stream; the link then acknowledges and pumps bytes both ways.
    /// Throwing here turns into a negative StreamOpenAck carrying the exception message.
    /// </summary>
    public Func<StreamOpenMessage, CancellationToken, Task<Stream>>? TargetDialer { get; set; }

    /// <summary>Raised once when the read loop ends, with the reason if it was a failure.</summary>
    public Action<Exception?>? Closed { get; set; }

    // ---- Handshake helpers ------------------------------------------------

    /// <summary>Reads one frame directly — used only for Hello/HelloAck before the loop starts.</summary>
    public async Task<Frame?> ReadHandshakeFrameAsync(CancellationToken cancellationToken)
        => await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask SendJsonAsync<T>(FrameType type, T value, CancellationToken cancellationToken = default)
        => _writer.WriteJsonAsync(type, ProtocolConstants.ControlStreamId, value, cancellationToken);

    public ValueTask SendControlAsync(ControlEnvelope envelope, CancellationToken cancellationToken = default)
        => _writer.WriteJsonAsync(FrameType.Control, ProtocolConstants.ControlStreamId, envelope, cancellationToken);

    public async ValueTask SendGoAwayAsync(string code, string? message, CancellationToken cancellationToken = default)
    {
        try
        {
            await SendJsonAsync(FrameType.GoAway, new GoAwayMessage { Code = code, Message = message }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Peer already vanished — nothing useful to do.
        }
    }

    // ---- Stream lifecycle -------------------------------------------------

    /// <summary>
    /// Opens a stream towards the peer and waits for it to confirm it reached the target.
    /// Throws <see cref="TargetUnreachableException"/> when the peer could not dial.
    /// </summary>
    public async Task<MuxStream> OpenStreamAsync(StreamOpenMessage request, CancellationToken cancellationToken = default)
    {
        var id = AllocateStreamId();
        // Send window starts empty: how much we may send is whatever the *peer* advertises in
        // its ack, not what we asked to receive. Credit is granted below once the ack lands.
        var stream = new MuxStream(this, id, request.TunnelId, initialSendWindow: 0)
        {
            RemoteEndpoint = request.RemoteEndpoint,
        };

        var ackSource = new TaskCompletionSource<StreamOpenAckMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _streams[id] = stream;
        _pendingOpens[id] = ackSource;

        try
        {
            await _writer.WriteJsonAsync(FrameType.StreamOpen, id, request, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(ProtocolConstants.HandshakeTimeout);

            var ack = await ackSource.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!ack.Ok)
            {
                throw new TargetUnreachableException(ack.Error ?? "The device could not reach the target service.");
            }

            if (ack.InitialWindow > 0)
            {
                stream.OnWindowUpdate(ack.InitialWindow);
            }

            return stream;
        }
        catch
        {
            _pendingOpens.TryRemove(id, out _);
            _streams.TryRemove(id, out _);
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _pendingOpens.TryRemove(id, out _);
        }
    }

    private uint AllocateStreamId()
    {
        while (true)
        {
            var id = Interlocked.Add(ref _nextStreamId, 2) - 2;
            if (id == ProtocolConstants.ControlStreamId)
            {
                continue; // wrapped onto the control id — skip it
            }

            if (!_streams.ContainsKey(id))
            {
                return id;
            }
        }
    }

    internal void RemoveStream(uint id) => _streams.TryRemove(id, out _);

    internal ValueTask SendDataAsync(uint streamId, ReadOnlyMemory<byte> payload, FrameFlags flags, CancellationToken cancellationToken)
        => _writer.WriteAsync(FrameType.StreamData, streamId, payload, flags, cancellationToken);

    internal ValueTask SendWindowUpdateAsync(uint streamId, int credit, CancellationToken cancellationToken)
        => _writer.WriteWindowUpdateAsync(streamId, credit, cancellationToken);

    internal ValueTask SendStreamCloseAsync(uint streamId, string? reason, CancellationToken cancellationToken)
        => reason is null
            ? _writer.WriteAsync(FrameType.StreamClose, streamId, default, FrameFlags.None, cancellationToken)
            : _writer.WriteJsonAsync(FrameType.StreamClose, streamId, new StreamCloseMessage { Reason = reason }, cancellationToken);

    // ---- Read loop --------------------------------------------------------

    /// <summary>
    /// Pumps frames until the peer disconnects or the link faults. Exactly one call per link.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var keepAlive = KeepAliveLoopAsync(linked.Token);
        Exception? failure = null;

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var frame = await _reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break; // peer closed cleanly
                }

                using (frame.Value)
                {
                    Interlocked.Exchange(ref _lastFrameTicks, DateTimeOffset.UtcNow.UtcTicks);
                    await DispatchAsync(frame.Value, linked.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Owner asked us to stop.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            FaultAllStreams(failure ?? new IOException("The tunnel link closed."));

            try
            {
                await keepAlive.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            Closed?.Invoke(failure);
        }
    }

    private async Task DispatchAsync(Frame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case FrameType.Ping:
                await _writer.WriteAsync(FrameType.Pong, ProtocolConstants.ControlStreamId, frame.Payload.ToArray(),
                    FrameFlags.None, cancellationToken).ConfigureAwait(false);
                break;

            case FrameType.Pong:
                if (frame.PayloadSpan.Length >= 8)
                {
                    var sentTicks = BinaryPrimitives.ReadInt64BigEndian(frame.PayloadSpan);
                    var elapsed = DateTimeOffset.UtcNow.UtcTicks - sentTicks;
                    LatencyMs = (int)Math.Clamp(elapsed / TimeSpan.TicksPerMillisecond, 0, int.MaxValue);
                }

                break;

            case FrameType.Control:
                {
                    var envelope = JsonProtocol.DeserializeRequired<ControlEnvelope>(frame.PayloadSpan);
                    var handler = ControlHandler;
                    if (handler is not null)
                    {
                        // Off the read loop: a control handler that talks to disk or the network
                        // must never stall data streams sharing this link.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await handler(envelope, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                ControlHandlerFaulted?.Invoke(envelope, ex);
                            }
                        }, CancellationToken.None);
                    }

                    break;
                }

            case FrameType.GoAway:
                GoAwayReceived = JsonProtocol.Deserialize<GoAwayMessage>(frame.PayloadSpan);
                await _shutdown.CancelAsync().ConfigureAwait(false);
                break;

            case FrameType.StreamOpen:
                HandleStreamOpen(frame.StreamId, JsonProtocol.DeserializeRequired<StreamOpenMessage>(frame.PayloadSpan));
                break;

            case FrameType.StreamOpenAck:
                {
                    var ack = JsonProtocol.DeserializeRequired<StreamOpenAckMessage>(frame.PayloadSpan);
                    if (_pendingOpens.TryRemove(frame.StreamId, out var pending))
                    {
                        pending.TrySetResult(ack);
                    }

                    break;
                }

            case FrameType.StreamData:
                if (_streams.TryGetValue(frame.StreamId, out var dataStream))
                {
                    await dataStream.OnDataAsync(frame.Payload, frame.HasFin, cancellationToken).ConfigureAwait(false);
                }

                break;

            case FrameType.StreamWindowUpdate:
                if (frame.PayloadSpan.Length >= 4 && _streams.TryGetValue(frame.StreamId, out var windowStream))
                {
                    var credit = BinaryPrimitives.ReadUInt32BigEndian(frame.PayloadSpan);
                    if (credit > 0)
                    {
                        windowStream.OnWindowUpdate((int)Math.Min(credit, int.MaxValue));
                    }
                }

                break;

            case FrameType.StreamClose:
                {
                    if (_pendingOpens.TryRemove(frame.StreamId, out var aborted))
                    {
                        aborted.TrySetResult(new StreamOpenAckMessage { Ok = false, Error = "Stream closed before it was acknowledged." });
                    }

                    if (_streams.TryGetValue(frame.StreamId, out var closing))
                    {
                        var reason = frame.PayloadSpan.IsEmpty
                            ? null
                            : JsonProtocol.Deserialize<StreamCloseMessage>(frame.PayloadSpan)?.Reason;
                        closing.OnRemoteClose(reason);
                    }

                    break;
                }

            case FrameType.Hello:
            case FrameType.HelloAck:
                throw new ProtocolException($"Unexpected {frame.Type} frame after the handshake completed.");

            default:
                // Unknown frame types are ignored on purpose so a newer peer can add frames
                // without breaking an older one.
                break;
        }
    }

    /// <summary>Reports a control handler that threw, for logging by the owner.</summary>
    public Action<ControlEnvelope, Exception>? ControlHandlerFaulted { get; set; }

    private void HandleStreamOpen(uint streamId, StreamOpenMessage request)
    {
        var stream = new MuxStream(this, streamId, request.TunnelId, request.InitialWindow)
        {
            RemoteEndpoint = request.RemoteEndpoint,
        };
        _streams[streamId] = stream;

        _ = Task.Run(async () =>
        {
            var dialer = TargetDialer;
            if (dialer is null)
            {
                await RejectAsync(streamId, "This peer does not accept inbound streams.").ConfigureAwait(false);
                return;
            }

            Stream target;
            try
            {
                target = await dialer(request, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RejectAsync(streamId, ex.Message).ConfigureAwait(false);
                return;
            }

            try
            {
                await _writer.WriteJsonAsync(
                    FrameType.StreamOpenAck,
                    streamId,
                    new StreamOpenAckMessage { Ok = true, InitialWindow = ProtocolConstants.InitialStreamWindow },
                    _shutdown.Token).ConfigureAwait(false);

                await StreamPump.RunAsync(stream, target, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Normal disconnect noise.
            }
            finally
            {
                await target.DisposeAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    private async Task RejectAsync(uint streamId, string error)
    {
        if (_streams.TryRemove(streamId, out var stream))
        {
            stream.OnLinkFailure(new TargetUnreachableException(error));
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _writer.WriteJsonAsync(
                FrameType.StreamOpenAck,
                streamId,
                new StreamOpenAckMessage { Ok = false, Error = error },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Link gone.
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        using var timer = new PeriodicTimer(ProtocolConstants.KeepAliveInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var since = DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastFrameTicks);
            if (since > ProtocolConstants.LinkTimeout.Ticks)
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
                return;
            }

            BinaryPrimitives.WriteInt64BigEndian(payload, DateTimeOffset.UtcNow.UtcTicks);
            await _writer.WriteAsync(FrameType.Ping, ProtocolConstants.ControlStreamId, payload,
                FrameFlags.None, cancellationToken).ConfigureAwait(false);
        }
    }

    private void FaultAllStreams(Exception exception)
    {
        foreach (var pending in _pendingOpens.Values)
        {
            pending.TrySetException(exception);
        }

        _pendingOpens.Clear();

        foreach (var stream in _streams.Values)
        {
            stream.OnLinkFailure(exception);
        }

        _streams.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        FaultAllStreams(new ObjectDisposedException(nameof(MuxLink)));
        _writer.Dispose();
        _shutdown.Dispose();

        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The peer accepted the stream request but could not reach the local target.</summary>
public sealed class TargetUnreachableException(string message) : Exception(message);
