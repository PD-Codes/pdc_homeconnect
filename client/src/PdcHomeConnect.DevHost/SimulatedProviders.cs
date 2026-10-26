using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.DevHost;

/// <summary>Simulated system statistics.</summary>
public sealed class SimulatedSystemProvider : EntityProvider
{
    private readonly Random _random = new();

    public SimulatedSystemProvider(EntityHub hub, ILogger<SimulatedSystemProvider> logger)
        : base("system", hub, logger)
    {
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new EntityDescriptor { Key = "cpu_load", Platform = Platforms.Sensor, TranslationKey = "cpu_load", Name = "CPU load", Unit = "%", StateClass = Ha.Measurement, Precision = 0 },
            new EntityDescriptor { Key = "memory_usage", Platform = Platforms.Sensor, TranslationKey = "memory_usage", Name = "Memory usage", Unit = "%", StateClass = Ha.Measurement, Precision = 0 },
            new EntityDescriptor { Key = "disk_usage_c", Platform = Platforms.Sensor, TranslationKey = "disk_usage", Name = "Disk C: usage", Placeholders = new() { ["disk"] = "C:" }, Unit = "%", StateClass = Ha.Measurement, Precision = 0 },
            new EntityDescriptor { Key = "last_boot", Platform = Platforms.Sensor, TranslationKey = "last_boot", Name = "Last boot", DeviceClass = "timestamp", EntityCategory = Ha.Diagnostic },
            new EntityDescriptor { Key = "shutdown", Platform = Platforms.Button, TranslationKey = "shutdown", Name = "Shut down" },
        ]);

        var boot = DateTimeOffset.UtcNow.AddHours(-3);
        return RunPeriodicAsync(TimeSpan.FromSeconds(2), _ =>
        {
            Set("cpu_load", _random.Next(2, 60), 0, Attrs(("cores", 16)));
            Set("memory_usage", 40 + _random.NextDouble() * 5, 0, Attrs(("used_gb", 12.5), ("total_gb", 32)));
            Set("disk_usage_c", 61.0, 0, Attrs(("free_gb", 180.2), ("total_gb", 476.9)));
            Set("last_boot", Timestamp(boot));
            Set("shutdown", (JsonNode?)null);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public override Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Simulated system command {Action} on {Key}", command.Action, command.Key);
        return Task.FromResult(command.Key == "shutdown" && command.Action == Actions.Press
            ? CommandResult.Ok()
            : CommandResult.NotSupported(command.Action));
    }
}

/// <summary>Simulated tray agent entities: switches, selects, events, media player.</summary>
public sealed class SimulatedUserProvider : EntityProvider
{
    private static SimulatedUserProvider? _instance;
    private bool _darkMode;
    private double _brightness = 70;
    private string _output = "Speakers";
    private bool _playing = true;
    private double _volume = 0.35;
    private bool _muted;

    public SimulatedUserProvider(EntityHub hub, ILogger<SimulatedUserProvider> logger)
        : base("agent", hub, logger)
    {
        _instance = this;
    }

