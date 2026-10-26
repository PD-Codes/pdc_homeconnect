using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>Lock state, idle time/AFK, display power and the lock/display buttons.</summary>
public sealed class SessionModule : EntityProvider
{
    private readonly AgentContext _context;

    public SessionModule(AgentContext context, ILogger<SessionModule> logger)
        : base("session", context.Hub, logger)
    {
        _context = context;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new() { Key = "afk", Platform = Platforms.BinarySensor, TranslationKey = "afk", Name = "AFK", OfflineState = JsonValue.Create(true) },
            new() { Key = "idle_time", Platform = Platforms.Sensor, TranslationKey = "idle_time", Name = "Idle time", DeviceClass = "duration", Unit = "min", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "monitor_power", Platform = Platforms.BinarySensor, TranslationKey = "monitor_power", Name = "Displays" },
            new() { Key = "lock", Platform = Platforms.Button, TranslationKey = "lock", Name = "Lock" },
            new() { Key = "monitor_off", Platform = Platforms.Button, TranslationKey = "monitor_off", Name = "Turn off displays" },
            new() { Key = "monitor_wake", Platform = Platforms.Button, TranslationKey = "monitor_wake", Name = "Wake displays" },
        ]);

        foreach (var button in new[] { "lock", "monitor_off", "monitor_wake" })
        {
            Set(button, (JsonNode?)null);
        }

        var window = _context.Window;
        window.SessionLockChanged += OnLockChanged;
        window.DisplayStateChanged += OnDisplayStateChanged;
        if (window.DisplayState is { } display)
        {
            OnDisplayStateChanged(display);
        }
        else
        {
            Set("monitor_power", true);
        }

        OnLockChanged(Native.Native.IsSessionLocked(_context.SessionId) ?? false);

        await RunPeriodicAsync(TimeSpan.FromSeconds(1), _ =>
        {
            var idle = Native.Native.IdleTime();
            var afkLimit = TimeSpan.FromMinutes(Math.Max(1, _context.Config.AfkMinutes));
            // Gamepad input does not count as input for Windows, so gaming never counts as AFK.
            var afk = _context.Signals.Locked || (idle >= afkLimit && !_context.Signals.Gaming);
            _context.Signals.Afk = afk;
            Set("afk", afk, Attrs(("idle_minutes", (long)idle.TotalMinutes)));
            Set("idle_time", (long)Math.Floor(idle.TotalMinutes));
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void OnLockChanged(bool locked)
    {
        _context.Signals.Locked = locked;
        _ = _context.SendToService(new JsonObject { ["type"] = "session", ["locked"] = locked });
    }

    private void OnDisplayStateChanged(int state) => Set("monitor_power", state != 0, Attrs(("dimmed", state == 2)));

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        switch (command.Key)
        {
            case "lock":
                return Native.Native.LockWorkStation() ? CommandResult.Ok() : CommandResult.Fail(ErrorCodes.Failed, "LockWorkStation failed.");
            case "monitor_off":
                await _context.Ui.InvokeAsync(_context.Window.TurnOffDisplays).ConfigureAwait(false);
                return CommandResult.Ok();
            case "monitor_wake":
                Native.Native.SetThreadExecutionState(Native.Native.EsDisplayRequired);
                Native.Native.NudgeMouse();
                return CommandResult.Ok();
            default:
                return CommandResult.NotSupported(command.Action);
        }
    }
}
