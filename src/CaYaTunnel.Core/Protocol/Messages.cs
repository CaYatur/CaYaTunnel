using System.Text.Json;
using CaYaTunnel.Core.Models;

namespace CaYaTunnel.Core.Protocol.Messages;

// ---------------------------------------------------------------------------
// Handshake
// ---------------------------------------------------------------------------

/// <summary>First frame a client sends once TLS is up.</summary>
public sealed class HelloMessage
{
    public int ProtocolVersion { get; set; } = ProtocolConstants.Version;

    /// <summary>Identity issued at provisioning time. Empty on a first-time enrolment.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Machine name, used as the default display name.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// The secret baked into this client build. Verified against the server's current key on
    /// every connect, so rotating the server key immediately locks out old builds.
    /// </summary>
    public string DeviceKey { get; set; } = "";

    public string ClientVersion { get; set; } = "";

    public string OperatingSystem { get; set; } = "";

    /// <summary>LAN addresses of this machine, surfaced in both UIs.</summary>
    public List<string> LocalAddresses { get; set; } = [];
}

/// <summary>Why a client was refused. The client shows a different UI for each.</summary>
public enum AuthFailureReason
{
    None = 0,

    /// <summary>Key does not match any known enrolment secret.</summary>
    InvalidKey,

    /// <summary>Key was valid for an older generation; the operator has since rotated it.</summary>
    KeyRotated,

    /// <summary>This specific device was revoked from the server UI.</summary>
    DeviceRevoked,

    /// <summary>Client speaks a protocol version this server cannot serve.</summary>
    UnsupportedVersion,

    /// <summary>Server is configured to require manual approval and this device is pending.</summary>
    PendingApproval,

    /// <summary>Server is shutting down or otherwise refusing new links.</summary>
    ServerUnavailable,
}

/// <summary>Server's answer to <see cref="HelloMessage"/>.</summary>
public sealed class HelloAckMessage
{
    public bool Ok { get; set; }

    public AuthFailureReason Reason { get; set; } = AuthFailureReason.None;

    /// <summary>Operator-readable explanation, shown verbatim in the client UI.</summary>
    public string? Message { get; set; }

    public int ProtocolVersion { get; set; } = ProtocolConstants.Version;

    /// <summary>Assigned on first enrolment; the client persists it for later connects.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Current server key generation, so the client can tell a rotation happened.</summary>
    public int KeyGeneration { get; set; }

    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Full registry state, so the client's UI is populated the moment it connects.</summary>
    public RegistrySnapshot? Snapshot { get; set; }
}

/// <summary>Sent immediately before a deliberate close so the peer can explain itself.</summary>
public sealed class GoAwayMessage
{
    public string Code { get; set; } = "";

    public string? Message { get; set; }
}

// ---------------------------------------------------------------------------
// Data streams
// ---------------------------------------------------------------------------

/// <summary>Transport a stream carries. Sent as a string so new ones need no protocol bump.</summary>
public static class StreamTransports
{
    public const string Tcp = "tcp";

    /// <summary>
    /// The stream carries length-prefixed UDP datagrams rather than a byte stream; see
    /// <see cref="DatagramFraming"/>.
    /// </summary>
    public const string Udp = "udp";
}

/// <summary>Server -&gt; client: a visitor arrived, please dial this target.</summary>
public sealed class StreamOpenMessage
{
    public string TunnelId { get; set; } = "";

    public string TargetHost { get; set; } = "";

    public int TargetPort { get; set; }

    /// <summary>
    /// <see cref="StreamTransports.Tcp"/> or <see cref="StreamTransports.Udp"/>. Defaults to TCP
    /// so an older client that ignores the field still behaves correctly.
    /// </summary>
    public string Transport { get; set; } = StreamTransports.Tcp;

    public bool IsUdp => string.Equals(Transport, StreamTransports.Udp, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the visitor came from, for logs and the live connection list.</summary>
    public string? RemoteEndpoint { get; set; }

    /// <summary>How many bytes the opener is willing to buffer before a WindowUpdate.</summary>
    public int InitialWindow { get; set; } = ProtocolConstants.InitialStreamWindow;
}

public sealed class StreamOpenAckMessage
{
    public bool Ok { get; set; }

    /// <summary>Populated when the client could not reach the target (refused, timeout, DNS).</summary>
    public string? Error { get; set; }

    public int InitialWindow { get; set; } = ProtocolConstants.InitialStreamWindow;
}

public sealed class StreamCloseMessage
{
    public string? Reason { get; set; }
}

// ---------------------------------------------------------------------------
// Control channel
// ---------------------------------------------------------------------------

/// <summary>
/// Every control-channel message is wrapped in this. <see cref="Id"/> is set on requests that
/// expect a <see cref="ControlMessageTypes.Result"/> reply carrying the same id.
/// </summary>
public sealed class ControlEnvelope
{
    public string Type { get; set; } = "";

