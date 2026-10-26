using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Console session state (active/locked/logged off) and all user sessions (RDP).</summary>
public sealed class SessionProvider : EntityProvider
{
    private readonly SessionTracker _tracker;
    private readonly ServiceConfig _config;
    private readonly AsyncSignal _changed = new();

    public SessionProvider(EntityHub hub, SessionTracker tracker, ServiceConfig config, ILogger<SessionProvider> logger)
        : base("sessions", hub, logger)
    {
        _tracker = tracker;
        _config = config;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        var withSessions = ModuleState.IsOn(_config.Modules.Sessions, true);
        var descriptors = new List<EntityDescriptor>
        {
            new()
            {
                Key = "session_state",
                Platform = Platforms.Sensor,
                TranslationKey = "session_state",
                Name = "Session",
                DeviceClass = "enum",
                Options = ["active", "locked", "disconnected", "logged_off"],
            },
        };
        if (withSessions)
        {
            descriptors.Add(new() { Key = "user_sessions", Platform = Platforms.Sensor, TranslationKey = "user_sessions", Name = "User sessions", StateClass = Ha.Measurement, Precision = 0 });
        }

        Publish(descriptors);
        _tracker.Changed += _changed.Set;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Update(withSessions);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Session update failed");
            }

            // Wake up on session events, otherwise poll every 5 seconds.
            try
            {
                await _changed.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                _tracker.Refresh();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Update(bool withSessions)
    {
        var console = _tracker.Console;
        Set("session_state", console?.State ?? "logged_off", Attrs(("user", console?.User), ("session_id", console?.Id)));

        if (!withSessions)
        {
            return;
        }

        var list = new JsonArray();
        var active = 0;
        foreach (var session in _tracker.UserSessions)
        {
            var state = _tracker.StateOf(session);
            if (state is "active" or "locked")
            {
                active++;
            }

            list.Add(new JsonObject
            {
                ["id"] = session.Id,
                ["user"] = SessionTracker.Format(session),
                ["state"] = state,
                ["station"] = session.Station,
                ["client"] = string.IsNullOrEmpty(session.Client) ? null : session.Client,
                ["logon"] = session.LogonTime?.ToUniversalTime().ToString("O"),
            });
        }

        Set("user_sessions", (long)list.Count, new JsonObject { ["active"] = active, ["sessions"] = list });
    }
}
