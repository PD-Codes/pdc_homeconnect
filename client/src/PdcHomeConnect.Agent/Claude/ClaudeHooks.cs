using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdcHomeConnect.Agent.Claude;

/// <summary>
/// Installs/removes the PDC HomeConnect hooks in the user's Claude Code settings (~/.claude/settings.json).
/// Hooks run the helper "pdc-hc-hook.exe" asynchronously (exec form), so Claude Code is never slowed down.
/// </summary>
public static class ClaudeHooks
{
    public static readonly string[] Events =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest",
        "Notification", "Stop", "StopFailure", "SessionEnd",
    ];

    private const string HookFileName = "pdc-hc-hook.exe";

    public static string ClaudeDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string SettingsPath => Path.Combine(ClaudeDirectory, "settings.json");

    /// <summary>True if Claude Code has been used by this Windows user.</summary>
    public static bool ClaudeCodeInstalled => Directory.Exists(ClaudeDirectory);

    public static bool IsInstalled()
    {
        var settings = Load(SettingsPath);
        return settings?["hooks"] is JsonObject hooks && Events.All(e => hooks[e] is JsonArray groups && groups.Any(IsOurs));
    }

    /// <summary>Adds (or updates) our hooks. Returns false if nothing had to be changed.</summary>
    public static bool Install(string hookExecutable)
    {
        var settings = Load(SettingsPath) ?? [];
        var hooks = settings["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = [];
            settings["hooks"] = hooks;
        }

        var changed = false;
        foreach (var name in Events)
        {
            if (hooks[name] is not JsonArray groups)
            {
                groups = [];
                hooks[name] = groups;
            }

            var existing = groups.Where(IsOurs).ToList();
            if (existing.Count == 1 && Command(existing[0]) is { } command && command.Equals(hookExecutable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var node in existing)
            {
                groups.Remove(node);
            }

            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = hookExecutable,
                    ["args"] = new JsonArray(),
                    ["async"] = true,
                    ["timeout"] = 10,
                }),
            });
            changed = true;
        }

        if (changed)
        {
            Save(SettingsPath, settings);
        }

        return changed;
    }

    /// <summary>Removes our hooks from the current user's settings and leaves everything else untouched.</summary>
    public static bool Uninstall() => Uninstall(SettingsPath);

    /// <summary>Removes our hooks from a settings.json (used by the uninstaller for all user profiles).</summary>
    public static bool Uninstall(string settingsPath)
    {
        var settings = Load(settingsPath);
        if (settings?["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        var changed = false;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray groups)
            {
                continue;
            }

            foreach (var node in groups.Where(IsOurs).ToList())
            {
                groups.Remove(node);
                changed = true;
            }

            if (groups.Count == 0)
            {
                hooks.Remove(name);
            }
        }

        if (hooks.Count == 0)
        {
            settings.Remove("hooks");
        }

        if (changed)
        {
            Save(settingsPath, settings);
        }

        return changed;
    }

    /// <summary>Claude Code settings files of all local user profiles (for the uninstaller, needs admin rights).</summary>
    public static IEnumerable<string> AllProfileSettings()
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        try
        {
            using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            foreach (var sid in list?.GetSubKeyNames() ?? [])
            {
                using var profile = list!.OpenSubKey(sid);
                if (profile?.GetValue("ProfileImagePath") is string path && path.Length > 0)
                {
                    profiles.Add(Environment.ExpandEnvironmentVariables(path));
                }
            }
        }
        catch
        {
            // Only the current profile.
        }

        return profiles
            .Select(p => Path.Combine(p, ".claude", "settings.json"))
            .Where(File.Exists)
            .ToList();
    }

    private static bool IsOurs(JsonNode? group) => Command(group) is not null;

    /// <summary>File name of a command path (handles both separators).</summary>
    private static string FileNameOf(string command)
    {
        var trimmed = command.Trim().Trim('"');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }

    private static string? Command(JsonNode? group)
    {
        if (group?["hooks"] is not JsonArray handlers)
        {
            return null;
        }

        foreach (var handler in handlers)
        {
            var command = handler?["command"]?.GetValueKind() == JsonValueKind.String ? handler["command"]!.GetValue<string>() : null;
            if (command is not null && FileNameOf(command).Equals(HookFileName, StringComparison.OrdinalIgnoreCase))
            {
                return command.Trim().Trim('"');
            }
        }

        return null;
    }

    private static JsonObject? Load(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        var text = File.ReadAllText(settingsPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // Throws on invalid JSON: never overwrite a settings file we cannot read.
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("settings.json is not a JSON object.");
    }

    private static void Save(string settingsPath, JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var backup = settingsPath + ".pdc-backup";
        if (File.Exists(settingsPath) && !File.Exists(backup))
        {
            File.Copy(settingsPath, backup);
        }

        var tmp = settingsPath + ".pdc-tmp";
        File.WriteAllText(tmp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.Move(tmp, settingsPath, overwrite: true);
    }
}