    public string? Id { get; set; }

    public JsonElement? Data { get; set; }

    public static ControlEnvelope Create<T>(string type, T data, string? id = null) => new()
    {
        Type = type,
        Id = id,
        Data = JsonSerializer.SerializeToElement(data, JsonProtocol.Options),
    };

    public static ControlEnvelope Create(string type, string? id = null) => new()
    {
        Type = type,
        Id = id,
    };

    public T? Read<T>() => Data is null ? default : Data.Value.Deserialize<T>(JsonProtocol.Options);

    public T ReadRequired<T>() => Read<T>()
        ?? throw new ProtocolException($"Control message '{Type}' is missing its {typeof(T).Name} body.");
}

/// <summary>Reply to any request-shaped control message.</summary>
public sealed class ControlResult
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public JsonElement? Data { get; set; }

    public static ControlResult Success(object? data = null) => new()
    {
        Ok = true,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, JsonProtocol.Options),
    };

    public static ControlResult Failure(string error) => new() { Ok = false, Error = error };
}

/// <summary>Complete registry state. Sent on connect and on demand.</summary>
public sealed class RegistrySnapshot
{
    public ServerInfo Server { get; set; } = new();

    public List<DeviceInfo> Devices { get; set; } = [];

    public List<TunnelDefinition> Tunnels { get; set; } = [];
}

/// <summary>A message the client should surface to the user as a toast/banner.</summary>
public sealed class NoticeMessage
{
    public string Severity { get; set; } = "info"; // info | warning | error

    public string Title { get; set; } = "";

    public string? Body { get; set; }

    /// <summary>Tunnel or device the notice is about, so the UI can highlight the right row.</summary>
    public string? SubjectId { get; set; }
}

// ---- Request bodies -------------------------------------------------------

public sealed class CreateTunnelRequest
{
    public string? Name { get; set; }

    public TunnelKind Kind { get; set; }

    /// <summary>Device that will carry the traffic. Defaults to the requesting device.</summary>
    public string? DeviceId { get; set; }

    public string TargetHost { get; set; } = "127.0.0.1";

    public int TargetPort { get; set; }

    /// <summary>
    /// Label only, e.g. "minecraft" — the server appends its configured base domain. Leave
    /// null to have the server generate a random one.
    /// </summary>
    public string? Subdomain { get; set; }

    /// <summary>Explicit public port. Null lets the server allocate from its configured range.</summary>
    public int? PublicPort { get; set; }

    public string? Protocol { get; set; }

    public bool TerminateTls { get; set; } = true;

    /// <summary>HTTP tunnels only: which public schemes the hostname answers on.</summary>
    public HttpAccess HttpAccess { get; set; } = HttpAccess.HttpAndHttps;

    /// <summary>HTTP tunnels only: present the target's own address as the Host header.</summary>
    public bool RewriteHostHeader { get; set; }

    /// <summary>Port tunnels only: TCP, UDP, or both on the same public port.</summary>
    public TransportProtocols Transports { get; set; } = TransportProtocols.Tcp;

    /// <summary>
    /// Port tunnels only: use the gateway's shared port rather than allocating one. At most one
    /// tunnel per transport can do this — see <see cref="Models.TunnelDefinition.UseSharedPort"/>.
    /// </summary>
    public bool UseSharedPort { get; set; }
}

public sealed class UpdateTunnelRequest
{
    public string TunnelId { get; set; } = "";

    public string? Name { get; set; }

    public string? TargetHost { get; set; }

    public int? TargetPort { get; set; }

    public bool? Enabled { get; set; }

    /// <summary>Changing this rebinds the public port, so it can be switched after creation.</summary>
    public TransportProtocols? Transports { get; set; }

    public HttpAccess? HttpAccess { get; set; }

    public bool? RewriteHostHeader { get; set; }
}

public sealed class TunnelIdRequest
{
    public string TunnelId { get; set; } = "";
}

public sealed class RenameDeviceRequest
{
    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>Control-channel message type discriminators.</summary>
public static class ControlMessageTypes
{
    // Server -> client
    public const string Snapshot = "snapshot";
    public const string TunnelAdded = "tunnel.added";
    public const string TunnelUpdated = "tunnel.updated";
    public const string TunnelRemoved = "tunnel.removed";
    public const string TunnelStats = "tunnel.stats";
    public const string DeviceUpdated = "device.updated";
    public const string DeviceRemoved = "device.removed";
    public const string ServerUpdated = "server.updated";
    public const string Notice = "notice";
    public const string Result = "result";

    // Client -> server
    public const string RequestSnapshot = "snapshot.request";
    public const string CreateTunnel = "tunnel.create";
    public const string UpdateTunnel = "tunnel.update";
    public const string DeleteTunnel = "tunnel.delete";
    public const string RenameDevice = "device.rename";
}
