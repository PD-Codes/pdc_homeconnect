namespace PdcHomeConnect.Core.Protocol;

/// <summary>Information about this PC, sent in the hello message and on changes.</summary>
public sealed class DeviceInfo
{
    /// <summary>Stable device id (derived from the Windows MachineGuid).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Hostname { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? Os { get; init; }

    public string? OsVersion { get; init; }

    public bool IsServer { get; init; }

    /// <summary>Client version (semantic version without "v").</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 fingerprint (hex, lowercase) of the TLS certificate.</summary>
    public string? CertSha256 { get; init; }

    /// <summary>MAC addresses of physical adapters (for Wake-on-LAN), formatted AA:BB:CC:DD:EE:FF.</summary>
    public List<string>? Macs { get; init; }

    public bool AgentConnected { get; init; }

    public string? AgentUser { get; init; }
}
