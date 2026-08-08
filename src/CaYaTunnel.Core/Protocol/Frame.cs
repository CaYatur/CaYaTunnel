using System.Buffers;

namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// A single decoded frame. <see cref="Payload"/> is backed by a pooled array, so callers
/// must <see cref="Dispose"/> the frame once they are done with (or have copied) the bytes.
/// Holding onto <see cref="Payload"/> after disposal is a use-after-free style bug.
/// </summary>
public readonly struct Frame : IDisposable
{
    private readonly byte[]? _rented;
    private readonly int _length;

    internal Frame(FrameType type, FrameFlags flags, uint streamId, byte[]? rented, int length)
    {
        Type = type;
        Flags = flags;
        StreamId = streamId;
        _rented = rented;
        _length = length;
    }

    public FrameType Type { get; }

    public FrameFlags Flags { get; }

    public uint StreamId { get; }

    public ReadOnlyMemory<byte> Payload =>
        _rented is null ? ReadOnlyMemory<byte>.Empty : _rented.AsMemory(0, _length);

    public ReadOnlySpan<byte> PayloadSpan =>
        _rented is null ? ReadOnlySpan<byte>.Empty : _rented.AsSpan(0, _length);

    public bool IsControlChannel => StreamId == ProtocolConstants.ControlStreamId;

    public bool HasFin => (Flags & FrameFlags.Fin) != 0;

    /// <summary>Copies the payload out of the pooled buffer so it can outlive this frame.</summary>
    public byte[] CopyPayload() => PayloadSpan.ToArray();

    public void Dispose()
    {
        if (_rented is not null)
        {
            ArrayPool<byte>.Shared.Return(_rented);
        }
    }

    public override string ToString() => $"{Type} stream={StreamId} flags={Flags} len={_length}";
}

/// <summary>Raised when a peer sends something that violates the wire format.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
