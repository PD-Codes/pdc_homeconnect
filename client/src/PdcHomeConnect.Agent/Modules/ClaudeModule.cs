using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Claude;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Ipc;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>
/// Claude Code status from its hooks: idle, working, waiting for input or error, plus events
/// (prompt submitted, waiting, finished, failed, session start/end). Hooks reach this module through
/// the helper exe and a per-session named pipe.
/// </summary>
public sealed class ClaudeModule : EntityProvider
{
    private static readonly TimeSpan StaleBusy = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StaleSession = TimeSpan.FromHours(12);

    private sealed class Session(string id)
    {
        public string Id { get; } = id;
        public string State { get; set; } = "idle";
        public string? Cwd { get; set; }
        public string? Tool { get; set; }
        public string? Message { get; set; }
        public DateTimeOffset LastEvent { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? WorkingSince { get; set; }
    }

    private readonly AgentContext _context;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = [];
    private string? _lastEvent;
    private DateTimeOffset? _lastEventTime;
    private bool _hooksInstalled;

    public ClaudeModule(AgentContext context, ILogger<ClaudeModule> logger)
        : base("claude_code", context.Hub, logger)
    {
        _context = context;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new()
            {
                Key = "claude_code",
                Platform = Platforms.Sensor,
                TranslationKey = "claude_code",
                Name = "Claude Code",
                DeviceClass = "enum",
                Options = ["idle", "working", "waiting", "error"],
                OfflineState = JsonValue.Create("idle"),
            },
            new()
            {
                Key = "claude_code_event",
                Platform = Platforms.Event,
                TranslationKey = "claude_code_event",
                Name = "Claude Code event",
                EventTypes = ["prompt_submitted", "waiting_for_input", "finished", "failed", "session_started", "session_ended"],
            },
        ]);
        EnsureHooks();
        PublishState();

        _ = RunPeriodicAsync(TimeSpan.FromMinutes(1), _ =>
        {
            var installed = HooksInstalled;
            if (installed != _hooksInstalled)
            {
                _hooksInstalled = installed;
                PublishState();
            }

            ExpireSessions();
            return Task.CompletedTask;
        }, cancellationToken);

        await ServeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs the hooks automatically unless the user removed them from the tray menu.</summary>
    private void EnsureHooks()
    {
        _hooksInstalled = HooksInstalled;
        if (_context.State.ClaudeHooksDisabled || !ClaudeHooks.ClaudeCodeInstalled || !File.Exists(AgentPaths.HookExecutable))
        {
            return;
        }

        try
        {
            if (ClaudeHooks.Install(AgentPaths.HookExecutable))
            {
                Logger.LogInformation("Claude Code hooks installed in {Path}", ClaudeHooks.SettingsPath);
            }

            _hooksInstalled = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not install the Claude Code hooks");
        }
    }

