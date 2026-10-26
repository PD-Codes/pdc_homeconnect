using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent;

/// <summary>Locations of agent files (per user).</summary>
public static class AgentPaths
{
    public static string RoamingRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDC", "HomeConnect");

    public static string LocalRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDC", "HomeConnect");

    public static string Config => Path.Combine(RoamingRoot, "agent.json");

    public static string State => Path.Combine(RoamingRoot, "agent.state.json");

    public static string Logs => Path.Combine(LocalRoot, "logs");

    public static string Images => Path.Combine(LocalRoot, "images");

    public static string ServiceConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PDC", "HomeConnect", "service.json");

    public static string InstallDirectory => AppContext.BaseDirectory;

    public static string HookExecutable => Path.Combine(InstallDirectory, "pdc-hc-hook.exe");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RoamingRoot);
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Images);
    }
}

/// <summary>User settings (agent.json, JSON with comments, edited by the user).</summary>
public sealed class AgentConfig
{
    public int AfkMinutes { get; set; } = 5;

    public bool ReportWindowTitles { get; set; }

    public List<WatchedApp> WatchedApps { get; set; } = [];

    public List<string> WorkApps { get; set; } = [];

    public List<string> CallApps { get; set; } = [];

    public GamesConfig Games { get; set; } = new();

    public DiscordConfig Discord { get; set; } = new();

    public NotificationsConfig Notifications { get; set; } = new();

    public List<HotkeyConfig> Hotkeys { get; set; } = [];

    public List<TrayActionConfig> TrayActions { get; set; } = [];

    public List<UserCommandConfig> Commands { get; set; } = [];

    public List<string> AllowedUrlSchemes { get; set; } = ["http", "https"];

    public FeaturesConfig Features { get; set; } = new();

    public static string DefaultTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("agent.default.jsonc")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class WatchedApp
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public List<string> Processes { get; set; } = [];
}

public sealed class GamesConfig
{
    public bool Steam { get; set; } = true;
    public bool Epic { get; set; } = true;
    public bool Gog { get; set; } = true;
    public bool Fullscreen { get; set; } = true;
    public bool SteamImages { get; set; } = true;
    public List<CustomGame> Custom { get; set; } = [];
    public List<string> Ignore { get; set; } = [];
}

public sealed class CustomGame
{
    public string Name { get; set; } = string.Empty;
    public List<string> Processes { get; set; } = [];
}

public sealed class DiscordConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://127.0.0.1";
}

public sealed class NotificationsConfig
{
    public string FullscreenFallback { get; set; } = "sound";
}

public sealed class HotkeyConfig
{
    public string Id { get; set; } = string.Empty;
    public string Keys { get; set; } = string.Empty;
}

public sealed class TrayActionConfig
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public sealed class UserCommandConfig
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string Command { get; set; } = string.Empty;
    public string? Args { get; set; }
    public string? WorkingDirectory { get; set; }
    public string Window { get; set; } = "hidden";
    public bool Wait { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public bool Button { get; set; } = true;
}

public sealed class FeaturesConfig
{
    public bool Media { get; set; } = true;
    public bool AudioDevices { get; set; } = true;
    public bool Brightness { get; set; } = true;
    public bool DarkMode { get; set; } = true;
    public bool ClaudeCode { get; set; } = true;
}

/// <summary>Machine-managed state (agent.state.json). Secrets are DPAPI-encrypted for the current user.</summary>
public sealed class AgentState
{
    private static readonly object Gate = new();

    public bool PrivacyMode { get; set; }

    /// <summary>Discord application id saved by the setup dialog (agent.json "discord.client_id" overrides it).</summary>
    public string? DiscordClientId { get; set; }

    /// <summary>DPAPI protected.</summary>
    public string? DiscordClientSecret { get; set; }

    public string? DiscordAccessToken { get; set; }

    public string? DiscordRefreshToken { get; set; }

    public DateTimeOffset? DiscordTokenExpires { get; set; }

    public string? LastGame { get; set; }

    public double? LastGameMinutes { get; set; }

    public DateTimeOffset? LastGameEnded { get; set; }

    public bool ClaudeHooksDisabled { get; set; }

    public static AgentState Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(AgentPaths.State))
                {
                    return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(AgentPaths.State), Json.Options) ?? new AgentState();
                }
            }
            catch
            {
                // Corrupt state: start fresh.
            }

            return new AgentState();
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                var tmp = AgentPaths.State + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Indented));
                File.Move(tmp, AgentPaths.State, overwrite: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
