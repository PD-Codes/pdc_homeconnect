using System.Text.Json.Nodes;
using Microsoft.Win32;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>Webcam/microphone usage (privacy consent store, event driven) and the fullscreen state.</summary>
public sealed class PresenceModule : EntityProvider, IDisposable
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private readonly AgentContext _context;
    private RegistryWatcher? _watcher;

    public PresenceModule(AgentContext context, ILogger<PresenceModule> logger)
        : base("presence", context.Hub, logger)
    {
        _context = context;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new() { Key = "webcam_in_use", Platform = Platforms.BinarySensor, TranslationKey = "webcam_in_use", Name = "Webcam in use", OfflineState = JsonValue.Create(false) },
            new() { Key = "microphone_in_use", Platform = Platforms.BinarySensor, TranslationKey = "microphone_in_use", Name = "Microphone in use", OfflineState = JsonValue.Create(false) },
            new()
            {
                Key = "fullscreen_state",
                Platform = Platforms.Sensor,
                TranslationKey = "fullscreen_state",
                Name = "Fullscreen",
                DeviceClass = "enum",
                Options = ["none", "fullscreen", "game", "presentation", "quiet_time", "app", "away"],
            },
        ]);

        _watcher = new RegistryWatcher(RegistryHive.CurrentUser, ConsentStore, TimeSpan.FromMilliseconds(300));
        _watcher.Changed += UpdateDevices;
        _watcher.Start();
        UpdateDevices();

        var tick = 0;
        await RunPeriodicAsync(TimeSpan.FromSeconds(2), _ =>
        {
            var state = Native.Native.QueryNotificationState() switch
            {
                1 => "away",
                2 => "fullscreen",
                3 => "game",
                4 => "presentation",
                6 => "quiet_time",
                7 => "app",
                _ => "none",
            };
            _context.Signals.Fullscreen = state;
            Set("fullscreen_state", state);

            // Safety net in case a registry notification was missed.
            if (++tick % 15 == 0)
            {
                UpdateDevices();
            }
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void UpdateDevices()
    {
        var webcam = AppsInUse("webcam");
        var microphone = AppsInUse("microphone");
        _context.Signals.WebcamApps = webcam;
        _context.Signals.MicrophoneApps = microphone;
        var privacy = _context.Signals.Privacy;
        Set("webcam_in_use", webcam.Count > 0, privacy ? null : Attrs(("apps", ToArray(webcam))));
        Set("microphone_in_use", microphone.Count > 0, privacy ? null : Attrs(("apps", ToArray(microphone))));
    }

    private static JsonArray ToArray(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    /// <summary>Apps with LastUsedTimeStop == 0 (currently using the device).</summary>
    private static List<string> AppsInUse(string capability)
    {
        var apps = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{capability}");
            if (root is null)
            {
                return apps;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var nonPackaged = root.OpenSubKey(name);
                    if (nonPackaged is null)
                    {
                        continue;
                    }

                    foreach (var appKey in nonPackaged.GetSubKeyNames())
                    {
                        using var key = nonPackaged.OpenSubKey(appKey);
                        if (key is not null && InUse(key))
                        {
                            // "C:#Program Files#App#app.exe" -> "app.exe"
                            apps.Add(Path.GetFileName(appKey.Replace('#', '\\')));
                        }
                    }
                }
                else
                {
                    using var key = root.OpenSubKey(name);
                    if (key is not null && InUse(key))
                    {
                        // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Microsoft.WindowsCamera"
                        var underscore = name.IndexOf('_');
                        apps.Add(underscore > 0 ? name[..underscore] : name);
                    }
                }
            }
        }
        catch
        {
            // Consent store not available.
        }

        return apps.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool InUse(RegistryKey key)
    {
        var start = key.GetValue("LastUsedTimeStart") is long s ? s : 0;
        var stop = key.GetValue("LastUsedTimeStop") is long e ? e : -1;
        return start > 0 && stop == 0;
    }

    public void Dispose() => _watcher?.Dispose();
}
