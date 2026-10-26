using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using PdcHomeConnect.Service.Native;

namespace PdcHomeConnect.Service.Providers;

/// <summary>CPU load, memory, disks, network throughput, battery and last boot.</summary>
public sealed class SystemProvider : EntityProvider
{
    private readonly ServiceConfig _config;
    private readonly SystemEvents _events;
    private PerformanceCounter? _cpuCounter;
    private (long Idle, long Kernel, long User)? _lastTimes;
    private readonly Dictionary<string, (long Rx, long Tx)> _lastNet = [];
    private DateTimeOffset _lastNetSample;
    private string _publishedSignature = string.Empty;
    private DateTimeOffset _bootTime;

    public SystemProvider(EntityHub hub, ServiceConfig config, SystemEvents events, ILogger<SystemProvider> logger)
        : base("system", hub, logger)
    {
        _config = config;
        _events = events;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", readOnly: true);
            _cpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            Logger.LogInformation("Processor utility counter unavailable ({Message}), using system times", ex.Message);
            _cpuCounter = null;
        }

        ComputeBootTime();
        _events.Power += status =>
        {
            if (status is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
            {
                ComputeBootTime();
                _lastNet.Clear();
            }
        };

        return RunPeriodicAsync(() => TimeSpan.FromSeconds(Math.Clamp(_config.StatsInterval, 1, 300)), Tick, cancellationToken);
    }

    private void ComputeBootTime()
    {
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        // Round to the minute so the value does not jitter.
        _bootTime = new DateTimeOffset(boot.Year, boot.Month, boot.Day, boot.Hour, boot.Minute, 0, TimeSpan.Zero);
    }

