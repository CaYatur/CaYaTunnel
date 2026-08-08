using System.Text.Json;
using CaYaTunnel.Core.Models;
using CaYaTunnel.Core.Protocol;
using CaYaTunnel.Core.Protocol.Messages;
using CaYaTunnel.Server.Configuration;

namespace CaYaTunnel.Server.Registry;

/// <summary>
/// Single source of truth for devices and tunnels. Every mutation raises an event, which is what
/// the gateway fans out over the control channels — that is why a tunnel created on one machine
/// shows up on another within a frame's time rather than at the next poll.
/// </summary>
public sealed class TunnelRegistry
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceRecord> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TunnelDefinition> _tunnels = new(StringComparer.OrdinalIgnoreCase);

    public TunnelRegistry(string? path = null)
    {
        _path = path ?? ServerPaths.RegistryFile;
        Load();
    }

    public event Action<TunnelDefinition>? TunnelAdded;

    public event Action<TunnelDefinition>? TunnelUpdated;

    public event Action<TunnelDefinition>? TunnelRemoved;

    public event Action<DeviceInfo>? DeviceUpdated;

    public event Action<DeviceInfo>? DeviceRemoved;

    // ---- Reads -------------------------------------------------------------

    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices.Values.Select(d => d.Info.Clone())];
            }
        }
    }

    public IReadOnlyList<TunnelDefinition> Tunnels
    {
        get
        {
            lock (_gate)
            {
                return [.. _tunnels.Values.Select(t => t.Clone())];
            }
        }
    }

    public RegistrySnapshot CreateSnapshot(ServerInfo server)
    {
        lock (_gate)
        {
            return new RegistrySnapshot
            {
                Server = server,
                Devices = [.. _devices.Values.Select(d => d.Info.Clone())],
                Tunnels = [.. _tunnels.Values.Select(t => t.Clone())],
            };
        }
    }

    public DeviceRecord? FindDevice(string deviceId)
    {
        lock (_gate)
        {
            return _devices.GetValueOrDefault(deviceId);
        }
    }

    public TunnelDefinition? FindTunnel(string tunnelId)
    {
        lock (_gate)
        {
            return _tunnels.GetValueOrDefault(tunnelId)?.Clone();
        }
    }

    /// <summary>Routing lookup for HTTP and host-aware TCP. Case-insensitive, as DNS is.</summary>
    public TunnelDefinition? FindByHostname(string hostname, TunnelKind kind)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var trimmed = hostname.Trim().TrimEnd('.');
        lock (_gate)
        {
            return _tunnels.Values.FirstOrDefault(t =>
                t.Kind == kind
                && t.Enabled
                && string.Equals(t.Hostname, trimmed, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    /// <summary>Routing lookup for dedicated TCP ports.</summary>
    public TunnelDefinition? FindByPublicPort(int port)
    {
        lock (_gate)
        {
            return _tunnels.Values.FirstOrDefault(t =>
                t.Kind == TunnelKind.PortForward && t.Enabled && t.PublicPort == port)?.Clone();
        }
    }

    // ---- Devices -----------------------------------------------------------

    /// <summary>Creates the record on first contact, or returns the existing one.</summary>
    public DeviceRecord GetOrCreateDevice(string deviceId, string deviceName, int keyGeneration)
    {
        DeviceRecord record;
        bool created;

        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out var existing))
            {
                record = existing;
                created = false;
            }
            else
            {
                record = new DeviceRecord
                {
                    Info = new DeviceInfo
                    {
                        Id = deviceId,
                        Name = string.IsNullOrWhiteSpace(deviceName) ? deviceId : deviceName,
                        KeyGeneration = keyGeneration,
                    },
                };
                _devices[deviceId] = record;
                created = true;
            }
        }

        if (created)
        {
            Persist();
            DeviceUpdated?.Invoke(record.Info.Clone());
        }

        return record;
    }

    /// <summary>Applies a mutation to a device and fans the result out. Returns the new state.</summary>
    public DeviceInfo? MutateDevice(string deviceId, Action<DeviceInfo> mutate, bool persist = true)
    {
        DeviceInfo snapshot;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var record))
            {
                return null;
            }

            mutate(record.Info);
            snapshot = record.Info.Clone();
        }

        if (persist)
        {
            Persist();
        }

        DeviceUpdated?.Invoke(snapshot);
        return snapshot;
    }

    public void SetDeviceKey(string deviceId, string keyHash, string salt)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out var record))
            {
                record.KeyHash = keyHash;
                record.KeySalt = salt;
            }
        }

        Persist();
    }

    /// <summary>
    /// Removes a device and every tunnel that depended on it — leaving orphan tunnels pointing at
    /// a machine that can no longer connect would just produce endpoints that time out.
    /// </summary>
    public IReadOnlyList<TunnelDefinition> RemoveDevice(string deviceId)
    {
        DeviceInfo? removed;
        List<TunnelDefinition> orphaned;

        lock (_gate)
        {
            if (!_devices.Remove(deviceId, out var record))
            {
                return [];
            }

            removed = record.Info.Clone();
            orphaned = [.. _tunnels.Values.Where(t => t.DeviceId == deviceId).Select(t => t.Clone())];
            foreach (var tunnel in orphaned)
            {
                _tunnels.Remove(tunnel.Id);
            }
        }

        Persist();

        foreach (var tunnel in orphaned)
        {
            TunnelRemoved?.Invoke(tunnel);
        }

        DeviceRemoved?.Invoke(removed);
        return orphaned;
    }

    // ---- Tunnels ------------------------------------------------------------

    /// <summary>
    /// Validates and stores a new tunnel, allocating a hostname or public port when the request
    /// did not pin one. Throws <see cref="TunnelValidationException"/> with a message meant for
    /// the operator, not a stack trace.
    /// </summary>
    public TunnelDefinition CreateTunnel(CreateTunnelRequest request, string requestingDeviceId, ServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? requestingDeviceId : request.DeviceId;

        if (request.TargetPort is < 1 or > 65535)
        {
            throw new TunnelValidationException("Target port must be between 1 and 65535.");
        }

        var targetHost = string.IsNullOrWhiteSpace(request.TargetHost) ? "127.0.0.1" : request.TargetHost.Trim();

        TunnelDefinition tunnel;
        lock (_gate)
        {
            if (!_devices.ContainsKey(deviceId))
            {
                throw new TunnelValidationException("That device is not registered on this server.");
            }

            tunnel = new TunnelDefinition
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = string.IsNullOrWhiteSpace(request.Name) ? "" : request.Name.Trim(),
                Kind = request.Kind,
                DeviceId = deviceId,
                TargetHost = targetHost,
                TargetPort = request.TargetPort,
                Protocol = request.Protocol,
                TerminateTls = request.TerminateTls,
                HttpAccess = request.HttpAccess,
                CreatedByDeviceId = requestingDeviceId,
            };

            switch (request.Kind)
            {
                case TunnelKind.HttpHost:
                    tunnel.Hostname = AllocateHostname(request.Subdomain, config);
                    break;

                case TunnelKind.TcpHostAware:
                    tunnel.Hostname = AllocateHostname(request.Subdomain, config);
                    tunnel.PublicPort = config.MinecraftPort;
                    tunnel.Protocol = string.IsNullOrWhiteSpace(request.Protocol)
                        ? HostAwareProtocols.MinecraftJava
                        : request.Protocol;
                    break;

                case TunnelKind.PortForward:
                    tunnel.PublicPort = AllocatePublicPort(request.PublicPort, config);
                    tunnel.Transports = request.Transports == TransportProtocols.None
                        ? TransportProtocols.Tcp
                        : request.Transports;
                    break;

                default:
                    throw new TunnelValidationException($"Unknown tunnel kind '{request.Kind}'.");
            }

            if (string.IsNullOrWhiteSpace(tunnel.Name))
            {
                tunnel.Name = tunnel.Hostname is not null
                    ? tunnel.Hostname.Split('.')[0]
                    : $"port-{tunnel.PublicPort}";
            }

            _tunnels[tunnel.Id] = tunnel;
        }

        Persist();
        var snapshot = tunnel.Clone();
        TunnelAdded?.Invoke(snapshot);
        return snapshot;
    }

    public TunnelDefinition UpdateTunnel(UpdateTunnelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        TunnelDefinition snapshot;
        lock (_gate)
        {
            if (!_tunnels.TryGetValue(request.TunnelId, out var tunnel))
            {
                throw new TunnelValidationException("That tunnel no longer exists.");
            }

            if (request.Name is not null)
            {
                tunnel.Name = request.Name.Trim();
            }

            if (request.TargetHost is not null)
            {
                tunnel.TargetHost = request.TargetHost.Trim();
            }

            if (request.TargetPort is { } port)
            {
                if (port is < 1 or > 65535)
                {
                    throw new TunnelValidationException("Target port must be between 1 and 65535.");
                }

                tunnel.TargetPort = port;
            }

            if (request.Enabled is { } enabled)
            {
                tunnel.Enabled = enabled;
            }

            if (request.Transports is { } transports && tunnel.Kind == TunnelKind.PortForward)
            {
                if (transports == TransportProtocols.None)
                {
                    throw new TunnelValidationException("A port tunnel must carry TCP, UDP, or both.");
                }

                tunnel.Transports = transports;
            }

            if (request.HttpAccess is { } access && tunnel.Kind == TunnelKind.HttpHost)
            {
                tunnel.HttpAccess = access;
            }

            snapshot = tunnel.Clone();
        }

        Persist();
        TunnelUpdated?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>Records the provider's record id so the tunnel's DNS can be cleaned up on delete.</summary>
    public void SetDnsRecordId(string tunnelId, string? recordId)
    {
        lock (_gate)
        {
            if (!_tunnels.TryGetValue(tunnelId, out var tunnel))
            {
                return;
            }

            tunnel.DnsRecordId = recordId;
        }

        Persist();
    }

    public TunnelDefinition? RemoveTunnel(string tunnelId)
    {
        TunnelDefinition? removed;
        lock (_gate)
        {
            if (!_tunnels.Remove(tunnelId, out var tunnel))
            {
                return null;
            }

            removed = tunnel.Clone();
        }

        Persist();
        TunnelRemoved?.Invoke(removed);
        return removed;
    }

    /// <summary>
    /// Records traffic counters. Kept out of <see cref="Persist"/> on purpose: writing the whole
    /// registry on every chunk of forwarded data would turn a busy tunnel into a disk hammer.
    /// <see cref="Flush"/> captures them periodically instead.
    /// </summary>
    public TunnelDefinition? RecordTraffic(string tunnelId, long bytesIn, long bytesOut, int activeDelta)
    {
        TunnelDefinition snapshot;
        lock (_gate)
        {
            if (!_tunnels.TryGetValue(tunnelId, out var tunnel))
            {
                return null;
            }

            tunnel.BytesIn += bytesIn;
            tunnel.BytesOut += bytesOut;
            tunnel.ActiveConnections = Math.Max(0, tunnel.ActiveConnections + activeDelta);
            if (activeDelta > 0)
            {
                tunnel.TotalConnections += activeDelta;
            }

            tunnel.LastActiveAt = DateTimeOffset.UtcNow;
            snapshot = tunnel.Clone();
        }

        return snapshot;
    }

    /// <summary>Clears live connection counts — called when a device's link drops.</summary>
    public IReadOnlyList<TunnelDefinition> ResetActiveConnections(string deviceId)
    {
        lock (_gate)
        {
            var affected = new List<TunnelDefinition>();
            foreach (var tunnel in _tunnels.Values.Where(t => t.DeviceId == deviceId && t.ActiveConnections != 0))
            {
                tunnel.ActiveConnections = 0;
                affected.Add(tunnel.Clone());
            }

            return affected;
        }
    }

    // ---- Allocation ----------------------------------------------------------

    private string AllocateHostname(string? requestedLabel, ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BaseDomain))
        {
            throw new TunnelValidationException(
                "This server has no base domain configured, so hostname tunnels are unavailable. Use a TCP port tunnel instead.");
        }

        var baseDomain = config.BaseDomain.Trim().TrimEnd('.');

        if (!string.IsNullOrWhiteSpace(requestedLabel))
        {
            var label = TunnelNameGenerator.Sanitise(requestedLabel);
            if (!TunnelNameGenerator.IsValidLabel(label))
            {
                throw new TunnelValidationException(
                    "That subdomain contains characters DNS will not accept. Use letters, digits and dashes.");
            }

            var hostname = $"{label}.{baseDomain}";
            if (_tunnels.Values.Any(t => string.Equals(t.Hostname, hostname, StringComparison.OrdinalIgnoreCase)))
            {
                throw new TunnelValidationException($"'{hostname}' is already in use by another tunnel.");
            }

            return hostname;
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = $"{TunnelNameGenerator.NewLabel()}.{baseDomain}";
            if (_tunnels.Values.All(t => !string.Equals(t.Hostname, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        throw new TunnelValidationException("Could not find a free random subdomain. Try naming it yourself.");
    }

    private int AllocatePublicPort(int? requested, ServerConfig config)
    {
        if (requested is { } port)
        {
            if (port is < 1 or > 65535)
            {
                throw new TunnelValidationException("Public port must be between 1 and 65535.");
            }

            if (port == config.ControlPort)
            {
                throw new TunnelValidationException("That is the control port clients connect on; pick another.");
            }

            if (_tunnels.Values.Any(t => t.Kind == TunnelKind.PortForward && t.PublicPort == port))
            {
                throw new TunnelValidationException($"Public port {port} is already taken by another tunnel.");
            }

            return port;
        }

        var taken = _tunnels.Values
            .Where(t => t.Kind == TunnelKind.PortForward && t.PublicPort.HasValue)
            .Select(t => t.PublicPort!.Value)
            .ToHashSet();

        for (var candidate = config.TcpPortRangeStart; candidate <= config.TcpPortRangeEnd; candidate++)
        {
            if (candidate != config.ControlPort && taken.Add(candidate))
            {
                return candidate;
            }
        }

        throw new TunnelValidationException(
            $"Every port between {config.TcpPortRangeStart} and {config.TcpPortRangeEnd} is in use. Widen the range in settings.");
    }

    // ---- Persistence -----------------------------------------------------------

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        RegistryState? state;
        try
        {
            state = JsonSerializer.Deserialize<RegistryState>(File.ReadAllText(_path), JsonProtocol.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Registry at '{_path}' is not valid JSON: {ex.Message}", ex);
        }

        if (state is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var device in state.Devices)
            {
                // Nothing is connected yet at load time, whatever the last shutdown wrote.
                device.Info.Online = false;
                device.Info.ConnectedAt = null;
                device.Info.LatencyMs = null;
                _devices[device.Info.Id] = device;
            }

            foreach (var tunnel in state.Tunnels)
            {
                tunnel.ActiveConnections = 0;
                _tunnels[tunnel.Id] = tunnel;
            }
        }
    }

    /// <summary>Writes current state to disk. Safe to call often; it is a whole-file swap.</summary>
    public void Flush() => Persist();

    private void Persist()
    {
        RegistryState state;
        lock (_gate)
        {
            state = new RegistryState
            {
                Devices = [.. _devices.Values],
                Tunnels = [.. _tunnels.Values],
            };

            try
            {
                ServerPaths.EnsureCreated();
                var json = JsonSerializer.Serialize(state, JsonProtocol.PrettyOptions);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (IOException)
            {
                // A failed write must not take the gateway down; in-memory state stays correct
                // and the next mutation retries.
            }
        }
    }
}

/// <summary>Rejection with a message intended to be shown to a person.</summary>
public sealed class TunnelValidationException(string message) : Exception(message);
