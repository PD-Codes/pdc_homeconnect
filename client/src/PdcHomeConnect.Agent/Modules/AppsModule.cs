using System.Diagnostics;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using N = PdcHomeConnect.Agent.Native.Native;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>Foreground app (event driven), open apps and watched apps.</summary>
public sealed class AppsModule : EntityProvider, IDisposable
{
    private readonly AgentContext _context;
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string?> _paths = [];
    private N.WinEventProc? _winEventProc;
    private nint _hook;

    public AppsModule(AgentContext context, ILogger<AppsModule> logger)
        : base("apps", context.Hub, logger)
    {
        _context = context;
    }

    private IEnumerable<WatchedApp> Watched => _context.Config.WatchedApps.Where(a => !string.IsNullOrWhiteSpace(a.Id) && a.Processes.Count > 0);

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        var descriptors = new List<EntityDescriptor>
        {
            new() { Key = "foreground_app", Platform = Platforms.Sensor, TranslationKey = "foreground_app", Name = "Foreground app" },
            new() { Key = "open_apps", Platform = Platforms.Sensor, TranslationKey = "open_apps", Name = "Open apps", StateClass = Ha.Measurement, Precision = 0 },
        };
        foreach (var app in Watched)
        {
            descriptors.Add(new EntityDescriptor
            {
                Key = Slug.Key("app", app.Id),
                Platform = Platforms.BinarySensor,
                TranslationKey = "app_running",
                Placeholders = new() { ["app"] = app.Name ?? app.Id },
                Name = app.Name ?? app.Id,
                DeviceClass = "running",
                OfflineState = JsonValue.Create(false),
            });
        }

        Publish(descriptors);

        // WinEvent hooks need a thread with a message loop: install on the UI thread.
        await _context.Ui.InvokeAsync(() =>
        {
            _winEventProc = (_, _, hwnd, _, _, _, _) => UpdateForeground(hwnd);
            _hook = N.SetWinEventHook(N.EventSystemForeground, N.EventSystemForeground, 0, _winEventProc, 0, 0, N.WinEventOutOfContext | N.WinEventSkipOwnProcess);
        }).ConfigureAwait(false);
        UpdateForeground(N.GetForegroundWindow());
        _context.Signals.Changed += OnSignalsChanged;

        await RunPeriodicAsync(TimeSpan.FromSeconds(3), _ =>
        {
            UpdateProcesses();
            UpdateOpenApps();
            if (_context.Config.ReportWindowTitles)
            {
                // Titles change without a foreground event (e.g. browser tabs).
                UpdateForeground(N.GetForegroundWindow());
            }
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    private bool _lastPrivacy;

    private void OnSignalsChanged()
    {
        var privacy = _context.Signals.Privacy;
        if (privacy == _lastPrivacy)
        {
            return;
        }

        _lastPrivacy = privacy;
        UpdateForeground(N.GetForegroundWindow());
        UpdateOpenApps();
    }

    private void UpdateForeground(nint hwnd)
    {
        try
        {
            if (hwnd == 0)
            {
                return;
            }

            N.GetWindowThreadProcessId(hwnd, out var pid);
            var path = PathOf(pid);
            // UWP apps are hosted by ApplicationFrameHost: find the real app process.
            if (path is not null && Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                N.EnumChildWindows(hwnd, (child, _) =>
                {
                    N.GetWindowThreadProcessId(child, out var childPid);
                    if (childPid != pid)
                    {
                        pid = childPid;
                        path = PathOf(childPid);
                        return false;
                    }
                    return true;
                }, 0);
            }

            var process = path is null ? null : Path.GetFileName(path);
            _context.Signals.ForegroundProcess = process;
            if (_context.Signals.Privacy)
            {
                Set("foreground_app", (string?)null, Attrs(("privacy", true)));
                return;
            }

            var title = _context.Config.ReportWindowTitles ? N.GetWindowTitle(hwnd) : null;
            Set("foreground_app", path is null ? null : DisplayName(path), Attrs(
                ("process", process),
                ("pid", (long)pid),
                ("title", string.IsNullOrEmpty(title) ? null : title)));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Foreground update failed");
        }
    }

    private void UpdateProcesses()
    {
        var sessionId = _context.SessionId;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alive = new HashSet<uint>();
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

                    names.Add(process.ProcessName + ".exe");
                    alive.Add((uint)process.Id);
                }
                catch
                {
                    // Process exited.
                }
            }
        }

        lock (_paths)
        {
            foreach (var stale in _paths.Keys.Where(pid => !alive.Contains(pid)).ToList())
            {
                _paths.Remove(stale);
            }
        }

        _context.Signals.RunningProcesses = names;
        foreach (var app in Watched)
        {
            var running = app.Processes.Any(p => names.Contains(p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe"));
            Set(Slug.Key("app", app.Id), running);
        }
    }

    /// <summary>Executable path for a process id (cached).</summary>
    public string? PathOf(uint pid)
    {
        lock (_paths)
        {
            if (!_paths.TryGetValue(pid, out var path))
            {
                path = N.GetProcessPath(pid);
                _paths[pid] = path;
            }
            return path;
        }
    }

    private void UpdateOpenApps()
    {
        if (_context.Signals.Privacy)
        {
            Set("open_apps", (long?)null, Attrs(("privacy", true)));
            return;
        }

        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        N.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!N.IsWindowVisible(hwnd) || N.GetWindow(hwnd, N.GwOwner) != 0 || N.IsCloaked(hwnd))
                {
                    return true;
                }

                var exStyle = (long)N.GetWindowLongPtr(hwnd, N.GwlExStyle);
                if ((exStyle & N.WsExToolWindow) != 0 && (exStyle & N.WsExAppWindow) == 0)
                {
                    return true;
                }

                if (N.GetWindowTextLength(hwnd) == 0)
                {
                    return true;
                }

                var className = N.GetClass(hwnd);
                if (className is "Progman" or "Shell_TrayWnd" or "WorkerW" or "Windows.UI.Core.CoreWindow")
                {
                    return true;
                }

                N.GetWindowThreadProcessId(hwnd, out var pid);
                var path = PathOf(pid);
                if (path is null || path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                apps.Add(DisplayName(path));
                processes.Add(Path.GetFileName(path));
            }
            catch
            {
                // Window vanished.
            }
            return true;
        }, 0);

        Set("open_apps", (long)apps.Count, new JsonObject
        {
            ["apps"] = new JsonArray(apps.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["processes"] = new JsonArray(processes.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        });
    }

    /// <summary>Friendly app name from the file description (e.g. "Visual Studio Code").</summary>
    public string DisplayName(string path)
    {
        lock (_displayNames)
        {
            if (_displayNames.TryGetValue(path, out var cached))
            {
                return cached;
            }

            string name;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                    : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim()
                    : Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                name = Path.GetFileNameWithoutExtension(path);
            }

            _displayNames[path] = name;
            return name;
        }
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            var hook = _hook;
            _context.Ui.Post(() => N.UnhookWinEvent(hook));
            _hook = 0;
        }
    }
}