    public bool HooksInstalled
    {
        get
        {
            try
            {
                return ClaudeHooks.IsInstalled();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Tray menu: install or remove the hooks.</summary>
    public string? SetHooks(bool install)
    {
        try
        {
            if (install)
            {
                if (!File.Exists(AgentPaths.HookExecutable))
                {
                    return $"The hook helper was not found: {AgentPaths.HookExecutable}";
                }

                ClaudeHooks.Install(AgentPaths.HookExecutable);
            }
            else
            {
                ClaudeHooks.Uninstall();
            }

            _context.State.ClaudeHooksDisabled = !install;
            _context.State.Save();
            _hooksInstalled = install;
            PublishState();
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Changing the Claude Code hooks failed");
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ pipe server

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        var name = IpcNames.AgentHookPipe(_context.SessionId);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connection = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(connection, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Hook pipe failed");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var text = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                if (text.Length is > 0 and < 1024 * 1024 && JsonNode.Parse(text) is JsonObject payload)
                {
                    Handle(payload);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Invalid hook message");
            }
        }
    }

    // ------------------------------------------------------------------ state machine

    private void Handle(JsonObject payload)
    {
        var name = payload.GetString("hook_event_name");
        var sessionId = payload.GetString("session_id") ?? "default";
        if (name is null)
        {
            return;
        }

        string? eventType = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new Session(sessionId);
                _sessions[sessionId] = session;
            }

            session.LastEvent = DateTimeOffset.UtcNow;
            session.Cwd = payload.GetString("cwd") ?? session.Cwd;
            switch (name)
            {
                case "SessionStart":
                    session.State = "idle";
                    eventType = "session_started";
                    break;
                case "UserPromptSubmit":
                    SetWorking(session);
                    session.Tool = null;
                    session.Message = null;
                    eventType = "prompt_submitted";
                    break;
                case "PreToolUse":
                case "PostToolUse":
                    SetWorking(session);
                    session.Tool = payload.GetString("tool_name");
                    break;
                case "PermissionRequest":
                    session.State = "waiting";
                    session.Tool = payload.GetString("tool_name") ?? session.Tool;
                    eventType = "waiting_for_input";
                    break;
                case "Notification":
                    var type = payload.GetString("notification_type");
                    session.Message = payload.GetString("message");
                    if (type is "permission_prompt" or "elicitation_dialog")
                    {
                        if (session.State != "waiting")
                        {
                            eventType = "waiting_for_input";
                        }

                        session.State = "waiting";
                    }
                    else if (type == "idle_prompt")
                    {
                        session.State = "idle";
                    }
                    break;
                case "Stop":
                    session.State = "idle";
                    session.WorkingSince = null;
                    session.Tool = null;
                    eventType = "finished";
                    break;
                case "StopFailure":
                    session.State = "error";
                    session.WorkingSince = null;
                    session.Message = payload.GetString("error") ?? payload.GetString("message") ?? session.Message;
                    eventType = "failed";
                    break;
                case "SessionEnd":
                    _sessions.Remove(sessionId);
                    eventType = "session_ended";
                    break;
            }
        }

        if (eventType is not null)
        {
            _lastEvent = eventType;
            _lastEventTime = DateTimeOffset.UtcNow;
            var privacy = _context.Signals.Privacy;
            var cwd = payload.GetString("cwd");
            Hub.FireEvent("claude_code_event", eventType, Attrs(
                ("session_id", sessionId),
                ("project", privacy || cwd is null ? null : Path.GetFileName(cwd.TrimEnd('\\', '/'))),
                ("message", privacy ? null : payload.GetString("message"))));
        }

        PublishState();
    }

    private static void SetWorking(Session session)
    {
        if (session.State != "working")
        {
            session.WorkingSince ??= DateTimeOffset.UtcNow;
        }

        session.State = "working";
    }

    private void ExpireSessions()
    {
        var changed = false;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var session in _sessions.Values.ToList())
            {
                if (now - session.LastEvent > StaleSession)
                {
                    _sessions.Remove(session.Id);
                    changed = true;
                }
                else if (session.State is "working" or "waiting" && now - session.LastEvent > StaleBusy)
                {
                    // Claude Code was closed without a Stop/SessionEnd hook (crash, killed terminal).
                    session.State = "idle";
                    session.WorkingSince = null;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            PublishState();
        }
    }

    private void PublishState()
    {
        string state;
        JsonObject attributes;
        lock (_gate)
        {
            var sessions = _sessions.Values.ToList();
            state = sessions.Any(s => s.State == "working") ? "working"
                : sessions.Any(s => s.State == "waiting") ? "waiting"
                : sessions.Any(s => s.State == "error") ? "error"
                : "idle";
            var current = sessions
                .OrderByDescending(s => s.State == state)
                .ThenByDescending(s => s.LastEvent)
                .FirstOrDefault();
            var privacy = _context.Signals.Privacy;
            attributes = Attrs(
                ("sessions", (long)sessions.Count),
                ("working_sessions", (long)sessions.Count(s => s.State == "working")),
                ("project", privacy || current?.Cwd is null ? null : Path.GetFileName(current.Cwd.TrimEnd('\\', '/'))),
                ("tool", privacy ? null : current?.Tool),
                ("message", privacy ? null : current?.Message),
                ("working_since", current?.WorkingSince),
                ("last_event", _lastEvent),
                ("last_event_at", _lastEventTime),
                ("hooks_installed", _hooksInstalled));
        }

        _context.Signals.ClaudeWorking = state == "working";
        Set("claude_code", state, attributes);
    }
}
