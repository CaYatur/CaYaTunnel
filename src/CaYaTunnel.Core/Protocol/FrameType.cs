namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// Frame kinds carried over the single multiplexed link between a client and the server.
/// Values are wire format — never renumber an existing entry.
/// </summary>
public enum FrameType : byte
{
    // ---- Control channel (stream id 0) ----------------------------------

    /// <summary>Client -&gt; server. JSON <see cref="Messages.HelloMessage"/>.</summary>
    Hello = 0x01,

    /// <summary>Server -&gt; client. JSON <see cref="Messages.HelloAckMessage"/>.</summary>
    HelloAck = 0x02,

    /// <summary>Either direction. Payload is 8 bytes of opaque echo data.</summary>
    Ping = 0x03,

    /// <summary>Reply to <see cref="Ping"/>, echoing its payload verbatim.</summary>
    Pong = 0x04,

    /// <summary>Either direction. JSON control envelope; see <see cref="Messages.ControlEnvelope"/>.</summary>
    Control = 0x05,

    /// <summary>
    /// Either direction, sent immediately before a deliberate close.
    /// JSON <see cref="Messages.GoAwayMessage"/> explaining why.
    /// </summary>
    GoAway = 0x06,

    // ---- Data streams (stream id != 0) ----------------------------------

    /// <summary>Opener -&gt; peer. JSON <see cref="Messages.StreamOpenMessage"/>.</summary>
    StreamOpen = 0x10,

    /// <summary>Peer -&gt; opener. JSON <see cref="Messages.StreamOpenAckMessage"/>.</summary>
    StreamOpenAck = 0x11,

    /// <summary>Either direction. Payload is raw tunnelled bytes.</summary>
    StreamData = 0x12,

    /// <summary>Either direction. Optional JSON <see cref="Messages.StreamCloseMessage"/>.</summary>
    StreamClose = 0x13,

    /// <summary>Either direction. Payload is a big-endian uint32 credit increment.</summary>
    StreamWindowUpdate = 0x14,
}

/// <summary>Per-frame bit flags.</summary>
[Flags]
public enum FrameFlags : byte
{
    None = 0,

    /// <summary>
    /// On <see cref="FrameType.StreamData"/>: the sender will send no further data on this
    /// stream (half-close). The stream stays readable in the other direction.
    /// </summary>
    Fin = 0x01,
}
