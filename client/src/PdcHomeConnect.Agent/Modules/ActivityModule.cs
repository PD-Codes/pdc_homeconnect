using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>
/// Combined "what is the user doing" sensor and the privacy mode switch.
/// Priority: locked, gaming, call, watching media (fullscreen or AFK), AFK, working, media, active.
/// </summary>
public sealed class ActivityModule : EntityProvider
{
    private readonly AgentContext _context;
    private readonly object _gate = new();
    private string? _activity;
    private DateTimeOffset _since = DateTimeOffset.UtcNow;

    public ActivityModule(AgentContext context, ILogger<ActivityModule> logger)
        : base("activity", context.Hub, logger)
    {
        _context = context;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new()
            {
                Key = "activity",
                Platform = Platforms.Sensor,
                TranslationKey = "activity",
                Name = "Activity",
                DeviceClass = "enum",
                Options = ["locked", "gaming", "call", "afk", "working", "media", "active", "logged_off", "unknown"],
            },
            new() { Key = "privacy_mode", Platform = Platforms.Switch, TranslationKey = "privacy_mode", Name = "Privacy mode", EntityCategory = Ha.Config },
        ]);

        _context.Signals.Privacy = _context.State.PrivacyMode;
        Set("privacy_mode", _context.State.PrivacyMode);
        _context.Signals.Changed += Update;
        Update();

        // Safety net (signals are event driven).
        await RunPeriodicAsync(TimeSpan.FromSeconds(30), _ =>
        {
            Update();
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Update()
    {
        var s = _context.Signals;
        var foreground = s.ForegroundProcess;
        var callApps = _context.Config.CallApps;
        var inCall = s.DiscordVoice
            || s.MicrophoneApps.Any(a => callApps.Contains(a, StringComparer.OrdinalIgnoreCase))
            || s.WebcamApps.Any(a => callApps.Contains(a, StringComparer.OrdinalIgnoreCase));
        var working = s.ClaudeWorking
            || (foreground is not null && _context.Config.WorkApps.Contains(foreground, StringComparer.OrdinalIgnoreCase));
        var watching = s.MediaPlaying && (s.Fullscreen is "fullscreen" or "presentation" || s.Afk);

        var activity = s.Locked ? "locked"
            : s.Gaming ? "gaming"
            : inCall ? "call"
            : watching ? "media"
            : s.Afk ? "afk"
            : working ? "working"
            : s.MediaPlaying ? "media"
            : "active";

        lock (_gate)
        {
            if (activity != _activity)
            {
                _activity = activity;
                _since = DateTimeOffset.UtcNow;
            }
        }

        var privacy = s.Privacy;
        Set("activity", activity, Attrs(
            ("since", _since),
            ("game", activity == "gaming" && !privacy ? s.Game : null),
            ("app", activity == "working" && !privacy ? foreground : null)));
    }

    public bool PrivacyMode => _context.State.PrivacyMode;

    /// <summary>Privacy mode hides app names, window titles, media and Discord details.</summary>
    public void SetPrivacy(bool enabled)
    {
        _context.State.PrivacyMode = enabled;
        _context.State.Save();
        _context.Signals.Privacy = enabled;
        Set("privacy_mode", enabled);
        PrivacyChanged?.Invoke(enabled);
    }

    public event Action<bool>? PrivacyChanged;

    public override Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (command.Key == "privacy_mode" && command.Action is Actions.TurnOn or Actions.TurnOff)
        {
            SetPrivacy(command.Action == Actions.TurnOn);
            return Task.FromResult(CommandResult.Ok());
        }

        return Task.FromResult(CommandResult.NotSupported(command.Action));
    }
}
