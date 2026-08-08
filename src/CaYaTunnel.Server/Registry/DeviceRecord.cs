using CaYaTunnel.Core.Models;

namespace CaYaTunnel.Server.Registry;

/// <summary>
/// Server-private view of a device: the public <see cref="DeviceInfo"/> plus credential material
/// that never leaves this machine.
/// </summary>
public sealed class DeviceRecord
{
    public required DeviceInfo Info { get; set; }

    /// <summary>
    /// Set when the operator provisioned a build for this specific device. A per-device key makes
    /// revoking that one device meaningful: its build stops working while everyone else's keeps
    /// going. Devices that enrolled with the shared key have no per-device key, and for those
    /// the real kill switch is rotating the shared enrollment key.
    /// </summary>
    public string? KeyHash { get; set; }

    public string? KeySalt { get; set; }

    public bool HasDeviceKey => !string.IsNullOrEmpty(KeyHash) && !string.IsNullOrEmpty(KeySalt);

    /// <summary>Note the operator can attach in the server UI.</summary>
    public string? Notes { get; set; }
}

/// <summary>What gets written to registry.json.</summary>
public sealed class RegistryState
{
    public List<DeviceRecord> Devices { get; set; } = [];

    public List<TunnelDefinition> Tunnels { get; set; } = [];
}
