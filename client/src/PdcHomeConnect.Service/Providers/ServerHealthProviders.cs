using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Failed scheduled tasks (outside the \Microsoft folder).</summary>
public sealed class ScheduledTasksProvider : EntityProvider
{
    // Result codes that are not failures: running, not yet run, queued, terminated by user, already running.
    private static readonly HashSet<uint> NotFailures = [0, 0x41301, 0x41303, 0x41325, 0x41306, 0x8004131F, 0x41300, 0x41302];
    private readonly ServiceConfig _config;
    private readonly DeviceInfoService _device;

    public ScheduledTasksProvider(EntityHub hub, ServiceConfig config, DeviceInfoService device, ILogger<ScheduledTasksProvider> logger)
        : base("tasks", hub, logger)
    {
        _config = config;
        _device = device;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.ScheduledTasks, _device.IsServer))
        {
            return Task.CompletedTask;
        }

        Publish([new() { Key = "failed_tasks", Platform = Platforms.Sensor, TranslationKey = "failed_tasks", Name = "Failed scheduled tasks", StateClass = Ha.Measurement, Precision = 0, EntityCategory = Ha.Diagnostic }]);
        return RunPeriodicAsync(TimeSpan.FromMinutes(10), _ =>
        {
            var failed = new JsonArray();
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return Task.CompletedTask;
            }

            dynamic scheduler = Activator.CreateInstance(type)!;
            scheduler.Connect();
            Walk(scheduler.GetFolder("\\"), failed, 0);
            Set("failed_tasks", (long)failed.Count, new JsonObject { ["tasks"] = failed });
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private void Walk(dynamic folder, JsonArray failed, int depth)
    {
        string path = folder.Path;
        if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase) || depth > 8)
        {
            return;
        }

        dynamic tasks = folder.GetTasks(1);
        int count = tasks.Count;
        for (var i = 1; i <= count; i++)
        {
            try
            {
                dynamic task = tasks.Item(i);
                bool enabled = task.Enabled;
                DateTime lastRun = task.LastRunTime;
                var result = unchecked((uint)(int)task.LastTaskResult);
                if (enabled && lastRun.Year > 2000 && !NotFailures.Contains(result) && failed.Count < 50)
                {
                    failed.Add(new JsonObject
                    {
                        ["name"] = (string)task.Name,
                        ["path"] = (string)task.Path,
                        ["last_run"] = new DateTimeOffset(lastRun).ToUniversalTime().ToString("O"),
                        ["result"] = $"0x{result:X8}",
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Task {Index} in {Folder} skipped", i, path);
            }
        }

        dynamic folders = folder.GetFolders(0);
        int folderCount = folders.Count;
        for (var i = 1; i <= folderCount; i++)
        {
            Walk(folders.Item(i), failed, depth + 1);
        }
    }
}

/// <summary>Last Windows Server Backup result (from the Microsoft-Windows-Backup event log).</summary>
public sealed class BackupProvider : EntityProvider
{
    private static readonly int[] FailureIds = [5, 8, 9, 17, 22, 49, 50, 52, 100, 517, 518, 521, 527, 528, 544, 545, 546, 561, 564, 612];
    private readonly ServiceConfig _config;

    public BackupProvider(EntityHub hub, ServiceConfig config, ILogger<BackupProvider> logger)
        : base("backup", hub, logger)
    {
        _config = config;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.Backup, LogHasEvents()))
        {
            return Task.CompletedTask;
        }

        Publish([new() { Key = "last_backup", Platform = Platforms.Sensor, TranslationKey = "last_backup", Name = "Last backup", DeviceClass = "timestamp" }]);
        return RunPeriodicAsync(TimeSpan.FromMinutes(15), _ =>
        {
            var success = Latest("*[System[(EventID=4)]]");
            var failure = Latest($"*[System[({string.Join(" or ", FailureIds.Select(id => $"EventID={id}"))})]]");
            var lastResult = success is null && failure is null
                ? "none"
                : failure is not null && (success is null || failure.Value.Time > success.Value.Time) ? "failed" : "success";
            Set("last_backup", success is null ? null : Timestamp(success.Value.Time), Attrs(
                ("last_result", lastResult),
                ("last_failure", failure?.Time),
                ("last_failure_event_id", failure?.Id)));
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private static bool LogHasEvents() => Latest("*") is not null;

    private static (DateTimeOffset Time, int Id)? Latest(string xpath)
    {
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Backup", PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return record?.TimeCreated is { } time ? (new DateTimeOffset(time), record.Id) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Health of physical disks, storage pools and virtual disks (Storage Spaces).</summary>
public sealed class StorageHealthProvider : EntityProvider
{
    private const string Namespace = @"root\Microsoft\Windows\Storage";
    private readonly ServiceConfig _config;
    private string _signature = string.Empty;

    public StorageHealthProvider(EntityHub hub, ServiceConfig config, ILogger<StorageHealthProvider> logger)
        : base("storage", hub, logger)
    {
        _config = config;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.StorageHealth, true))
        {
            return Task.CompletedTask;
        }

        return RunPeriodicAsync(TimeSpan.FromMinutes(10), _ =>
        {
            Update();
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private sealed record Item(string Key, string TranslationKey, string PlaceholderName, string Label, string Health, JsonObject Attributes);

    private void Update()
    {
        var items = new List<Item>();
        var keys = new HashSet<string>();
        string UniqueKey(string baseKey)
        {
            var key = baseKey;
            for (var n = 2; !keys.Add(key); n++)
            {
                key = $"{baseKey}_{n}";
            }
            return key;
        }

        foreach (var disk in Query("SELECT FriendlyName, SerialNumber, MediaType, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk"))
        {
            var busType = Convert.ToInt32(disk["BusType"] ?? 0);
            if (busType is 14 or 15)
            {
                continue; // Virtual / file backed virtual disks
            }

            var name = (disk["FriendlyName"] as string)?.Trim() ?? "Disk";
            items.Add(new Item(
                UniqueKey(Slug.Key("drive", name)) + "_health",
                "drive_health",
                "drive",
                name,
                Health(disk["HealthStatus"]),
                new JsonObject
                {
                    ["media_type"] = Convert.ToInt32(disk["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "unknown" },
                    ["bus_type"] = BusType(busType),
                    ["size_gb"] = Math.Round(Convert.ToDouble(disk["Size"] ?? 0) / 1e9, 0),
                    ["operational_status"] = Operational(disk["OperationalStatus"]),
                }));
        }

        foreach (var pool in Query("SELECT FriendlyName, HealthStatus, OperationalStatus, Size, AllocatedSize, IsPrimordial FROM MSFT_StoragePool WHERE IsPrimordial = FALSE"))
        {
            var name = pool["FriendlyName"] as string ?? "Pool";
            items.Add(new Item(UniqueKey(Slug.Key("storage_pool", name)), "storage_pool_health", "pool", name, Health(pool["HealthStatus"]), new JsonObject
            {
                ["size_gb"] = Math.Round(Convert.ToDouble(pool["Size"] ?? 0) / 1e9, 0),
                ["allocated_gb"] = Math.Round(Convert.ToDouble(pool["AllocatedSize"] ?? 0) / 1e9, 0),
                ["operational_status"] = Operational(pool["OperationalStatus"]),
            }));
        }

        foreach (var virtualDisk in Query("SELECT FriendlyName, HealthStatus, OperationalStatus, Size FROM MSFT_VirtualDisk"))
        {
            var name = virtualDisk["FriendlyName"] as string ?? "Virtual disk";
            items.Add(new Item(UniqueKey(Slug.Key("virtual_disk", name)), "virtual_disk_health", "disk", name, Health(virtualDisk["HealthStatus"]), new JsonObject
            {
                ["size_gb"] = Math.Round(Convert.ToDouble(virtualDisk["Size"] ?? 0) / 1e9, 0),
                ["operational_status"] = Operational(virtualDisk["OperationalStatus"]),
            }));
        }

        var signature = string.Join('|', items.Select(i => i.Key + i.Label));
        if (signature != _signature)
        {
            Publish(items.Select(i => new EntityDescriptor
            {
                Key = i.Key,
                Platform = Platforms.Sensor,
                TranslationKey = i.TranslationKey,
                Placeholders = new() { [i.PlaceholderName] = i.Label },
                Name = i.TranslationKey switch
                {
                    "drive_health" => $"{i.Label} health",
                    "storage_pool_health" => $"Storage pool {i.Label}",
                    _ => $"Virtual disk {i.Label}",
                },
                DeviceClass = "enum",
                Options = ["healthy", "warning", "unhealthy", "unknown"],
                EntityCategory = Ha.Diagnostic,
            }));
            _signature = signature;
        }

        foreach (var item in items)
        {
            Set(item.Key, item.Health, item.Attributes);
        }
    }

    private List<ManagementBaseObject> Query(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Storage query failed: {Query}", wql);
            return [];
        }
    }

    private static string Health(object? value) => Convert.ToInt32(value ?? 5) switch
    {
        0 => "healthy",
        1 => "warning",
        2 => "unhealthy",
        _ => "unknown",
    };

    private static string BusType(int value) => value switch
    {
        1 => "SCSI",
        3 => "ATA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        17 => "NVMe",
        _ => "other",
    };

    private static string? Operational(object? value)
    {
        if (value is not ushort[] states || states.Length == 0)
        {
            return null;
        }

        return string.Join(", ", states.Select(s => s switch
        {
            2 => "OK",
            3 => "Degraded",
            5 => "Predictive Failure",
            6 => "Error",
            10 => "Stopped",
            11 => "In Service",
            13 => "Lost Communication",
            0xD00D => "Transient Error",
            0xD00E => "Incomplete",
            _ => s.ToString(),
        }));
    }
}
