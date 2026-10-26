using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Service.Native;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Shutdown, restart, sleep, hibernate, sign out, UEFI restart and the power plan.</summary>
public sealed class PowerProvider : EntityProvider
{
    private const uint AccessScheme = 16;
    private readonly ServiceConfig _config;
    private readonly SessionTracker _sessions;
    private List<(Guid Id, string Name)> _plans = [];

    public PowerProvider(EntityHub hub, ServiceConfig config, SessionTracker sessions, ILogger<PowerProvider> logger)
        : base("power", hub, logger)
    {
        _config = config;
        _sessions = sessions;
        hub.RegisterMethod("power", PowerMethodAsync);
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        var (sleep, hibernate) = Win32.GetPowerCapabilities();
        var list = new List<EntityDescriptor>
        {
            new() { Key = "shutdown", Platform = Platforms.Button, TranslationKey = "shutdown", Name = "Shut down" },
            new() { Key = "restart", Platform = Platforms.Button, TranslationKey = "restart", Name = "Restart" },
            new() { Key = "logoff", Platform = Platforms.Button, TranslationKey = "logoff", Name = "Sign out" },
        };
        if (sleep)
        {
            list.Add(new() { Key = "sleep", Platform = Platforms.Button, TranslationKey = "sleep", Name = "Sleep" });
        }

        if (hibernate)
        {
            list.Add(new() { Key = "hibernate", Platform = Platforms.Button, TranslationKey = "hibernate", Name = "Hibernate" });
        }

        if (_config.AllowFirmwareRestart && IsUefi())
        {
            list.Add(new() { Key = "restart_firmware", Platform = Platforms.Button, TranslationKey = "restart_firmware", Name = "Restart to UEFI", EnabledByDefault = false });
        }

        var planEnabled = ModuleState.IsOn(_config.Modules.PowerPlan, true);
        if (planEnabled)
        {
            _plans = ReadPlans();
        }

        if (_plans.Count > 0)
        {
            list.Add(new() { Key = "power_plan", Platform = Platforms.Select, TranslationKey = "power_plan", Name = "Power plan", Options = _plans.Select(p => p.Name).ToList() });
        }

        Publish(list);
        foreach (var button in list.Where(d => d.Platform == Platforms.Button))
        {
            Set(button.Key, (JsonNode?)null);
        }

        if (_plans.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunPeriodicAsync(TimeSpan.FromSeconds(15), _ =>
        {
            var active = ReadActivePlan();
            var name = _plans.FirstOrDefault(p => p.Id == active).Name;
            Set("power_plan", name);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public override Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(command.Key switch
            {
                "shutdown" => Shutdown(restart: false, delay: 0),
                "restart" => Shutdown(restart: true, delay: 0),
                "sleep" => Suspend(hibernate: false, force: false),
                "hibernate" => Suspend(hibernate: true, force: false),
                "logoff" => Logoff(),
                "restart_firmware" => RestartToFirmware(),
                "power_plan" when command.Action == Actions.SelectOption => SetPlan(command.Params.GetString("option")),
                _ => CommandResult.NotSupported(command.Action),
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Power action {Key} failed", command.Key);
            return Task.FromResult(CommandResult.Fail(ErrorCodes.Failed, ex.Message));
        }
    }

    private Task<CommandResult> PowerMethodAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var action = parameters.GetString("action") ?? string.Empty;
        var delay = (uint)Math.Clamp(parameters.GetLong("delay") ?? 0, 0, 86400);
        var force = parameters.GetBool("force") ?? false;
        return Task.FromResult(action switch
        {
            "shutdown" => Shutdown(false, delay),
            "restart" => Shutdown(true, delay),
            "sleep" => Suspend(false, force),
            "hibernate" => Suspend(true, force),
            "logoff" => Logoff(),
            "abort" => Abort(),
            _ => CommandResult.Fail(ErrorCodes.InvalidParams, $"Unknown power action '{action}'."),
        });
    }

    private CommandResult Shutdown(bool restart, uint delay)
    {
        Win32.EnablePrivilege("SeShutdownPrivilege");
        // FORCE_OTHERS is required: the service runs in session 0, the signed-in user is "another" session.
        var flags = Win32.ShutdownForceOthers | (restart ? Win32.ShutdownRestart : Win32.ShutdownPowerOff);
        var message = restart ? "Restart requested by Home Assistant." : "Shutdown requested by Home Assistant.";
        var result = Win32.InitiateShutdown(null, message, delay, flags, Win32.ReasonFlagPlanned);
        if (result != 0)
        {
            return CommandResult.Fail(ErrorCodes.Failed, new Win32Exception((int)result).Message);
        }

        Logger.LogInformation("{Action} initiated (delay {Delay}s)", restart ? "Restart" : "Shutdown", delay);
        return CommandResult.Ok();
    }

    private CommandResult Abort()
    {
        Win32.EnablePrivilege("SeShutdownPrivilege");
        return Win32.AbortSystemShutdown(null)
            ? CommandResult.Ok()
            : CommandResult.Fail(ErrorCodes.Failed, new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private CommandResult Suspend(bool hibernate, bool force)
    {
        Win32.EnablePrivilege("SeShutdownPrivilege");
        // SetSuspendState blocks until the PC wakes up: run it in the background and answer first.
        _ = Task.Run(async () =>
        {
            await Task.Delay(750).ConfigureAwait(false);
            if (!Win32.SetSuspendState(hibernate, force, false))
            {
                Logger.LogWarning("SetSuspendState failed: {Error}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        });
        return CommandResult.Ok();
    }

    private CommandResult Logoff()
    {
        var session = _sessions.Console;
        if (session is null || session.User is null)
        {
            return CommandResult.Fail(ErrorCodes.Unavailable, "No user is signed in.");
        }

        return Win32.WTSLogoffSession(0, session.Id, false)
            ? CommandResult.Ok()
            : CommandResult.Fail(ErrorCodes.Failed, new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private CommandResult RestartToFirmware()
    {
        using var process = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /fw /t 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        process?.WaitForExit(10_000);
        return process is { HasExited: true, ExitCode: not 0 }
            ? CommandResult.Fail(ErrorCodes.Failed, $"shutdown.exe returned {process.ExitCode}")
            : CommandResult.Ok();
    }

    private static bool IsUefi()
    {
        var firmware = Environment.GetEnvironmentVariable("firmware_type");
        if (!string.IsNullOrEmpty(firmware))
        {
            return firmware.Equals("UEFI", StringComparison.OrdinalIgnoreCase);
        }

        // Windows does not expose firmware_type to services; check the EFI boot entry location instead.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return key?.GetValue("PEFirmwareType") is int type && type == 2;
        }
        catch
        {
            return false;
        }
    }

    private CommandResult SetPlan(string? name)
    {
        var plan = _plans.FirstOrDefault(p => p.Name == name);
        if (plan.Id == Guid.Empty)
        {
            return CommandResult.Fail(ErrorCodes.InvalidParams, $"Unknown power plan '{name}'.");
        }

        var id = plan.Id;
        var result = Win32.PowerSetActiveScheme(0, ref id);
        if (result != 0)
        {
            return CommandResult.Fail(ErrorCodes.Failed, new Win32Exception((int)result).Message);
        }

        Set("power_plan", plan.Name);
        return CommandResult.Ok();
    }

    private static Guid ReadActivePlan()
    {
        if (Win32.PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0)
        {
            return Guid.Empty;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            Win32.LocalFree(pointer);
        }
    }

    private List<(Guid, string)> ReadPlans()
    {
        var plans = new List<(Guid, string)>();
        try
        {
            for (uint index = 0; index < 32; index++)
            {
                uint size = 16;
                var buffer = new byte[16];
                if (Win32.PowerEnumerate(0, 0, 0, AccessScheme, index, buffer, ref size) != 0)
                {
                    break;
                }

                var id = new Guid(buffer);
                uint nameSize = 0;
                Win32.PowerReadFriendlyName(0, ref id, 0, 0, null, ref nameSize);
                var name = id.ToString();
                if (nameSize > 0)
                {
                    var nameBuffer = new byte[nameSize];
                    if (Win32.PowerReadFriendlyName(0, ref id, 0, 0, nameBuffer, ref nameSize) == 0)
                    {
                        name = Encoding.Unicode.GetString(nameBuffer).TrimEnd('\0');
                    }
                }

                if (plans.All(p => p.Item2 != name))
                {
                    plans.Add((id, name));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not enumerate power plans");
        }

        return plans;
    }
}
