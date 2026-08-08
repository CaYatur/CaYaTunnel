using System.Net;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;

namespace CaYaTunnel.Server.Gateway;

/// <summary>
/// One authenticated client's live link. Owns the multiplexed connection, opens data streams
/// towards the device when public traffic arrives, and carries control messages both ways.
/// </summary>
public sealed class DeviceSession : IAsyncDisposable
{
    private readonly MuxLink _link;
    private int _disposed;

    internal DeviceSession(string deviceId, string deviceName, MuxLink link, IPEndPoint? remoteEndPoint)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        _link = link;
        RemoteAddress = remoteEndPoint?.Address.ToString() ?? "unknown";
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    public string DeviceId { get; }

    public string DeviceName { get; private set; }

    public string RemoteAddress { get; }

    public DateTimeOffset ConnectedAt { get; }

    public int? LatencyMs => _link.LatencyMs;

    public int ActiveStreams => _link.ActiveStreamCount;

    internal MuxLink Link => _link;

    public void UpdateName(string name) => DeviceName = name;

    /// <summary>
    /// Asks the device to dial <paramref name="tunnel"/>'s target and returns the stream that
    /// carries it. Throws <see cref="TargetUnreachableException"/> when the service is down on
    /// the device's side, which the caller turns into a clean close rather than a hang.
    /// </summary>
    public Task<MuxStream> OpenStreamAsync(
        TunnelDefinition tunnel,
        string? remoteEndpoint,
        CancellationToken cancellationToken = default,
        string transport = StreamTransports.Tcp)
        => _link.OpenStreamAsync(
            new StreamOpenMessage
            {
                TunnelId = tunnel.Id,
                TargetHost = tunnel.TargetHost,
                TargetPort = tunnel.TargetPort,
                RemoteEndpoint = remoteEndpoint,
                Transport = transport,
            },
            cancellationToken);

    public ValueTask SendControlAsync(ControlEnvelope envelope, CancellationToken cancellationToken = default)
        => _link.SendControlAsync(envelope, cancellationToken);

    /// <summary>Fire-and-forget push. Used for fan-out, where one dead link must not stall the rest.</summary>
    public void PostControl(ControlEnvelope envelope)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _link.SendControlAsync(envelope).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The link is on its way out; its own read loop will clean up.
            }
        });
    }

    public ValueTask DisconnectAsync(string code, string? message)
        => _link.SendGoAwayAsync(code, message);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _link.DisposeAsync().ConfigureAwait(false);
    }
}
