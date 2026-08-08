using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Server.Dns;
using CaYaTunnel.Server.Registry;

namespace CaYaTunnel.Server.Gateway;

/// <summary>
/// Operations that change the registry. Both the local admin UI and remote clients call these,
/// so a tunnel created from either place gets identical validation, DNS handling and fan-out.
/// </summary>
public sealed partial class TunnelServer
{
    // ---- Tunnels -----------------------------------------------------------

    public async Task<TunnelDefinition> CreateTunnelAsync(
        CreateTunnelRequest request,
        string actorDeviceId,
        string actorLabel,
        CancellationToken cancellationToken = default)
    {
        var tunnel = Registry.CreateTunnel(request, actorDeviceId, Config);

        if (tunnel.Hostname is not null && _dns.IsAutomated)
        {
            try
            {
                // Only HTTP tunnels may sit behind an HTTP proxy; see IDnsProvider.CreateRecordAsync.
                var recordId = await _dns.CreateRecordAsync(
                    tunnel.Hostname,
                    string.IsNullOrWhiteSpace(Config.PublicHost) ? tunnel.Hostname : Config.PublicHost,
                    allowProxy: tunnel.Kind == TunnelKind.HttpHost,
                    cancellationToken).ConfigureAwait(false);

                if (recordId is not null)
                {
                    tunnel.DnsRecordId = recordId;
                    Registry.SetDnsRecordId(tunnel.Id, recordId);
                }

                Log.Info("dns", $"Created a record for {tunnel.Hostname}.");
            }
            catch (Exception ex) when (ex is DnsProviderException or HttpRequestException or TaskCanceledException)
            {
                // The tunnel itself is fine — it just will not resolve until DNS is sorted out.
                // Deleting it here would be worse: the operator would lose the config and still
                // have to fix DNS.
                Log.Warning("dns", $"Tunnel '{tunnel.Name}' was created, but its DNS record was not: {ex.Message}");
                Notify(actorDeviceId, new NoticeMessage
                {
                    Severity = "warning",
                    Title = "DNS record not created",
                    Body = $"{tunnel.Hostname} will not resolve until the record exists. {ex.Message}",
                    SubjectId = tunnel.Id,
                });
            }
        }

        Log.Info("tunnel", $"'{actorLabel}' created {Describe(tunnel)}.");
        return tunnel;
    }

    public Task<TunnelDefinition> UpdateTunnelAsync(UpdateTunnelRequest request, string actorLabel)
    {
        var tunnel = Registry.UpdateTunnel(request);
        Log.Info("tunnel", $"'{actorLabel}' updated {Describe(tunnel)}.");
        return Task.FromResult(tunnel);
    }

    /// <summary>
    /// Removes a tunnel and tells the device carrying it, so the machine that owns the service
    /// shows "removed remotely" rather than silently losing an endpoint.
    /// </summary>
    public async Task<bool> DeleteTunnelAsync(string tunnelId, string actorLabel, CancellationToken cancellationToken = default)
    {
        var tunnel = Registry.RemoveTunnel(tunnelId);
        if (tunnel is null)
        {
            return false;
        }

        if (tunnel.Hostname is not null && _dns.IsAutomated)
        {
            try
            {
                await _dns.RemoveRecordAsync(tunnel.Hostname, tunnel.DnsRecordId, cancellationToken).ConfigureAwait(false);
                Log.Info("dns", $"Removed the record for {tunnel.Hostname}.");
            }
            catch (Exception ex) when (ex is DnsProviderException or HttpRequestException or TaskCanceledException)
            {
                Log.Warning("dns", $"Could not remove the record for {tunnel.Hostname}: {ex.Message}");
            }
        }

        Notify(tunnel.DeviceId, new NoticeMessage
        {
            Severity = "warning",
            Title = "Tunnel removed remotely",
            Body = $"'{tunnel.Name}' ({tunnel.TargetEndpoint()}) was deleted by {actorLabel}.",
            SubjectId = tunnel.Id,
        });

        Log.Info("tunnel", $"'{actorLabel}' deleted {Describe(tunnel)}.");
        return true;
    }

    private void OnTunnelAdded(TunnelDefinition tunnel)
    {
        if (tunnel.Kind == TunnelKind.TcpPort && tunnel.PublicPort is { } port)
        {
            StartPortListener(port);
        }

        Broadcast(ControlMessageTypes.TunnelAdded, tunnel);
    }

    private void OnTunnelRemoved(TunnelDefinition tunnel)
    {
        if (tunnel.Kind == TunnelKind.TcpPort && tunnel.PublicPort is { } port)
        {
            StopPortListener(port);
        }

        Broadcast(ControlMessageTypes.TunnelRemoved, tunnel);
    }

    private static string Describe(TunnelDefinition tunnel) => tunnel.Kind switch
    {
        TunnelKind.HttpHost => $"HTTP tunnel {tunnel.Hostname} -> {tunnel.TargetEndpoint()}",
        TunnelKind.TcpHostAware => $"{tunnel.Protocol} tunnel {tunnel.Hostname}:{tunnel.PublicPort} -> {tunnel.TargetEndpoint()}",
        _ => $"TCP tunnel :{tunnel.PublicPort} -> {tunnel.TargetEndpoint()}",
    };

