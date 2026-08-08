using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;

namespace CaYaTunnel.Client;

public enum ClientState
{
    Offline,
    Connecting,
    Authenticating,
    Online,
    Reconnecting,

    /// <summary>
    /// Terminal until something changes: the server refused this build. Retrying cannot help,
    /// so the client stops instead of hammering the gateway forever.
    /// </summary>
    Unauthorized,
}

/// <summary>
/// The agent. Keeps one outbound link to the gateway alive, dials local and LAN services when
/// the gateway asks, and mirrors the shared registry so the UI can show what every other device
/// is publishing.
/// </summary>
public sealed class TunnelClient : IAsyncDisposable
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResult>> _pending = new();
    private readonly ClientSettingsStore _store;
    private readonly Lock _stateGate = new();

    private ClientSettings _settings;
    private ClientConnectionProfile _profile;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private MuxLink? _link;

    public TunnelClient(ClientSettingsStore store, ClientSettings settings, ClientConnectionProfile profile)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public ClientState State { get; private set; } = ClientState.Offline;

    /// <summary>Set when the server refused us, so the UI can explain exactly why.</summary>
    public AuthFailureReason? RefusalReason { get; private set; }

    public string? StatusMessage { get; private set; }

    public string DeviceId => _settings.DeviceId ?? "";

    public string DeviceName => string.IsNullOrWhiteSpace(_settings.DeviceName) ? Environment.MachineName : _settings.DeviceName!;

    public ClientConnectionProfile Profile => _profile;

    public RegistrySnapshot? Snapshot { get; private set; }

    public ServerInfo? ServerInfo => Snapshot?.Server;

    public int? LatencyMs => _link?.LatencyMs;

    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>Fires on every state transition, with the reason when there is one.</summary>
    public event Action<ClientState, string?>? StateChanged;

    /// <summary>Fires whenever the mirrored registry changes, for any reason.</summary>
    public event Action<RegistrySnapshot>? SnapshotChanged;

    /// <summary>A message from the server the user should see — "tunnel removed remotely" etc.</summary>
    public event Action<NoticeMessage>? NoticeReceived;

    public event Action<string>? LogMessage;

    // ---- Lifecycle -----------------------------------------------------------

    public void Start()
    {
        lock (_stateGate)
        {
            if (_runTask is { IsCompleted: false })
            {
                return;
            }

            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_runCts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_stateGate)
        {
            cts = _runCts;
            task = _runTask;
            _runCts = null;
            _runTask = null;
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Give up waiting; the link is disposed below either way.
            }
        }

        cts.Dispose();
        SetState(ClientState.Offline, "Disconnected.");
    }

    /// <summary>Applies new connection details and reconnects with them.</summary>
    public async Task ReconfigureAsync(ClientSettings settings, ClientConnectionProfile profile)
    {
        await StopAsync().ConfigureAwait(false);

        _settings = settings;
        _profile = profile;
        RefusalReason = null;

        if (profile.IsUsable)
        {
            Start();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_profile.IsUsable)
            {
                SetState(ClientState.Offline, "No server has been configured yet.");
                return;
            }

            try
            {
                SetState(attempt == 0 ? ClientState.Connecting : ClientState.Reconnecting,
                    $"Connecting to {_profile.ServerHost}:{_profile.ControlPort}…");

                await ConnectAndServeAsync(cancellationToken).ConfigureAwait(false);

                // A clean return means the server closed the link; reconnect promptly.
                attempt = 0;
            }
            catch (UnauthorizedClientException ex)
            {
                RefusalReason = ex.Reason;
                SetState(ClientState.Unauthorized, ex.Message);
                return; // retrying cannot fix a refused key
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                Log($"Connection failed: {ex.Message}");
                SetState(ClientState.Reconnecting, ex.Message);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var delay = BackoffFor(attempt);
            SetState(ClientState.Reconnecting, $"Retrying in {delay.TotalSeconds:0}s…");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(ClientState.Offline, "Disconnected.");
    }

    /// <summary>
    /// Exponential backoff with jitter. The jitter matters when a gateway restarts: without it
    /// every client would retry in lockstep and hit the server as one wave.
    /// </summary>
    private static TimeSpan BackoffFor(int attempt)
    {
        if (attempt <= 0)
        {
            return MinBackoff;
        }

        var seconds = Math.Min(MaxBackoff.TotalSeconds, MinBackoff.TotalSeconds * Math.Pow(1.7, attempt - 1));
        var jitter = RandomNumberGenerator.GetInt32(0, 1000) / 1000.0;
        return TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * jitter));
    }

    private async Task ConnectAndServeAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(_profile.ServerHost, _profile.ControlPort, connectTimeout.Token)
                .ConfigureAwait(false);
        }

        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, ValidateServerCertificate);
        MuxLink? link = null;

        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(ProtocolConstants.HandshakeTimeout);

            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    // A fixed name rather than the server's: it identifies this as a control
                    // link, which is what lets the gateway share one port with public HTTPS.
                    // Certificate validation is by pinned fingerprint, so the name is not used
                    // for trust and choosing it freely costs nothing.
                    TargetHost = ProtocolConstants.ControlSniName,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                handshakeTimeout.Token).ConfigureAwait(false);

            await tls.WriteAsync(ProtocolConstants.Preamble.ToArray(), handshakeTimeout.Token).ConfigureAwait(false);
            await tls.FlushAsync(handshakeTimeout.Token).ConfigureAwait(false);

            SetState(ClientState.Authenticating, "Authenticating…");

            link = new MuxLink(tls, MuxRole.Client);
            tls = null!; // ownership moved

            var ack = await HandshakeAsync(link, handshakeTimeout.Token).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(ack.DeviceId) && ack.DeviceId != _settings.DeviceId)
            {
                _settings.DeviceId = ack.DeviceId;
                _store.Save(_settings);
            }

            Snapshot = ack.Snapshot;
            if (ack.Snapshot is not null)
            {
                SnapshotChanged?.Invoke(ack.Snapshot);
            }

            link.ControlHandler = (envelope, ct) => HandleControlAsync(envelope, ct);
            link.TargetDialer = DialTargetAsync;
            link.DatagramDialer = DialUdpTargetAsync;
            link.ControlHandlerFaulted = (envelope, ex) => Log($"Handling '{envelope.Type}' failed: {ex.Message}");

            _link = link;
            ConnectedAt = DateTimeOffset.UtcNow;
            SetState(ClientState.Online, $"Connected to {_profile.ServerName}.");

            await link.RunAsync(cancellationToken).ConfigureAwait(false);

            if (link.GoAwayReceived is { } goAway)
            {
                Log($"Server closed the link: {goAway.Message ?? goAway.Code}");

                // A revocation is not a transient failure — say so instead of reconnecting.
                if (goAway.Code is "device-revoked" or "device-removed")
                {
                    throw new UnauthorizedClientException(
                        AuthFailureReason.DeviceRevoked,
                        goAway.Message ?? "This device was revoked from the server.");
                }
            }
        }
        finally
        {
            _link = null;
            ConnectedAt = null;
            FailPendingRequests("The connection to the server was lost.");

            if (link is not null)
            {
                await link.DisposeAsync().ConfigureAwait(false);
            }
            else if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<HelloAckMessage> HandshakeAsync(MuxLink link, CancellationToken cancellationToken)
    {
        await link.SendJsonAsync(FrameType.Hello, new HelloMessage
        {
            DeviceId = _settings.DeviceId,
            DeviceName = DeviceName,
            DeviceKey = _profile.EnrollmentKey,
            ClientVersion = typeof(TunnelClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            OperatingSystem = Environment.OSVersion.VersionString,
            LocalAddresses = [.. GetLocalAddresses()],
        }, cancellationToken).ConfigureAwait(false);

        var frame = await link.ReadHandshakeFrameAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The server closed the connection during the handshake.");

        using var received = frame;
        if (received.Type != FrameType.HelloAck)
        {
            throw new ProtocolException($"Expected HelloAck but the server sent {received.Type}.");
        }

        var ack = JsonProtocol.DeserializeRequired<HelloAckMessage>(received.PayloadSpan);
        if (!ack.Ok)
        {
            throw new UnauthorizedClientException(ack.Reason, ack.Message ?? DescribeRefusal(ack.Reason));
        }

        return ack;
    }

    private static string DescribeRefusal(AuthFailureReason reason) => reason switch
    {
        AuthFailureReason.KeyRotated => "The server key was rotated, so this build is no longer authorised. Ask for a new client build.",
        AuthFailureReason.DeviceRevoked => "This device has been revoked on the server.",
        AuthFailureReason.PendingApproval => "This device is waiting to be approved on the server.",
        AuthFailureReason.UnsupportedVersion => "This client is too old (or too new) for that server.",
        AuthFailureReason.ServerUnavailable => "The server is not accepting connections right now.",
        _ => "The server refused this client's key.",
    };

    /// <summary>
    /// Pinned-certificate validation. The deployment is expected to use a self-signed
    /// certificate, so chain trust is meaningless here; what matters is that the certificate is
    /// exactly the one this build was provisioned against.
    /// </summary>
    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var expected = _profile.CertificateFingerprint;

        if (string.IsNullOrWhiteSpace(expected))
        {
            // No pin available (a hand-configured client). Fall back to normal chain validation
            // so a public certificate still works, rather than trusting anything at all.
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null)
        {
            return false;
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
        var matches = string.Equals(actual, expected.Replace(":", "").Trim(), StringComparison.OrdinalIgnoreCase);

        if (!matches)
        {
            Log($"Refusing the server: certificate {actual[..16]}… does not match the pinned fingerprint.");
        }

        return matches;
    }

    // ---- Serving the gateway --------------------------------------------------

    /// <summary>
    /// Connects to whatever the gateway asked for. This is the whole point of the agent: the
    /// target may be loopback or any address this machine can reach on its LANs, which is what
    /// makes tunnelling a device on another computer possible.
    /// </summary>
    private async Task<Stream> DialTargetAsync(StreamOpenMessage request, CancellationToken cancellationToken)
    {
        var target = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DialTimeout);

            await target.ConnectAsync(request.TargetHost, request.TargetPort, timeout.Token).ConfigureAwait(false);
            Log($"Forwarding {request.RemoteEndpoint ?? "a visitor"} to {request.TargetHost}:{request.TargetPort}.");
            return target.GetStream();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            target.Dispose();
            throw new IOException($"Timed out connecting to {request.TargetHost}:{request.TargetPort}.");
        }
        catch (SocketException ex)
        {
            target.Dispose();
            throw new IOException($"Could not reach {request.TargetHost}:{request.TargetPort} — {ex.SocketErrorCode}.");
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a UDP socket towards the target. Unlike TCP there is no connection to fail, so this
    /// only reports a problem when the address cannot be resolved or the socket cannot be created.
    /// </summary>
    private async Task<IDatagramChannel> DialUdpTargetAsync(StreamOpenMessage request, CancellationToken cancellationToken)
    {
        var channel = await UdpTargetChannel
            .ConnectAsync(request.TargetHost, request.TargetPort, cancellationToken)
            .ConfigureAwait(false);

        Log($"Forwarding UDP from {request.RemoteEndpoint ?? "a visitor"} to {request.TargetHost}:{request.TargetPort}.");
        return channel;
    }

    private Task HandleControlAsync(ControlEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case ControlMessageTypes.Result:
                if (envelope.Id is not null && _pending.TryRemove(envelope.Id, out var pending))
                {
                    pending.TrySetResult(envelope.Read<ControlResult>() ?? ControlResult.Failure("Empty result."));
                }

                break;

            case ControlMessageTypes.Snapshot:
                Snapshot = envelope.Read<RegistrySnapshot>();
                if (Snapshot is not null)
                {
                    SnapshotChanged?.Invoke(Snapshot);
                }

                break;

            case ControlMessageTypes.TunnelAdded:
            case ControlMessageTypes.TunnelUpdated:
            case ControlMessageTypes.TunnelStats:
                ApplyTunnelChange(envelope.Read<TunnelDefinition>(), removed: false);
                break;

            case ControlMessageTypes.TunnelRemoved:
                ApplyTunnelChange(envelope.Read<TunnelDefinition>(), removed: true);
                break;

            case ControlMessageTypes.DeviceUpdated:
                ApplyDeviceChange(envelope.Read<DeviceInfo>(), removed: false);
                break;

            case ControlMessageTypes.DeviceRemoved:
                ApplyDeviceChange(envelope.Read<DeviceInfo>(), removed: true);
                break;

            case ControlMessageTypes.ServerUpdated:
                if (Snapshot is not null && envelope.Read<ServerInfo>() is { } server)
                {
                    Snapshot.Server = server;
                    SnapshotChanged?.Invoke(Snapshot);
                }

                break;

            case ControlMessageTypes.Notice:
                if (envelope.Read<NoticeMessage>() is { } notice)
                {
                    Log($"{notice.Title}{(notice.Body is null ? "" : " — " + notice.Body)}");
                    NoticeReceived?.Invoke(notice);
                }

                break;
        }

        return Task.CompletedTask;
    }

    private void ApplyTunnelChange(TunnelDefinition? tunnel, bool removed)
    {
        if (tunnel is null || Snapshot is null)
        {
            return;
        }

        var tunnels = Snapshot.Tunnels;
        var index = tunnels.FindIndex(t => t.Id == tunnel.Id);

        if (removed)
        {
            if (index >= 0)
            {
                tunnels.RemoveAt(index);
            }
        }
        else if (index >= 0)
        {
            tunnels[index] = tunnel;
        }
        else
        {
            tunnels.Add(tunnel);
        }

        SnapshotChanged?.Invoke(Snapshot);
    }

    private void ApplyDeviceChange(DeviceInfo? device, bool removed)
    {
        if (device is null || Snapshot is null)
        {
            return;
        }

        var devices = Snapshot.Devices;
        var index = devices.FindIndex(d => d.Id == device.Id);

        if (removed)
        {
            if (index >= 0)
            {
                devices.RemoveAt(index);
            }
        }
        else if (index >= 0)
        {
            devices[index] = device;
        }
        else
        {
            devices.Add(device);
        }

        SnapshotChanged?.Invoke(Snapshot);
    }

    // ---- Requests to the server -------------------------------------------------

    public Task<ControlResult> CreateTunnelAsync(CreateTunnelRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(ControlMessageTypes.CreateTunnel, request, cancellationToken);

    public Task<ControlResult> UpdateTunnelAsync(UpdateTunnelRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(ControlMessageTypes.UpdateTunnel, request, cancellationToken);

    public Task<ControlResult> DeleteTunnelAsync(string tunnelId, CancellationToken cancellationToken = default)
        => RequestAsync(ControlMessageTypes.DeleteTunnel, new TunnelIdRequest { TunnelId = tunnelId }, cancellationToken);

    public Task<ControlResult> RenameDeviceAsync(string deviceId, string name, CancellationToken cancellationToken = default)
        => RequestAsync(ControlMessageTypes.RenameDevice, new RenameDeviceRequest { DeviceId = deviceId, Name = name }, cancellationToken);

    public Task<ControlResult> RefreshAsync(CancellationToken cancellationToken = default)
        => RequestAsync(ControlMessageTypes.RequestSnapshot, new { }, cancellationToken);

    private async Task<ControlResult> RequestAsync<T>(string type, T body, CancellationToken cancellationToken)
    {
        var link = _link;
        if (link is null || State != ClientState.Online)
        {
            return ControlResult.Failure("Not connected to the server.");
        }

        var id = Guid.NewGuid().ToString("n");
        var completion = new TaskCompletionSource<ControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await link.SendControlAsync(ControlEnvelope.Create(type, body, id), cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ControlResult.Failure("The server did not answer in time.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return ControlResult.Failure("The connection to the server was lost.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void FailPendingRequests(string message)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.TrySetResult(ControlResult.Failure(message));
            }
        }
    }

    // ---- Helpers -------------------------------------------------------------------

    /// <summary>Every usable IPv4/IPv6 address on this machine, for display on other devices.</summary>
    public static IEnumerable<string> GetLocalAddresses()
    {
        var addresses = new List<string>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily is AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address.Address))
                    {
                        addresses.Add(address.Address.ToString());
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Nothing to show; not worth failing over.
        }

        return addresses.Distinct();
    }

    private void SetState(ClientState state, string? message)
    {
        if (State == state && StatusMessage == message)
        {
            return;
        }

        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(state, message);
    }

    private void Log(string message) => LogMessage?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

/// <summary>The server refused this client; reconnecting will not help until something changes.</summary>
public sealed class UnauthorizedClientException(AuthFailureReason reason, string message) : Exception(message)
{
    public AuthFailureReason Reason { get; } = reason;
}
