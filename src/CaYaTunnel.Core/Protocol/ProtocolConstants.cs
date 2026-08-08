namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// Wire-level constants shared by server and client. Changing any of these is a
/// breaking protocol change and must come with a <see cref="Version"/> bump.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Protocol version negotiated in the Hello/HelloAck exchange.</summary>
    public const int Version = 1;

    /// <summary>Magic bytes sent by the client immediately after the TLS handshake.</summary>
    public static ReadOnlySpan<byte> Preamble => "CYT1"u8;

    /// <summary>
    /// The TLS server name a client announces when opening its control link.
    /// <para>
    /// This is what lets one port carry both agent links and public HTTPS: the gateway reads the
    /// name out of the ClientHello and knows which it is before anything else happens. The
    /// .invalid suffix is reserved by RFC 2606 and can never be registered, so it can never
    /// collide with a real tunnel hostname.
    /// </para>
    /// </summary>
    public const string ControlSniName = "control.cayatunnel.invalid";

    /// <summary>Bytes on the wire before every frame payload.</summary>
    public const int HeaderSize = 10;

    /// <summary>
    /// Hard cap the reader enforces on any single frame payload. Control frames carry
    /// JSON (device and tunnel lists), so this is generous; data frames are chunked far
    /// below it by <see cref="DataChunkSize"/>.
    /// </summary>
    public const int MaxPayloadSize = 1024 * 1024;

    /// <summary>Largest slice of tunnelled bytes we put in a single StreamData frame.</summary>
    public const int DataChunkSize = 32 * 1024;

    /// <summary>Per-stream receive window before the sender must wait for a WindowUpdate.</summary>
    public const int InitialStreamWindow = 512 * 1024;

    /// <summary>Send a WindowUpdate once this many bytes of the window have been consumed.</summary>
    public const int WindowUpdateThreshold = InitialStreamWindow / 2;

    /// <summary>Control channel always uses stream id 0.</summary>
    public const uint ControlStreamId = 0;

    /// <summary>How often each side sends a Ping when the link is otherwise idle.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    /// <summary>Link is considered dead if no frame of any kind arrives within this window.</summary>
    public static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(70);

    /// <summary>Time the client waits for HelloAck before giving up on an attempt.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
}
