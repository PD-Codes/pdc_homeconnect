using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using N = PdcHomeConnect.Agent.Native.Native;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>
/// Game detection: Steam (registry, instant), Epic, GOG, custom games and exclusive fullscreen apps.
/// Provides the current game with session duration, Steam playtime and cover art, plus game events.
/// </summary>
public sealed class GameModule : EntityProvider, IDisposable
{
    private const string SteamKey = @"Software\Valve\Steam";
    // Steam "apps" that are not games but report as running.
    private static readonly HashSet<long> IgnoredSteamApps = [431960, 228980, 1070560, 1391110];

    private sealed record DetectedGame(string Name, string Source, long? AppId, string? Exe);

    private sealed record GameSession(DetectedGame Game, DateTimeOffset Started);

    private readonly AgentContext _context;
    private readonly object _gate = new();
    private readonly Dictionary<uint, string?> _paths = [];
    private Dictionary<string, string> _installDirs = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<long, string> _steamNames = [];
    private DateTimeOffset _librariesLoaded = DateTimeOffset.MinValue;
    private GameSession? _current;
    private DateTimeOffset _lastSeen;
    private readonly Dictionary<long, (double? Minutes, DateTimeOffset Loaded)> _playtime = [];
    private (string? Name, DateTimeOffset Loaded) _persona;
    private RegistryWatcher? _steamWatcher;
    private string? _lastFullscreen;

    public GameModule(AgentContext context, ILogger<GameModule> logger)
        : base("games", context.Hub, logger)
    {
        _context = context;
    }

    private GamesConfig Config => _context.Config.Games;

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        var descriptors = new List<EntityDescriptor>
        {
            new() { Key = "current_game", Platform = Platforms.Sensor, TranslationKey = "current_game", Name = "Current game" },
            new() { Key = "gaming", Platform = Platforms.BinarySensor, TranslationKey = "gaming", Name = "Gaming", OfflineState = JsonValue.Create(false) },
            new() { Key = "game", Platform = Platforms.Event, TranslationKey = "game", Name = "Game", EventTypes = ["started", "stopped"] },
        };
        if (Config.Steam)
        {
            descriptors.Add(new()
            {
                Key = "steam_status",
                Platform = Platforms.Sensor,
                TranslationKey = "steam_status",
                Name = "Steam",
                DeviceClass = "enum",
                Options = ["offline", "online", "in_game"],
                OfflineState = JsonValue.Create("offline"),
            });
            descriptors.Add(new() { Key = "steam_updating", Platform = Platforms.BinarySensor, TranslationKey = "steam_updating", Name = "Steam downloading", OfflineState = JsonValue.Create(false) });
        }

        Publish(descriptors);
        LoadLibraries();

        if (Config.Steam)
        {
            _steamWatcher = new RegistryWatcher(RegistryHive.CurrentUser, SteamKey, TimeSpan.FromMilliseconds(500));
            _steamWatcher.Changed += Update;
            _steamWatcher.Start();
        }

        _context.Signals.Changed += OnSignalsChanged;
        await RunPeriodicAsync(TimeSpan.FromSeconds(3), _ =>
        {
            if (DateTimeOffset.UtcNow - _librariesLoaded > TimeSpan.FromMinutes(10))
            {
                LoadLibraries();
            }

            Update();
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void OnSignalsChanged()
    {
        var fullscreen = _context.Signals.Fullscreen;
        if (fullscreen != _lastFullscreen)
        {
            _lastFullscreen = fullscreen;
            Update();
        }
    }

    private void Update()
    {
        lock (_gate)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var detected = Detect();
                if (detected is not null)
                {
                    _lastSeen = now;
                    if (_current is null || _current.Game.Name != detected.Name)
                    {
                        if (_current is not null)
                        {
                            Stop(_current, now);
                        }

                        _current = new GameSession(detected, now);
                        Started(_current);
                    }
                }
                else if (_current is not null && now - _lastSeen > TimeSpan.FromSeconds(5))
                {
                    // Small grace period: launchers often restart the game process.
                    Stop(_current, now);
                    _current = null;
                }

                PublishState(now);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Game update failed");
            }
        }
    }