    private Task Tick(CancellationToken cancellationToken)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && SafeReady(d))
            .ToList();
        var hasBattery = Win32.GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryFlag != 255;
        var signature = string.Join('|', drives.Select(d => d.Name)) + (hasBattery ? "|battery" : string.Empty);
        if (signature != _publishedSignature)
        {
            Publish(BuildDescriptors(drives, hasBattery));
            _publishedSignature = signature;
        }

        Set("cpu_load", ReadCpu(), 0);

        var memory = new Win32.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MemoryStatusEx>() };
        if (Win32.GlobalMemoryStatusEx(ref memory))
        {
            var totalGb = memory.TotalPhys / 1073741824.0;
            var usedGb = (memory.TotalPhys - memory.AvailPhys) / 1073741824.0;
            var attributes = Attrs(("used_gb", Math.Round(usedGb, 1)), ("total_gb", Math.Round(totalGb, 1)), ("available_gb", Math.Round(memory.AvailPhys / 1073741824.0, 1)));
            Set("memory_usage", usedGb / totalGb * 100, 0, attributes);
            Set("memory_used", usedGb, 1);
        }

        foreach (var drive in drives)
        {
            try
            {
                var total = drive.TotalSize;
                var free = drive.TotalFreeSpace;
                Set(DiskKey(drive), total > 0 ? (total - free) * 100.0 / total : null, 0, Attrs(
                    ("free_gb", Math.Round(free / 1073741824.0, 1)),
                    ("used_gb", Math.Round((total - free) / 1073741824.0, 1)),
                    ("total_gb", Math.Round(total / 1073741824.0, 1)),
                    ("label", string.IsNullOrEmpty(drive.VolumeLabel) ? null : drive.VolumeLabel),
                    ("file_system", drive.DriveFormat)));
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Drive {Drive} not readable", drive.Name);
            }
        }

        ReadNetwork();
        Set("last_boot", Timestamp(_bootTime));

        if (hasBattery)
        {
            Set("battery", power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null, 0);
            Set("battery_charging", (power.BatteryFlag & 8) != 0);
            Set("power_plugged", power.ACLineStatus == 1);
        }

        return Task.CompletedTask;
    }

    private static bool SafeReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch
        {
            return false;
        }
    }

    private static string DiskKey(DriveInfo drive) => Slug.Key("disk_usage", drive.Name.TrimEnd('\\', ':'));

    private List<EntityDescriptor> BuildDescriptors(List<DriveInfo> drives, bool hasBattery)
    {
        var list = new List<EntityDescriptor>
        {
            new() { Key = "cpu_load", Platform = Platforms.Sensor, TranslationKey = "cpu_load", Name = "CPU load", Unit = "%", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "memory_usage", Platform = Platforms.Sensor, TranslationKey = "memory_usage", Name = "Memory usage", Unit = "%", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "memory_used", Platform = Platforms.Sensor, TranslationKey = "memory_used", Name = "Memory used", DeviceClass = "data_size", Unit = "GiB", StateClass = Ha.Measurement, Precision = 1, EnabledByDefault = false },
            new() { Key = "network_download", Platform = Platforms.Sensor, TranslationKey = "network_download", Name = "Network download", DeviceClass = "data_rate", Unit = "kB/s", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "network_upload", Platform = Platforms.Sensor, TranslationKey = "network_upload", Name = "Network upload", DeviceClass = "data_rate", Unit = "kB/s", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "last_boot", Platform = Platforms.Sensor, TranslationKey = "last_boot", Name = "Last boot", DeviceClass = "timestamp", EntityCategory = Ha.Diagnostic },
        };

        foreach (var drive in drives)
        {
            var letter = drive.Name.TrimEnd('\\');
            list.Add(new EntityDescriptor
            {
                Key = DiskKey(drive),
                Platform = Platforms.Sensor,
                TranslationKey = "disk_usage",
                Placeholders = new() { ["disk"] = letter },
                Name = $"Disk {letter} usage",
                Unit = "%",
                StateClass = Ha.Measurement,
                Precision = 0,
            });
        }

        if (hasBattery)
        {
            list.Add(new() { Key = "battery", Platform = Platforms.Sensor, TranslationKey = "battery", Name = "Battery", DeviceClass = "battery", Unit = "%", StateClass = Ha.Measurement, Precision = 0 });
            list.Add(new() { Key = "battery_charging", Platform = Platforms.BinarySensor, TranslationKey = "battery_charging", Name = "Charging", DeviceClass = "battery_charging" });
            list.Add(new() { Key = "power_plugged", Platform = Platforms.BinarySensor, TranslationKey = "power_plugged", Name = "Power plugged in", DeviceClass = "plug" });
        }

        return list;
    }

    private double? ReadCpu()
    {
        if (_cpuCounter is not null)
        {
            try
            {
                return Math.Clamp(_cpuCounter.NextValue(), 0, 100);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "CPU counter failed, switching to system times");
                _cpuCounter.Dispose();
                _cpuCounter = null;
            }
        }

        if (!Win32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var previous = _lastTimes;
        _lastTimes = (idle, kernel, user);
        if (previous is null)
        {
            return null;
        }

        var idleDelta = idle - previous.Value.Idle;
        var totalDelta = (kernel - previous.Value.Kernel) + (user - previous.Value.User);
        return totalDelta <= 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
    }

    private void ReadNetwork()
    {
        var now = DateTimeOffset.UtcNow;
        long rxRate = 0;
        long txRate = 0;
        var elapsed = (now - _lastNetSample).TotalSeconds;
        var adapters = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                // Only adapters with a default gateway carry the real traffic (avoids counting
                // the physical NIC and its Hyper-V vSwitch twice).
                if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)))
                {
                    continue;
                }

                var stats = nic.GetIPStatistics();
                seen.Add(nic.Id);
                if (_lastNet.TryGetValue(nic.Id, out var last) && elapsed > 0.5)
                {
                    var rx = Math.Max(0, stats.BytesReceived - last.Rx);
                    var tx = Math.Max(0, stats.BytesSent - last.Tx);
                    rxRate += (long)(rx / elapsed);
                    txRate += (long)(tx / elapsed);
                }

                _lastNet[nic.Id] = (stats.BytesReceived, stats.BytesSent);
                var ipv4 = properties.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                adapters.Add(new JsonObject
                {
                    ["name"] = nic.Name,
                    ["ip"] = ipv4,
                    ["speed_mbit"] = nic.Speed > 0 ? nic.Speed / 1_000_000 : null,
                });
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Adapter {Name} skipped", nic.Name);
            }
        }

        foreach (var stale in _lastNet.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _lastNet.Remove(stale);
        }

        var first = _lastNetSample == default;
        _lastNetSample = now;
        if (first)
        {
            return;
        }

        Set("network_download", rxRate / 1000.0, 0, new JsonObject { ["adapters"] = adapters });
        Set("network_upload", txRate / 1000.0, 0);
    }
}