    // ---- Devices --------------------------------------------------------------

    public DeviceInfo? RenameDevice(string deviceId, string name, string actorLabel)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new TunnelValidationException("A device name cannot be empty.");
        }

        var updated = Registry.MutateDevice(deviceId, info => info.Name = trimmed);
        if (updated is not null)
        {
            FindSession(deviceId)?.UpdateName(trimmed);
            Log.Info("device", $"'{actorLabel}' renamed a device to '{trimmed}'.");
        }

        return updated;
    }

    /// <summary>
    /// Revokes or restores a device. A revoked device is disconnected immediately rather than at
    /// its next reconnect — otherwise revoking would not actually stop traffic.
    /// </summary>
    public async Task SetDeviceRevokedAsync(string deviceId, bool revoked, string actorLabel)
    {
        Registry.MutateDevice(deviceId, info => info.Revoked = revoked);

        if (revoked && FindSession(deviceId) is { } session)
        {
            await session.DisconnectAsync("device-revoked", "This device was revoked from the server.")
                .ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        Log.Warning("device", $"'{actorLabel}' {(revoked ? "revoked" : "restored")} a device.");
    }

    public void ApproveDevice(string deviceId, string actorLabel)
    {
        Registry.MutateDevice(deviceId, info => info.Approved = true);
        Log.Info("device", $"'{actorLabel}' approved a device.");
    }

    public async Task RemoveDeviceAsync(string deviceId, string actorLabel, CancellationToken cancellationToken = default)
    {
        if (FindSession(deviceId) is { } session)
        {
            await session.DisconnectAsync("device-removed", "This device was removed from the server.")
                .ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        var orphaned = Registry.RemoveDevice(deviceId);

        foreach (var tunnel in orphaned.Where(t => t.Hostname is not null && _dns.IsAutomated))
        {
            try
            {
                await _dns.RemoveRecordAsync(tunnel.Hostname!, tunnel.DnsRecordId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DnsProviderException or HttpRequestException or TaskCanceledException)
            {
                Log.Warning("dns", $"Could not remove the record for {tunnel.Hostname}: {ex.Message}");
            }
        }

        Log.Warning("device", $"'{actorLabel}' removed a device and {orphaned.Count} tunnel(s) that depended on it.");
    }

    // ---- Requests arriving from clients -----------------------------------------

    private async Task HandleClientControlAsync(DeviceSession session, ControlEnvelope envelope, CancellationToken cancellationToken)
    {
        ControlResult result;

        try
        {
            switch (envelope.Type)
            {
                case ControlMessageTypes.RequestSnapshot:
                    await session.SendControlAsync(
                        ControlEnvelope.Create(ControlMessageTypes.Snapshot, Registry.CreateSnapshot(BuildServerInfo())),
                        cancellationToken).ConfigureAwait(false);
                    return;

                case ControlMessageTypes.CreateTunnel:
                    {
                        var request = envelope.ReadRequired<CreateTunnelRequest>();
                        var tunnel = await CreateTunnelAsync(request, session.DeviceId, session.DeviceName, cancellationToken)
                            .ConfigureAwait(false);
                        result = ControlResult.Success(tunnel);
                        break;
                    }

                case ControlMessageTypes.UpdateTunnel:
                    {
                        var request = envelope.ReadRequired<UpdateTunnelRequest>();
                        var tunnel = await UpdateTunnelAsync(request, session.DeviceName).ConfigureAwait(false);
                        result = ControlResult.Success(tunnel);
                        break;
                    }

                case ControlMessageTypes.DeleteTunnel:
                    {
                        var request = envelope.ReadRequired<TunnelIdRequest>();
                        var removed = await DeleteTunnelAsync(request.TunnelId, session.DeviceName, cancellationToken)
                            .ConfigureAwait(false);
                        result = removed
                            ? ControlResult.Success()
                            : ControlResult.Failure("That tunnel no longer exists.");
                        break;
                    }

                case ControlMessageTypes.RenameDevice:
                    {
                        var request = envelope.ReadRequired<RenameDeviceRequest>();
                        var updated = RenameDevice(request.DeviceId, request.Name, session.DeviceName);
                        result = updated is null
                            ? ControlResult.Failure("That device is not registered on this server.")
                            : ControlResult.Success(updated);
                        break;
                    }

                default:
                    result = ControlResult.Failure($"This server does not understand '{envelope.Type}'.");
                    break;
            }
        }
        catch (TunnelValidationException ex)
        {
            // Expected rejections: bad port, taken hostname. The client shows the message as-is.
            result = ControlResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("control", $"'{envelope.Type}' from '{session.DeviceName}' failed", ex);
            result = ControlResult.Failure("The server hit an unexpected error handling that request.");
        }

        if (envelope.Id is not null)
        {
            await session.SendControlAsync(
                ControlEnvelope.Create(ControlMessageTypes.Result, result, envelope.Id),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