    public static void RegisterMethods(EntityHub hub, ILogger logger)
    {
        hub.RegisterMethod("notify", async (parameters, ct) =>
        {
            logger.LogInformation("NOTIFY {Params}", parameters.ToJsonString());
            if (parameters["actions"] is JsonArray { Count: > 0 } actions)
            {
                var first = actions[0]?["action"]?.ToString() ?? "action";
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500, CancellationToken.None);
                    var data = new JsonObject { ["action"] = first, ["tag"] = parameters["tag"]?.DeepClone() };
                    hub.FireEvent("notification_action", "action", data);
                    hub.FireBusEvent("notification_action", (JsonObject)data.DeepClone());
                }, CancellationToken.None);
            }
            await Task.CompletedTask;
            return CommandResult.Ok();
        });
        hub.RegisterMethod("open_url", (parameters, ct) =>
        {
            logger.LogInformation("OPEN URL {Url}", parameters["url"]);
            return Task.FromResult(CommandResult.Ok());
        });
        hub.RegisterMethod("power", (parameters, ct) =>
        {
            logger.LogInformation("POWER {Params}", parameters.ToJsonString());
            return Task.FromResult(CommandResult.Ok());
        });
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new EntityDescriptor { Key = "dark_mode", Platform = Platforms.Switch, TranslationKey = "dark_mode", Name = "Dark mode" },
            new EntityDescriptor { Key = "gaming", Platform = Platforms.BinarySensor, TranslationKey = "gaming", Name = "Gaming", OfflineState = JsonValue.Create(false) },
            new EntityDescriptor { Key = "activity", Platform = Platforms.Sensor, TranslationKey = "activity", Name = "Activity", DeviceClass = "enum", Options = ["locked", "gaming", "call", "afk", "working", "media", "active", "logged_off", "unknown"] },
            new EntityDescriptor { Key = "current_game", Platform = Platforms.Sensor, TranslationKey = "current_game", Name = "Current game" },
            new EntityDescriptor { Key = "monitor_1_brightness", Platform = Platforms.Number, TranslationKey = "monitor_brightness", Name = "Monitor 1 brightness", Placeholders = new() { ["monitor"] = "1" }, Min = 0, Max = 100, Step = 1, Unit = "%", Mode = "slider" },
            new EntityDescriptor { Key = "audio_output", Platform = Platforms.Select, TranslationKey = "audio_output", Name = "Audio output", Options = ["Speakers", "Headset"] },
            new EntityDescriptor { Key = "hotkey", Platform = Platforms.Event, TranslationKey = "hotkey", Name = "Hotkey", EventTypes = ["toggle_light"] },
            new EntityDescriptor { Key = "notification_action", Platform = Platforms.Event, TranslationKey = "notification_action", Name = "Notification action", EventTypes = ["action", "reply", "dismissed"] },
            new EntityDescriptor { Key = "lock", Platform = Platforms.Button, TranslationKey = "lock", Name = "Lock" },
            new EntityDescriptor
            {
                Key = "media",
                Platform = Platforms.MediaPlayer,
                TranslationKey = "media",
                Name = "Media",
                Features = ["play", "pause", "stop", "next_track", "previous_track", "volume_set", "volume_mute", "volume_step", "seek", "play_media"],
            },
        ]);

        // A tiny 1x1 PNG as "cover art".
        Hub.SetBlob("media_cover", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), "image/png");
        PublishAll();
        var tick = 0;
        return RunPeriodicAsync(TimeSpan.FromSeconds(15), _ =>
        {
            tick++;
            if (tick % 4 == 0)
            {
                Hub.FireEvent("hotkey", "toggle_light", Attrs(("keys", "Ctrl+Alt+L")));
            }
            PublishAll();
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private void PublishAll()
    {
        Set("dark_mode", _darkMode);
        Set("gaming", true, Attrs(("game", "Satisfactory")));
        Set("activity", "gaming");
        Set("current_game", "Satisfactory", Attrs(("app_id", 526870), ("source", "steam"), ("entity_picture", "https://cdn.cloudflare.steamstatic.com/steam/apps/526870/header.jpg")));
        Set("monitor_1_brightness", _brightness, 0);
        Hub.SetState("audio_output", JsonValue.Create(_output));
        Set("lock", (JsonNode?)null);
        Hub.SetState("media", JsonValue.Create(_playing ? "playing" : "paused"), Attrs(
            ("media_title", "Simulated Song"),
            ("media_artist", "PD-Codes"),
            ("media_album_name", "DevHost"),
            ("media_duration", 215),
            ("media_position", 42),
            ("media_position_updated_at", DateTimeOffset.UtcNow),
            ("volume_level", _volume),
            ("is_volume_muted", _muted),
            ("source", "Spotify"),
            ("media_image_hash", Hub.GetBlob("media_cover")?.Hash)));
    }

    public override Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Simulated user command {Action} on {Key} {Params}", command.Action, command.Key, command.Params.ToJsonString());
        switch (command.Key, command.Action)
        {
            case ("dark_mode", Actions.TurnOn):
                _darkMode = true;
                break;
            case ("dark_mode", Actions.TurnOff):
                _darkMode = false;
                break;
            case ("monitor_1_brightness", Actions.SetValue):
                _brightness = command.Params.GetDouble("value") ?? _brightness;
                break;
            case ("audio_output", Actions.SelectOption):
                _output = command.Params.GetString("option") ?? _output;
                break;
            case ("lock", Actions.Press):
                break;
            case ("media", Actions.MediaPlay):
                _playing = true;
                break;
            case ("media", Actions.MediaPause):
                _playing = false;
                break;
            case ("media", Actions.MediaPlayPause):
                _playing = !_playing;
                break;
            case ("media", Actions.VolumeSet):
                _volume = command.Params.GetDouble("volume") ?? _volume;
                break;
            case ("media", Actions.VolumeMute):
                _muted = command.Params.GetBool("mute") ?? _muted;
                break;
            case ("media", Actions.MediaNext or Actions.MediaPrevious or Actions.MediaStop or Actions.MediaSeek or Actions.PlayMedia or Actions.VolumeUp or Actions.VolumeDown):
                break;
            default:
                return Task.FromResult(CommandResult.NotSupported(command.Action));
        }

        PublishAll();
        return Task.FromResult(CommandResult.Ok());
    }
}
