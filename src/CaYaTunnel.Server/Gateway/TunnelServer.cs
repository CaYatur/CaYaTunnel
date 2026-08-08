using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Core.Security;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Dns;
using CaYaTunnel.Server.Registry;

namespace CaYaTunnel.Server.Gateway;

/// <summary>
/// The gateway. Accepts client links on the control port, accepts public traffic on the
/// listeners, and joins the two together. Everything the admin UI does goes through here so
/// local edits and remote client requests follow exactly the same validation and fan-out.
/// </summary>
public sealed partial class TunnelServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _listenerTasks = [];
    private readonly Lock _stateGate = new();

    private CancellationTokenSource? _shutdown;
    private X509Certificate2? _controlCertificate;
    private X509Certificate2? _publicCertificate;
    private IDnsProvider _dns = new ManualDnsProvider();

    public TunnelServer(ServerConfig config, TunnelRegistry registry, GatewayLog log)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Log = log ?? throw new ArgumentNullException(nameof(log));

        Registry.TunnelAdded += tunnel => OnTunnelAdded(tunnel);
        Registry.TunnelUpdated += tunnel => Broadcast(ControlMessageTypes.TunnelUpdated, tunnel);
        Registry.TunnelRemoved += tunnel => OnTunnelRemoved(tunnel);
        Registry.DeviceUpdated += device => Broadcast(ControlMessageTypes.DeviceUpdated, device);
        Registry.DeviceRemoved += device => Broadcast(ControlMessageTypes.DeviceRemoved, device);
    }

    public ServerConfig Config { get; private set; }

    public TunnelRegistry Registry { get; }

    public GatewayLog Log { get; }

    public bool IsRunning { get; private set; }

    /// <summary>SHA-256 the clients pin. Baked into every provisioned build.</summary>
    public string ControlCertificateFingerprint { get; private set; } = "";

    public IDnsProvider DnsProvider => _dns;

    public IReadOnlyCollection<DeviceSession> Sessions => [.. _sessions.Values];

    public event Action? StateChanged;

    public ServerInfo BuildServerInfo() => Config.ToServerInfo(_dns.IsAutomated);

    // ---- Lifecycle ---------------------------------------------------------

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        var problems = Config.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The gateway cannot start until these are fixed:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  • " + p)));
        }

        ServerPaths.EnsureCreated();

        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _shutdown.Token;

        _controlCertificate = CertificateManager.LoadOrCreate(
            string.IsNullOrWhiteSpace(Config.TlsCertificatePath) ? ServerPaths.ControlCertificateFile : Config.TlsCertificatePath,
            Config.TlsCertificatePassword,
            string.IsNullOrWhiteSpace(Config.PublicHost) ? "cayatunnel-control" : Config.PublicHost);

        ControlCertificateFingerprint = CertificateManager.Fingerprint(_controlCertificate);

        _publicCertificate = CertificateManager.LoadOrCreate(
            string.IsNullOrWhiteSpace(Config.PublicTlsCertificatePath) ? ServerPaths.PublicCertificateFile : Config.PublicTlsCertificatePath,
            Config.PublicTlsCertificatePassword,
            string.IsNullOrWhiteSpace(Config.BaseDomain) ? "cayatunnel-public" : $"*.{Config.BaseDomain}");

        _dns = CreateDnsProvider(Config);

        _listenerTasks.Clear();
        _listenerTasks.Add(RunControlListenerAsync(token));

        StartPublicListeners(token);

        IsRunning = true;
        Log.Info("gateway", $"Listening for clients on {Config.ControlBindAddress}:{Config.ControlPort}.");
        Log.Info("gateway", $"Certificate fingerprint {CertificateManager.FormatFingerprint(ControlCertificateFingerprint)}");
        StateChanged?.Invoke();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        Log.Info("gateway", "Shutting down.");

        if (_shutdown is not null)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisconnectAsync("server-shutdown", "The gateway is shutting down.").ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        StopPublicListeners();

        try
        {
            await Task.WhenAll(_listenerTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Listeners that will not stop promptly are abandoned; the process is going down.
        }

        _listenerTasks.Clear();
        _shutdown?.Dispose();
        _shutdown = null;

        Registry.Flush();
        StateChanged?.Invoke();
    }

    /// <summary>Applies edited settings by restarting the listeners with them.</summary>
    public async Task ApplyConfigAsync(ServerConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var wasRunning = IsRunning;
        if (wasRunning)
        {
            await StopAsync().ConfigureAwait(false);
        }

        Config = config;

        if (wasRunning)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _dns = CreateDnsProvider(config);
            StateChanged?.Invoke();
        }
    }

    public static IDnsProvider CreateDnsProvider(ServerConfig config) => config.Dns.Provider switch
    {
        DnsProviderKind.Cloudflare => new CloudflareDnsProvider(
            config.Dns.CloudflareApiToken,
            config.Dns.CloudflareZoneId,
            config.Dns.ProxyRecords,
            config.Dns.RecordTtl),
        _ => new ManualDnsProvider(),
    };

    // ---- Control listener ----------------------------------------------------

    private async Task RunControlListenerAsync(CancellationToken cancellationToken)
    {
        var address = IPAddress.TryParse(Config.ControlBindAddress, out var parsed) ? parsed : IPAddress.Any;
        var listener = new TcpListener(address, Config.ControlPort);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            Log.Error("control", $"Could not bind {address}:{Config.ControlPort} — {ex.Message}");
            throw;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleControlConnectionAsync(client, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (SocketException ex)
        {
            Log.Error("control", $"Control listener stopped — {ex.Message}");
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleControlConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        SslStream? tls = null;

        try
        {
            client.NoDelay = true;

            tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(ProtocolConstants.HandshakeTimeout);

            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _controlCertificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                handshakeTimeout.Token).ConfigureAwait(false);

            // A short magic string before any frame, so a port scanner that completes a TLS
            // handshake gets dropped immediately instead of reaching the frame decoder.
            var preamble = new byte[ProtocolConstants.Preamble.Length];
            await tls.ReadExactlyAsync(preamble, handshakeTimeout.Token).ConfigureAwait(false);
            if (!preamble.AsSpan().SequenceEqual(ProtocolConstants.Preamble))
            {
                Log.Debug("control", $"Rejected {remote}: not a CaYaTunnel client.");
                return;
            }

            var link = new MuxLink(tls, MuxRole.Server);
            tls = null; // ownership moved to the link

            var accepted = await AuthenticateAsync(link, remote, handshakeTimeout.Token).ConfigureAwait(false);
            if (accepted is null)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await RunSessionAsync(accepted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or SocketException)
        {
            Log.Debug("control", $"Connection from {remote} ended during setup: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("control", $"Unexpected failure handling {remote}", ex);
        }
        finally
        {
            if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }

            client.Dispose();
        }
    }

    /// <summary>
    /// Verifies the client's key and produces a session, or answers with the specific reason it
    /// was refused so the client can show "authorization revoked" instead of retrying forever.
    /// </summary>
    private async Task<DeviceSession?> AuthenticateAsync(MuxLink link, IPEndPoint? remote, CancellationToken cancellationToken)
    {
        var frame = await link.ReadHandshakeFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        HelloMessage hello;
        using (var received = frame.Value)
        {
            if (received.Type != FrameType.Hello)
            {
                Log.Debug("auth", $"{remote} sent {received.Type} instead of Hello.");
                return null;
            }

            hello = JsonProtocol.DeserializeRequired<HelloMessage>(received.PayloadSpan);
        }

        if (hello.ProtocolVersion != ProtocolConstants.Version)
        {
            await RefuseAsync(link, AuthFailureReason.UnsupportedVersion,
                $"This server speaks protocol v{ProtocolConstants.Version}; the client speaks v{hello.ProtocolVersion}.",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var deviceName = string.IsNullOrWhiteSpace(hello.DeviceName) ? "unnamed-device" : hello.DeviceName.Trim();
        var existing = string.IsNullOrWhiteSpace(hello.DeviceId) ? null : Registry.FindDevice(hello.DeviceId);

        if (existing?.Info.Revoked == true)
        {
            Log.Warning("auth", $"Refused revoked device '{existing.Info.Name}' from {remote?.Address}.");
            await RefuseAsync(link, AuthFailureReason.DeviceRevoked,
                "This device has been revoked from the server.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!VerifyKey(hello.DeviceKey, existing, out var failure))
        {
            Log.Warning("auth", $"Refused '{deviceName}' from {remote?.Address}: {failure}.");
            await RefuseAsync(link, failure,
                failure == AuthFailureReason.KeyRotated
                    ? "The server key was rotated. This client build is no longer valid — generate a new one."
                    : "The key this client presented is not valid on this server.",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var deviceId = string.IsNullOrWhiteSpace(hello.DeviceId) ? Guid.NewGuid().ToString("n") : hello.DeviceId;
        var record = Registry.GetOrCreateDevice(deviceId, deviceName, Config.KeyGeneration);

        if (Config.RequireManualApproval && !record.Info.Approved)
        {
            Log.Warning("auth", $"'{deviceName}' from {remote?.Address} is waiting for approval.");
            await RefuseAsync(link, AuthFailureReason.PendingApproval,
                "This device is waiting for an operator to approve it on the server.", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        Registry.MutateDevice(deviceId, info =>
        {
            info.Name = string.IsNullOrWhiteSpace(info.Name) || info.Name == deviceId ? deviceName : info.Name;
            info.Online = true;
            info.RemoteAddress = remote?.Address.ToString();
            info.LocalAddresses = [.. hello.LocalAddresses.Take(16)];
            info.ClientVersion = hello.ClientVersion;
            info.OperatingSystem = hello.OperatingSystem;
            info.ConnectedAt = DateTimeOffset.UtcNow;
            info.LastSeenAt = DateTimeOffset.UtcNow;
            info.KeyGeneration = Config.KeyGeneration;
        });

        var session = new DeviceSession(deviceId, record.Info.Name, link, remote);

        await link.SendJsonAsync(FrameType.HelloAck, new HelloAckMessage
        {
            Ok = true,
            DeviceId = deviceId,
            KeyGeneration = Config.KeyGeneration,
            Snapshot = Registry.CreateSnapshot(BuildServerInfo()),
        }, cancellationToken).ConfigureAwait(false);

        return session;
    }

    private bool VerifyKey(string presentedKey, DeviceRecord? device, out AuthFailureReason failure)
    {
        failure = AuthFailureReason.InvalidKey;

        if (string.IsNullOrWhiteSpace(presentedKey))
        {
            return false;
        }

        // A build cut for one specific machine carries its own key, so revoking that device
        // actually disables that build. Shared-key builds fall back to the server key.
        if (device is { HasDeviceKey: true })
        {
            if (EnrollmentKey.Verify(presentedKey, device.KeyHash!, device.KeySalt!))
            {
                return true;
            }
        }
        else if (EnrollmentKey.Matches(presentedKey, Config.EnrollmentKey))
        {
            return true;
        }

        foreach (var retired in Config.RetiredKeys)
        {
            if (EnrollmentKey.Verify(presentedKey, retired.Hash, retired.Salt))
            {
                failure = AuthFailureReason.KeyRotated;
                return false;
            }
        }

        return false;
    }

    private static async Task RefuseAsync(MuxLink link, AuthFailureReason reason, string message, CancellationToken cancellationToken)
    {
        try
        {
            await link.SendJsonAsync(FrameType.HelloAck, new HelloAckMessage
            {
                Ok = false,
                Reason = reason,
                Message = message,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Client hung up first.
        }
    }

    // ---- Session lifetime ------------------------------------------------------

    private async Task RunSessionAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        // A reconnect after an unclean drop can arrive before the old link times out. The new
        // one wins: the device is definitely reachable there.
        if (_sessions.TryRemove(session.DeviceId, out var previous))
        {
            Log.Debug("session", $"Replacing an earlier link for '{session.DeviceName}'.");
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        _sessions[session.DeviceId] = session;
        session.Link.ControlHandler = (envelope, ct) => HandleClientControlAsync(session, envelope, ct);
        session.Link.ControlHandlerFaulted = (envelope, ex) =>
            Log.Error("control", $"Handling '{envelope.Type}' from '{session.DeviceName}' failed", ex);

        Log.Info("session", $"'{session.DeviceName}' connected from {session.RemoteAddress}.");
        StateChanged?.Invoke();

        try
        {
            await session.Link.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<string, DeviceSession>(session.DeviceId, session));

            Registry.MutateDevice(session.DeviceId, info =>
            {
                info.Online = false;
                info.ConnectedAt = null;
                info.LatencyMs = null;
                info.LastSeenAt = DateTimeOffset.UtcNow;
            });

            foreach (var tunnel in Registry.ResetActiveConnections(session.DeviceId))
            {
                Broadcast(ControlMessageTypes.TunnelUpdated, tunnel);
            }

            Log.Info("session", $"'{session.DeviceName}' disconnected.");
            await session.DisposeAsync().ConfigureAwait(false);
            StateChanged?.Invoke();
        }
    }

    public DeviceSession? FindSession(string deviceId) => _sessions.GetValueOrDefault(deviceId);

    // ---- Fan-out ---------------------------------------------------------------

    /// <summary>
    /// Pushes a change to every connected client. This is why a tunnel created on one machine
    /// appears on another immediately: the control channel is already open, so there is nothing
    /// to poll.
    /// </summary>
    private void Broadcast<T>(string type, T payload)
    {
        var envelope = ControlEnvelope.Create(type, payload);
        foreach (var session in _sessions.Values)
        {
            session.PostControl(envelope);
        }

        StateChanged?.Invoke();
    }

    private void Notify(string deviceId, NoticeMessage notice)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            session.PostControl(ControlEnvelope.Create(ControlMessageTypes.Notice, notice));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _controlCertificate?.Dispose();
        _publicCertificate?.Dispose();
        (_dns as IDisposable)?.Dispose();
    }
}
