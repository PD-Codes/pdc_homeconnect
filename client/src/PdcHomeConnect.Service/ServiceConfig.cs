using System.Reflection;

namespace PdcHomeConnect.Service;

/// <summary>Service configuration (C:\ProgramData\PDC\HomeConnect\service.json, JSON with comments).</summary>
public sealed class ServiceConfig
{
    /// <summary>Name shown in Home Assistant. Default: computer name.</summary>
    public string? DeviceName { get; set; }

    public int Port { get; set; } = 48733;

    /// <summary>Announce the PC via mDNS so Home Assistant discovers it.</summary>
    public bool Discovery { get; set; } = true;

    /// <summary>Create/maintain the Windows Firewall rules automatically.</summary>
    public bool ManageFirewall { get; set; } = true;

    /// <summary>"local_subnet" (default) or "any".</summary>
    public string FirewallScope { get; set; } = "local_subnet";

    /// <summary>Update interval for CPU, memory, disks and network in seconds.</summary>
    public int StatsInterval { get; set; } = 10;

    /// <summary>Update interval for temperatures, fans and GPU in seconds.</summary>
    public int HardwareInterval { get; set; } = 10;

    /// <summary>Use LibreHardwareMonitor for CPU temperatures, fans and drive SMART data.</summary>
    public bool HardwareSensors { get; set; } = true;

    /// <summary>Start the tray agent in user sessions if it is not running (crash recovery, after updates).</summary>
    public bool AgentWatchdog { get; set; } = true;

    /// <summary>Accept agents from any executable path (development only).</summary>
    public bool AllowAnyAgent { get; set; }

    public bool AllowFirmwareRestart { get; set; } = true;

    public int WindowsUpdateCheckHours { get; set; } = 6;

    public ModulesConfig Modules { get; set; } = new();

    /// <summary>Windows services exposed as switches (service names, e.g. "Spooler").</summary>
    public List<string> WatchedServices { get; set; } = [];

    public EventLogConfig EventLog { get; set; } = new();

    public PlexConfig Plex { get; set; } = new();

    /// <summary>Commands Home Assistant may run as SYSTEM.</summary>
    public List<CommandConfig> Commands { get; set; } = [];

    public static string DefaultTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("service.default.jsonc")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Module switches: "auto", "on" or "off".</summary>
public sealed class ModulesConfig
{
    public string Services { get; set; } = "auto";
    public string HyperV { get; set; } = "auto";
    public string Sessions { get; set; } = "on";
    public string EventLog { get; set; } = "auto";
    public string ScheduledTasks { get; set; } = "auto";
    public string Backup { get; set; } = "auto";
    public string StorageHealth { get; set; } = "on";
    public string Iis { get; set; } = "auto";
    public string Docker { get; set; } = "auto";
    public string Smb { get; set; } = "auto";
    public string Plex { get; set; } = "auto";
    public string WindowsUpdate { get; set; } = "on";
    public string Defender { get; set; } = "on";
    public string PowerPlan { get; set; } = "on";
}

public sealed class EventLogConfig
{
    /// <summary>Fire the system_event entity for errors too (not only critical events).</summary>
    public bool EventsForErrors { get; set; }

    public List<string> Logs { get; set; } = ["System", "Application"];
}

public sealed class PlexConfig
{
    /// <summary>Plex Media Server URL. Default: http://127.0.0.1:32400</summary>
    public string Url { get; set; } = "http://127.0.0.1:32400";

    /// <summary>X-Plex-Token. Empty: read automatically from the registry of the user running Plex.</summary>
    public string? Token { get; set; }
}

/// <summary>A command that Home Assistant can run (button entity and run_command action).</summary>
public sealed class CommandConfig
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    /// <summary>Executable, .ps1, .bat or .cmd file.</summary>
    public string Command { get; set; } = string.Empty;

    public string? Args { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>Wait for the command and return exit code and output.</summary>
    public bool Wait { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Create a button entity for this command.</summary>
    public bool Button { get; set; } = true;
}

public static class ModuleState
{
    public static bool IsOn(string? value, bool autoValue) => (value ?? "auto").Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "enabled" => true,
        "off" or "false" or "no" or "disabled" => false,
        _ => autoValue,
    };
}