    private DetectedGame? Detect()
    {
        if (Config.Steam && SteamRunningApp() is { } appId && !IgnoredSteamApps.Contains(appId) && !Config.Ignore.Contains(appId.ToString()))
        {
            return new DetectedGame(SteamName(appId), "steam", appId, null);
        }

        var running = _context.Signals.RunningProcesses;
        foreach (var custom in Config.Custom.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            var match = custom.Processes.FirstOrDefault(p => running.Contains(p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe"));
            if (match is not null)
            {
                return new DetectedGame(custom.Name, "custom", null, match);
            }
        }

        if (_installDirs.Count > 0)
        {
            var found = FindInstalledGameProcess();
            if (found is not null)
            {
                return found;
            }
        }

        if (Config.Fullscreen && _context.Signals.Fullscreen == "game")
        {
            var hwnd = N.GetForegroundWindow();
            N.GetWindowThreadProcessId(hwnd, out var pid);
            var path = N.GetProcessPath(pid);
            if (path is not null && !IsIgnored(Path.GetFileName(path)))
            {
                return new DetectedGame(FriendlyName(path), "fullscreen", null, Path.GetFileName(path));
            }
        }

        return null;
    }

    private bool IsIgnored(string exe) => Config.Ignore.Any(i => i.Equals(exe, StringComparison.OrdinalIgnoreCase));

    private DetectedGame? FindInstalledGameProcess()
    {
        var sessionId = _context.SessionId;
        var alive = new HashSet<uint>();
        DetectedGame? result = null;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId)
                    {
                        continue;
                    }

                    var pid = (uint)process.Id;
                    alive.Add(pid);
                    if (result is not null)
                    {
                        continue;
                    }

                    if (!_paths.TryGetValue(pid, out var path))
                    {
                        path = N.GetProcessPath(pid);
                        _paths[pid] = path;
                    }

                    if (path is null || IsIgnored(Path.GetFileName(path)))
                    {
                        continue;
                    }

                    foreach (var (dir, name) in _installDirs)
                    {
                        if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                        {
                            result = new DetectedGame(name, dir.Contains("GOG", StringComparison.OrdinalIgnoreCase) ? "gog" : "epic", null, Path.GetFileName(path));
                            break;
                        }
                    }
                }
                catch
                {
                    // Process exited.
                }
            }
        }

        foreach (var stale in _paths.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _paths.Remove(stale);
        }

        return result;
    }

    private void Started(GameSession session)
    {
        Logger.LogInformation("Game started: {Game} ({Source})", session.Game.Name, session.Game.Source);
        Hub.FireEvent("game", "started", Attrs(("game", session.Game.Name), ("source", session.Game.Source), ("app_id", session.Game.AppId)));
    }

    private void Stop(GameSession session, DateTimeOffset now)
    {
        var minutes = Math.Round((now - session.Started).TotalMinutes, 1);
        Logger.LogInformation("Game stopped: {Game} after {Minutes} min", session.Game.Name, minutes);
        Hub.FireEvent("game", "stopped", Attrs(("game", session.Game.Name), ("source", session.Game.Source), ("app_id", session.Game.AppId), ("duration_minutes", minutes)));
        _context.State.LastGame = session.Game.Name;
        _context.State.LastGameMinutes = minutes;
        _context.State.LastGameEnded = now;
        _context.State.Save();
    }

    private void PublishState(DateTimeOffset now)
    {
        var session = _current;
        _context.Signals.Gaming = session is not null;
        _context.Signals.Game = session?.Game.Name;

        var state = _context.State;
        if (session is null)
        {
            Set("current_game", (string?)null, Attrs(
                ("last_game", state.LastGame),
                ("last_session_minutes", state.LastGameMinutes),
                ("last_played", state.LastGameEnded)));
            Set("gaming", false);
        }
        else
        {
            var game = session.Game;
            var playtime = game.AppId is { } id ? SteamPlaytime(id) : null;
            Set("current_game", game.Name, Attrs(
                ("source", game.Source),
                ("app_id", game.AppId),
                ("exe", game.Exe),
                ("started_at", session.Started),
                ("session_minutes", (long)(now - session.Started).TotalMinutes),
                ("total_playtime_hours", playtime is { } minutes ? Math.Round(minutes / 60.0, 1) : null),
                ("entity_picture", game.AppId is { } appId && Config.SteamImages ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg" : null)));
            Set("gaming", true, Attrs(("game", game.Name), ("source", game.Source)));
        }

        if (Config.Steam)
        {
            var (running, user) = SteamProcess();
            Set("steam_status", !running ? "offline" : session?.Game.Source == "steam" ? "in_game" : "online", Attrs(("user", user)));
            var updating = SteamUpdating();
            Set("steam_updating", updating.Count > 0, Attrs(("apps", new JsonArray(updating.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()))));
        }
    }

    // ------------------------------------------------------------------ Steam

    private static long? SteamRunningApp()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamKey);
        return key?.GetValue("RunningAppID") is int id && id > 0 ? id : null;
    }

    private (bool Running, string? User) SteamProcess()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{SteamKey}\ActiveProcess");
        if (key?.GetValue("pid") is not int pid || pid == 0)
        {
            return (false, null);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return (false, null);
            }
        }
        catch
        {
            return (false, null);
        }

        var activeUser = key.GetValue("ActiveUser") is int user ? user : 0;
        return (activeUser != 0, activeUser != 0 ? SteamPersona() : null);
    }

    private List<string> SteamUpdating()
    {
        var result = new List<string>();
        using var apps = Registry.CurrentUser.OpenSubKey($@"{SteamKey}\Apps");
        if (apps is null)
        {
            return result;
        }

        foreach (var name in apps.GetSubKeyNames())
        {
            using var app = apps.OpenSubKey(name);
            if (app?.GetValue("Updating") is int updating && updating != 0 && long.TryParse(name, out var id))
            {
                result.Add(SteamName(id));
            }
        }

        return result;
    }

    private string SteamName(long appId)
    {
        if (_steamNames.TryGetValue(appId, out var name))
        {
            return name;
        }

        using var key = Registry.CurrentUser.OpenSubKey($@"{SteamKey}\Apps\{appId}");
        if (key?.GetValue("Name") is string registryName && registryName.Length > 0)
        {
            return registryName;
        }

        return $"Steam app {appId}";
    }

    private static string? SteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamKey);
        return (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
    }

    private string? SteamPersona()
    {
        if (DateTimeOffset.UtcNow - _persona.Loaded < TimeSpan.FromMinutes(1))
        {
            return _persona.Name;
        }

        string? name = null;
        var path = SteamPath();
        var users = path is null ? null : Vdf.TryParseFile(Path.Combine(path, "config", "loginusers.vdf"))?.Path("users");
        if (users is not null)
        {
            var recent = users.Children.Values.FirstOrDefault(u => u.GetString("MostRecent") == "1") ?? users.Children.Values.FirstOrDefault();
            name = recent?.GetString("PersonaName");
        }

        _persona = (name, DateTimeOffset.UtcNow);
        return name;
    }

    /// <summary>Total playtime in minutes from localconfig.vdf of the active Steam account (cached, the file can be large).</summary>
    private double? SteamPlaytime(long appId)
    {
        if (_playtime.TryGetValue(appId, out var cached) && DateTimeOffset.UtcNow - cached.Loaded < TimeSpan.FromMinutes(5))
        {
            return cached.Minutes;
        }

        double? minutes = null;
        var path = SteamPath();
        using (var key = Registry.CurrentUser.OpenSubKey($@"{SteamKey}\ActiveProcess"))
        {
            if (path is not null && key?.GetValue("ActiveUser") is int accountId && accountId != 0)
            {
                var config = Vdf.TryParseFile(Path.Combine(path, "userdata", accountId.ToString(), "config", "localconfig.vdf"));
                var apps = config?.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                minutes = apps?[appId.ToString()]?.GetLong("Playtime");
            }
        }

        _playtime[appId] = (minutes, DateTimeOffset.UtcNow);
        return minutes;
    }

    // ------------------------------------------------------------------ libraries

    private void LoadLibraries()
    {
        _librariesLoaded = DateTimeOffset.UtcNow;
        var steamNames = new Dictionary<long, string>();
        var installDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Config.Steam && SteamPath() is { } steam)
            {
                var libraries = Vdf.TryParseFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"))?.Path("libraryfolders");
                var paths = libraries?.Children.Values.Select(l => l.GetString("path")).Where(p => p is not null).Select(p => p!).ToList() ?? [];
                if (!paths.Contains(steam, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(steam);
                }

                foreach (var library in paths)
                {
                    var apps = Path.Combine(library, "steamapps");
                    if (!Directory.Exists(apps))
                    {
                        continue;
                    }

                    foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                    {
                        var state = Vdf.TryParseFile(manifest)?.Path("AppState");
                        if (state?.GetLong("appid") is { } id && state.GetString("name") is { } name)
                        {
                            steamNames[id] = name;
                        }
                    }
                }
            }

            if (Config.Epic)
            {
                var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (Directory.Exists(manifests))
                {
                    foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
                    {
                        try
                        {
                            var item = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                            var location = item?.GetString("InstallLocation");
                            var name = item?.GetString("DisplayName");
                            if (!string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(name))
                            {
                                installDirs[location.TrimEnd('\\') + "\\"] = name;
                            }
                        }
                        catch (JsonException)
                        {
                            // Skip broken manifests.
                        }
                    }
                }
            }

            if (Config.Gog)
            {
                using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
                foreach (var id in games?.GetSubKeyNames() ?? [])
                {
                    using var game = games!.OpenSubKey(id);
                    if (game?.GetValue("path") is string path && game.GetValue("gameName") is string name)
                    {
                        installDirs[path.TrimEnd('\\') + "\\"] = name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Loading game libraries failed");
        }

        _steamNames = steamNames;
        _installDirs = installDirs;
        Logger.LogDebug("Game libraries: {Steam} Steam, {Other} Epic/GOG", steamNames.Count, installDirs.Count);
    }

    private static string FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.ProductName))
            {
                return info.ProductName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription.Trim();
            }
        }
        catch
        {
            // Fall back to the file name.
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose() => _steamWatcher?.Dispose();
}
