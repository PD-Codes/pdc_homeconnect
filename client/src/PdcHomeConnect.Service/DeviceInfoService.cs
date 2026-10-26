using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Service;

/// <summary>Builds the device info sent to Home Assistant.</summary>
public sealed class DeviceInfoService
{
    private readonly ServiceConfig _config;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private DeviceInfo? _cached;

    public DeviceInfoService(ServiceConfig config, ILogger<DeviceInfoService> logger)
    {
        _config = config;
        _logger = logger;
        DeviceId = ComputeDeviceId();
        Hostname = Environment.MachineName;
        (Manufacturer, Model) = ReadHardware();
        (OsName, OsVersion, IsServer) = ReadOs();
    }

    public string DeviceId { get; }

    public string Hostname { get; }

    public string Name => string.IsNullOrWhiteSpace(_config.DeviceName) ? Hostname : _config.DeviceName.Trim();

    public string? Manufacturer { get; }

    public string? Model { get; }

    public string OsName { get; }

    public string OsVersion { get; }

    public bool IsServer { get; }

    public string Version { get; } = typeof(DeviceInfoService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string? CertificateFingerprint { get; set; }

    public bool AgentConnected { get; private set; }

    public string? AgentUser { get; private set; }

    public void SetAgent(bool connected, string? user)
    {
        lock (_gate)
        {
            AgentConnected = connected;
            AgentUser = user;
            _cached = null;
        }
    }

    public DeviceInfo Current
    {
        get
        {
            lock (_gate)
            {
                return _cached ??= new DeviceInfo
                {
                    Id = DeviceId,
                    Name = Name,
                    Hostname = Hostname,
                    Manufacturer = Manufacturer,
                    Model = Model,
                    Os = OsName,
                    OsVersion = OsVersion,
                    IsServer = IsServer,
                    Version = Version,
                    CertSha256 = CertificateFingerprint,
                    Macs = ReadMacs(),
                    AgentConnected = AgentConnected,
                    AgentUser = AgentUser,
                };
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    /// <summary>Stable id derived from the Windows MachineGuid (survives client reinstalls).</summary>
    private static string ComputeDeviceId()
    {
        string? guid = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            guid = key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            // Fall back to the host name.
        }

        var source = guid ?? Environment.MachineName;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("pdc-homeconnect:" + source)))[..16];
    }

    private (string? Manufacturer, string? Model) ReadHardware()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var item in searcher.Get())
            {
                var manufacturer = Clean(item["Manufacturer"] as string);
                var model = Clean(item["Model"] as string);
                return (manufacturer, model);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read hardware info");
        }

        return (null, null);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private (string Name, string Version, bool IsServer) ReadOs()
    {
        var name = "Windows";
        var version = Environment.OSVersion.Version.ToString();
        var isServer = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, ProductType FROM Win32_OperatingSystem");
            foreach (var item in searcher.Get())
            {
                name = (item["Caption"] as string)?.Replace("Microsoft ", string.Empty).Trim() ?? name;
                version = item["Version"] as string ?? version;
                isServer = Convert.ToUInt32(item["ProductType"] ?? 1u) != 1;
            }

            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("DisplayVersion") is string displayVersion)
            {
                name = $"{name} {displayVersion}";
            }
            if (key?.GetValue("UBR") is int ubr)
            {
                version = $"{version}.{ubr}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read OS info");
        }

        return (name, version, isServer);
    }

    private static List<string> ReadMacs()
    {
        var macs = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT))
                {
                    continue;
                }

                var description = nic.Description;
                if (description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 6 || bytes.All(b => b == 0))
                {
                    continue;
                }

                var mac = string.Join(':', bytes.Select(b => b.ToString("X2")));
                // Adapters with a gateway (the active connection) first.
                var hasGateway = nic.OperationalStatus == OperationalStatus.Up &&
                                 nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (hasGateway)
                {
                    macs.Insert(0, mac);
                }
                else
                {
                    macs.Add(mac);
                }
            }
        }
        catch
        {
            // No MACs: Wake-on-LAN is unavailable.
        }

        return macs.Distinct().Take(4).ToList();
    }
}
