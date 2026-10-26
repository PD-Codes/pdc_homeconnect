using System.Management;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Hyper-V virtual machines: state, CPU, memory and power control.</summary>
public sealed class HyperVProvider : EntityProvider
{
    private const string Namespace = @"root\virtualization\v2";
    private readonly ServiceConfig _config;
    private Dictionary<string, (string Guid, string Name)> _vms = new(StringComparer.Ordinal);
    private string _signature = string.Empty;

    public HyperVProvider(EntityHub hub, ServiceConfig config, ILogger<HyperVProvider> logger)
        : base("hyperv", hub, logger)
    {
        _config = config;
        hub.RegisterMethod("vm_control", VmControlAsync);
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.HyperV, HyperVInstalled()))
        {
            return Task.CompletedTask;
        }

        return RunPeriodicAsync(TimeSpan.FromSeconds(10), _ =>
        {
            Update();
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private static bool HyperVInstalled()
    {
        try
        {
            return ServiceController.GetServices().Any(s => s.ServiceName.Equals("vmms", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void Update()
    {
        var summaries = ReadSummaries();
        var vms = new Dictionary<string, (string Guid, string Name)>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            var key = Slug.Key("vm", summary.Name);
            if (vms.ContainsKey(key))
            {
                key = $"{key}_{summary.Guid[..4]}";
            }
            vms[key] = (summary.Guid, summary.Name);
        }

        var signature = string.Join('|', vms.Select(v => v.Key + "=" + v.Value.Name));
        if (signature != _signature)
        {
            var descriptors = new List<EntityDescriptor>();
            foreach (var (key, vm) in vms)
            {
                var placeholders = new Dictionary<string, string> { ["vm"] = vm.Name };
                descriptors.Add(new() { Key = key, Platform = Platforms.Switch, TranslationKey = "vm", Placeholders = placeholders, Name = $"VM {vm.Name}" });
                descriptors.Add(new()
                {
                    Key = $"{key}_state",
                    Platform = Platforms.Sensor,
                    TranslationKey = "vm_state",
                    Placeholders = placeholders,
                    Name = $"VM {vm.Name} state",
                    DeviceClass = "enum",
                    Options = ["running", "off", "saved", "paused", "starting", "stopping", "saving", "pausing", "resuming", "other"],
                });
                descriptors.Add(new() { Key = $"{key}_cpu", Platform = Platforms.Sensor, TranslationKey = "vm_cpu", Placeholders = placeholders, Name = $"VM {vm.Name} CPU", Unit = "%", StateClass = Ha.Measurement, Precision = 0 });
                descriptors.Add(new() { Key = $"{key}_memory", Platform = Platforms.Sensor, TranslationKey = "vm_memory", Placeholders = placeholders, Name = $"VM {vm.Name} memory", DeviceClass = "data_size", Unit = "MiB", StateClass = Ha.Measurement, Precision = 0 });
            }

            Publish(descriptors);
            _signature = signature;
        }

        _vms = vms;
        foreach (var summary in summaries)
        {
            var key = vms.First(v => v.Value.Guid == summary.Guid).Key;
            var state = MapState(summary.EnabledState);
            Set(key, state is "running" or "starting" or "pausing" or "paused" or "resuming", Attrs(("state", state)));
            Set($"{key}_state", state, Attrs(
                ("uptime_hours", summary.UptimeMs > 0 ? Math.Round(summary.UptimeMs / 3_600_000.0, 1) : null),
                ("heartbeat", summary.Heartbeat),
                ("guest_os", summary.GuestOs),
                ("processors", summary.Processors),
                ("vm_id", summary.Guid)));
            Set($"{key}_cpu", state == "running" ? summary.ProcessorLoad : 0, 0);
            Set($"{key}_memory", state == "running" ? summary.MemoryUsage : 0, 0);
        }
    }

    private sealed record Summary(string Guid, string Name, int EnabledState, double ProcessorLoad, double MemoryUsage, long UptimeMs, string? Heartbeat, string? GuestOs, int? Processors);

    private List<Summary> ReadSummaries()
    {
        var result = new List<Summary>();
        using var management = new ManagementClass(Namespace, "Msvm_VirtualSystemManagementService", null);
        using var instances = management.GetInstances();
        foreach (ManagementObject service in instances)
        {
            using (service)
            {
                using var inParams = service.GetMethodParameters("GetSummaryInformation");
                // 1=ElementName 4=NumberOfProcessors 100=EnabledState 101=ProcessorLoad 103=MemoryUsage 104=Heartbeat 105=Uptime 106=GuestOperatingSystem
                inParams["RequestedInformation"] = new uint[] { 0, 1, 4, 100, 101, 103, 104, 105, 106 };
                using var outParams = service.InvokeMethod("GetSummaryInformation", inParams, null);
                if (outParams?["SummaryInformation"] is not ManagementBaseObject[] summaries)
                {
                    continue;
                }

                foreach (var summary in summaries)
                {
                    var guid = summary["Name"] as string ?? string.Empty;
                    var name = summary["ElementName"] as string ?? guid;
                    if (string.IsNullOrEmpty(guid))
                    {
                        continue;
                    }

                    result.Add(new Summary(
                        guid,
                        name,
                        Convert.ToInt32(summary["EnabledState"] ?? 0),
                        Convert.ToDouble(summary["ProcessorLoad"] ?? 0),
                        Convert.ToDouble(summary["MemoryUsage"] ?? 0),
                        Convert.ToInt64(summary["UpTime"] ?? 0),
                        HeartbeatText(summary["Heartbeat"]),
                        summary["GuestOperatingSystem"] as string,
                        summary["NumberOfProcessors"] is { } p ? Convert.ToInt32(p) : null));
                }
                break;
            }
        }

        return result;
    }

    private static string? HeartbeatText(object? value) => value is null ? null : Convert.ToInt32(value) switch
    {
        2 => "ok",
        6 => "error",
        12 => "no_contact",
        13 => "lost_communication",
        15 => "paused",
        _ => "unknown",
    };

    private static string MapState(int enabledState) => enabledState switch
    {
        2 => "running",
        3 => "off",
        6 or 32769 => "saved",
        9 or 32768 => "paused",
        10 or 32770 => "starting",
        4 or 32774 => "stopping",
        32773 => "saving",
        32776 => "pausing",
        32777 => "resuming",
        _ => "other",
    };

    public override Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (!_vms.TryGetValue(command.Key, out var vm))
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.NotFound, "Unknown virtual machine."));
        }

        return command.Action switch
        {
            Actions.TurnOn => ControlAsync(vm.Guid, "start"),
            Actions.TurnOff => ControlAsync(vm.Guid, "shutdown"),
            _ => Task.FromResult(CommandResult.NotSupported(command.Action)),
        };
    }

    private Task<CommandResult> VmControlAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var name = parameters.GetString("vm") ?? string.Empty;
        var vm = _vms.Values.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || v.Guid.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (vm.Guid is null)
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.NotFound, $"Virtual machine '{name}' not found."));
        }

        return ControlAsync(vm.Guid, parameters.GetString("action") ?? string.Empty);
    }

    private Task<CommandResult> ControlAsync(string guid, string action) => Task.Run(() =>
    {
        try
        {
            if (action == "shutdown")
            {
                using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM Msvm_ShutdownComponent WHERE SystemName = '{guid.Replace("'", string.Empty)}'");
                foreach (ManagementObject component in searcher.Get())
                {
                    using (component)
                    {
                        using var inParams = component.GetMethodParameters("InitiateShutdown");
                        inParams["Force"] = true;
                        inParams["Reason"] = "Requested by Home Assistant";
                        using var outParams = component.InvokeMethod("InitiateShutdown", inParams, null);
                        var code = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);
                        return code is 0 or 4096
                            ? CommandResult.Ok()
                            : CommandResult.Fail(ErrorCodes.Failed, $"Guest shutdown failed ({code}). Are the integration services running?");
                    }
                }

                // No integration services: turn off hard.
                action = "turn_off";
            }

            ushort requested = action switch
            {
                "start" or "resume" => 2,
                "turn_off" => 3,
                "save" => 6,
                "pause" => 9,
                "reset" => 11,
                _ => 0,
            };
            if (requested == 0)
            {
                return CommandResult.Fail(ErrorCodes.InvalidParams, $"Unknown action '{action}'.");
            }

            using var vmSearcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{guid.Replace("'", string.Empty)}'");
            foreach (ManagementObject vm in vmSearcher.Get())
            {
                using (vm)
                {
                    using var inParams = vm.GetMethodParameters("RequestStateChange");
                    inParams["RequestedState"] = requested;
                    using var outParams = vm.InvokeMethod("RequestStateChange", inParams, null);
                    var code = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);
                    return code is 0 or 4096
                        ? CommandResult.Ok()
                        : CommandResult.Fail(ErrorCodes.Failed, $"Hyper-V returned {code}.");
                }
            }

            return CommandResult.Fail(ErrorCodes.NotFound, "Virtual machine not found.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "VM action {Action} failed", action);
            return CommandResult.Fail(ErrorCodes.Failed, ex.Message);
        }
    });
}
