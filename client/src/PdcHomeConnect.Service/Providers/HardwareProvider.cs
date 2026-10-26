using LibreHardwareMonitor.Hardware;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using PdcHomeConnect.Service.Native;

namespace PdcHomeConnect.Service.Providers;

/// <summary>
/// Temperatures, fans, GPUs and drive SMART data.
/// NVIDIA GPUs are read via NVML (part of the driver); everything else via LibreHardwareMonitor.
/// CPU temperatures and mainboard fans need the PawnIO driver (installed by the setup).
/// </summary>
public sealed class HardwareProvider : EntityProvider
{
    private sealed record Reading(EntityDescriptor Descriptor, Func<double?> Read, int Digits);

    private readonly ServiceConfig _config;
    private readonly List<Reading> _readings = [];
    private Computer? _computer;
    private bool _nvml;

    public HardwareProvider(EntityHub hub, ServiceConfig config, ILogger<HardwareProvider> logger)
        : base("hardware", hub, logger)
    {
        _config = config;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        _nvml = Nvml.TryLoad();
        Logger.LogInformation("NVML (NVIDIA) {State}", _nvml ? "available" : "not available");

        if (_config.HardwareSensors)
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                    IsMemoryEnabled = false,
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false,
                    IsPsuEnabled = false,
                    IsBatteryEnabled = false,
                };
                _computer.Open();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "LibreHardwareMonitor could not be initialized; temperatures are unavailable");
                _computer = null;
            }
        }

        // Two samples so that sensors report values before discovery.
        UpdateHardware();
        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        UpdateHardware();
        Discover();
        Publish(_readings.Select(r => r.Descriptor));
        Logger.LogInformation("Hardware sensors: {Count}", _readings.Count);

        try
        {
            await RunPeriodicAsync(() => TimeSpan.FromSeconds(Math.Clamp(_config.HardwareInterval, 1, 300)), _ =>
            {
                UpdateHardware();
                foreach (var reading in _readings)
                {
                    double? value;
                    try
                    {
                        value = reading.Read();
                    }
                    catch
                    {
                        value = null;
                    }

                    Set(reading.Descriptor.Key, value, reading.Digits);
                }
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _computer?.Close();
                if (_nvml)
                {
                    Nvml.Shutdown();
                }
            }
            catch
            {
                // Ignore shutdown errors.
            }
        }
    }

    private void UpdateHardware()
    {
        if (_computer is null)
        {
            return;
        }

        foreach (var hardware in _computer.Hardware)
        {
            try
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware)
                {
                    sub.Update();
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Update of {Hardware} failed", hardware.Name);
            }
        }
    }

    private void Discover()
    {
        var gpuIndex = 0;

        if (_nvml && Nvml.DeviceGetCount(out var count) == Nvml.Result.Success)
        {
            for (uint i = 0; i < count; i++)
            {
                if (Nvml.DeviceGetHandleByIndex(i, out var device) != Nvml.Result.Success)
                {
                    continue;
                }

                gpuIndex++;
                AddNvidiaGpu(device, gpuIndex);
            }
        }

        if (_computer is null)
        {
            return;
        }

        var hardware = _computer.Hardware.ToList();
        foreach (var cpu in hardware.Where(h => h.HardwareType == HardwareType.Cpu).Take(1))
        {
            AddCpu(cpu);
        }

        foreach (var gpu in hardware.Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia))
        {
            if (gpu.HardwareType == HardwareType.GpuNvidia && _nvml)
            {
                continue;
            }

            if (gpu.HardwareType == HardwareType.GpuIntel && FindSensor(gpu, SensorType.Load, "GPU Core", "D3D 3D") is null)
            {
                continue;
            }

            gpuIndex++;
            AddLhmGpu(gpu, gpuIndex);
        }

        var fanNames = new HashSet<string>();
        foreach (var board in hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
        {
            foreach (var chip in board.SubHardware)
            {
                foreach (var fan in chip.Sensors.Where(s => s.SensorType == SensorType.Fan && s.Value is > 0))
                {
                    var name = fan.Name;
                    if (!fanNames.Add(name))
                    {
                        continue;
                    }

                    var sensor = fan;
                    _readings.Add(new Reading(new EntityDescriptor
                    {
                        Key = Slug.Key("fan", name),
                        Platform = Platforms.Sensor,
                        TranslationKey = "fan",
                        Placeholders = new() { ["name"] = name },
                        Name = $"Fan {name}",
                        Unit = "rpm",
                        StateClass = Ha.Measurement,
                        Precision = 0,
                        EntityCategory = Ha.Diagnostic,
                    }, () => sensor.Value, 0));
                }
            }
        }

        var driveKeys = new HashSet<string>();
        foreach (var storage in hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            var baseKey = Slug.Key("drive", storage.Name);
            var key = baseKey;
            for (var n = 2; !driveKeys.Add(key); n++)
            {
                key = $"{baseKey}_{n}";
            }

            var temperature = FindSensor(storage, SensorType.Temperature, "Temperature", "Composite Temperature") ??
                              storage.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value is > 0);
            if (temperature is not null && temperature.Value is > 0)
            {
                var sensor = temperature;
                _readings.Add(new Reading(new EntityDescriptor
                {
                    Key = $"{key}_temperature",
                    Platform = Platforms.Sensor,
                    TranslationKey = "drive_temperature",
                    Placeholders = new() { ["drive"] = storage.Name },
                    Name = $"{storage.Name} temperature",
                    DeviceClass = "temperature",
                    Unit = "°C",
                    StateClass = Ha.Measurement,
                    Precision = 0,
                }, () => sensor.Value, 0));
            }

            var percentageUsed = FindSensor(storage, SensorType.Level, "Percentage Used");
            var remaining = FindSensor(storage, SensorType.Level, "Remaining Life", "Life");
            if (percentageUsed is not null || remaining is not null)
            {
                Func<double?> read = percentageUsed is not null
                    ? () => percentageUsed.Value is { } used ? Math.Clamp(100 - used, 0, 100) : null
                    : () => remaining!.Value;
                _readings.Add(new Reading(new EntityDescriptor
                {
                    Key = $"{key}_life_remaining",
                    Platform = Platforms.Sensor,
                    TranslationKey = "drive_life_remaining",
                    Placeholders = new() { ["drive"] = storage.Name },
                    Name = $"{storage.Name} life remaining",
                    Unit = "%",
                    StateClass = Ha.Measurement,
                    Precision = 0,
                    EntityCategory = Ha.Diagnostic,
                }, read, 0));
            }
        }
    }

    private void AddCpu(IHardware cpu)
    {
        var temperature = FindSensor(cpu, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core Average", "Core Max", "CPU Cores")
                          ?? cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value is > 0);
        if (temperature is not null && temperature.Value is > 0)
        {
            var sensor = temperature;
            _readings.Add(new Reading(new EntityDescriptor
            {
                Key = "cpu_temperature",
                Platform = Platforms.Sensor,
                TranslationKey = "cpu_temperature",
                Name = "CPU temperature",
                DeviceClass = "temperature",
                Unit = "°C",
                StateClass = Ha.Measurement,
                Precision = 0,
            }, () => sensor.Value, 0));
        }

        var power = FindSensor(cpu, SensorType.Power, "Package", "CPU Package");
        if (power is not null && power.Value is > 0)
        {
            var sensor = power;
            _readings.Add(new Reading(new EntityDescriptor
            {
                Key = "cpu_power",
                Platform = Platforms.Sensor,
                TranslationKey = "cpu_power",
                Name = "CPU power",
                DeviceClass = "power",
                Unit = "W",
                StateClass = Ha.Measurement,
                Precision = 0,
            }, () => sensor.Value, 0));
        }

        var clocks = cpu.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)).ToList();
        if (clocks.Count > 0)
        {
            _readings.Add(new Reading(new EntityDescriptor
            {
                Key = "cpu_clock",
                Platform = Platforms.Sensor,
                TranslationKey = "cpu_clock",
                Name = "CPU clock",
                DeviceClass = "frequency",
                Unit = "MHz",
                StateClass = Ha.Measurement,
                Precision = 0,
                EnabledByDefault = false,
            }, () =>
            {
                var values = clocks.Where(c => c.Value is > 0).Select(c => (double)c.Value!.Value).ToList();
                return values.Count == 0 ? null : values.Average();
            }, 0));
        }
    }

    private (string Prefix, string TranslationPrefix, Dictionary<string, string>? Placeholders, string NamePrefix) GpuNaming(int index) =>
        index == 1
            ? ("gpu_", "gpu_", null, "GPU")
            : ($"gpu{index}_", "gpu_n_", new Dictionary<string, string> { ["index"] = index.ToString() }, $"GPU {index}");

    private void AddGpuReading(int index, string suffix, string name, string? deviceClass, string? unit, int digits, bool enabled, Func<double?> read, string? category = null)
    {
        var (prefix, translationPrefix, placeholders, namePrefix) = GpuNaming(index);
        _readings.Add(new Reading(new EntityDescriptor
        {
            Key = prefix + suffix,
            Platform = Platforms.Sensor,
            TranslationKey = translationPrefix + suffix,
            Placeholders = placeholders,
            Name = $"{namePrefix} {name}",
            DeviceClass = deviceClass,
            Unit = unit,
            StateClass = Ha.Measurement,
            Precision = digits,
            EnabledByDefault = enabled,
            EntityCategory = category,
        }, read, digits));
    }

    private void AddNvidiaGpu(nint device, int index)
    {
        var name = Nvml.GetName(device);
        Logger.LogInformation("NVIDIA GPU {Index}: {Name}", index, name);
        AddGpuReading(index, "load", "load", null, "%", 0, true,
            () => Nvml.DeviceGetUtilizationRates(device, out var u) == Nvml.Result.Success ? u.Gpu : null);
        if (Nvml.DeviceGetTemperature(device, 0, out _) == Nvml.Result.Success)
        {
            AddGpuReading(index, "temperature", "temperature", "temperature", "°C", 0, true,
                () => Nvml.DeviceGetTemperature(device, 0, out var t) == Nvml.Result.Success ? t : null);
        }
        AddGpuReading(index, "memory_usage", "memory usage", null, "%", 0, true,
            () => Nvml.DeviceGetMemoryInfo(device, out var m) == Nvml.Result.Success && m.Total > 0 ? m.Used * 100.0 / m.Total : null);
        AddGpuReading(index, "memory_used", "memory used", "data_size", "MiB", 0, false,
            () => Nvml.DeviceGetMemoryInfo(device, out var m) == Nvml.Result.Success ? m.Used / 1048576.0 : null);
        if (Nvml.DeviceGetPowerUsage(device, out _) == Nvml.Result.Success)
        {
            AddGpuReading(index, "power", "power", "power", "W", 0, true,
                () => Nvml.DeviceGetPowerUsage(device, out var p) == Nvml.Result.Success ? p / 1000.0 : null);
        }
        if (Nvml.DeviceGetFanSpeed(device, out _) == Nvml.Result.Success)
        {
            AddGpuReading(index, "fan", "fan", null, "%", 0, true,
                () => Nvml.DeviceGetFanSpeed(device, out var f) == Nvml.Result.Success ? f : null);
        }
        AddGpuReading(index, "clock", "clock", "frequency", "MHz", 0, false,
            () => Nvml.DeviceGetClockInfo(device, 0, out var c) == Nvml.Result.Success ? c : null);
    }

    private void AddLhmGpu(IHardware gpu, int index)
    {
        Logger.LogInformation("GPU {Index}: {Name}", index, gpu.Name);
        var load = FindSensor(gpu, SensorType.Load, "GPU Core", "D3D 3D");
        if (load is not null)
        {
            AddGpuReading(index, "load", "load", null, "%", 0, true, () => load.Value);
        }

        var temperature = FindSensor(gpu, SensorType.Temperature, "GPU Core");
        if (temperature is not null && temperature.Value is > 0)
        {
            AddGpuReading(index, "temperature", "temperature", "temperature", "°C", 0, true, () => temperature.Value);
        }

        var hotspot = FindSensor(gpu, SensorType.Temperature, "GPU Hot Spot");
        if (hotspot is not null && hotspot.Value is > 0)
        {
            AddGpuReading(index, "hotspot_temperature", "hotspot temperature", "temperature", "°C", 0, false, () => hotspot.Value);
        }

        var used = FindSensor(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        var total = FindSensor(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        if (used is not null && total is not null)
        {
            AddGpuReading(index, "memory_usage", "memory usage", null, "%", 0, true,
                () => total.Value is > 0 && used.Value is { } u ? u * 100.0 / total.Value.Value : null);
        }

        if (used is not null)
        {
            AddGpuReading(index, "memory_used", "memory used", "data_size", "MiB", 0, false, () => used.Value);
        }

        var power = FindSensor(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
        if (power is not null && power.Value is > 0)
        {
            AddGpuReading(index, "power", "power", "power", "W", 0, true, () => power.Value);
        }

        var fan = FindSensor(gpu, SensorType.Control, "GPU Fan");
        if (fan is not null)
        {
            AddGpuReading(index, "fan", "fan", null, "%", 0, true, () => fan.Value);
        }

        var clock = FindSensor(gpu, SensorType.Clock, "GPU Core");
        if (clock is not null)
        {
            AddGpuReading(index, "clock", "clock", "frequency", "MHz", 0, false, () => clock.Value);
        }
    }

    private static ISensor? FindSensor(IHardware hardware, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var sensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (sensor is not null)
            {
                return sensor;
            }
        }

        return null;
    }
}
