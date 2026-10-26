using System.ServiceProcess;
using PdcHomeConnect.Service.Native;

namespace PdcHomeConnect.Service;

/// <summary>Tracks Windows sessions: who is signed in at the console and whether it is locked.</summary>
public sealed class SessionTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<int, bool> _lockedByEvent = [];
    private readonly ILogger _logger;
    private List<Win32.SessionInfo> _sessions = [];

    public SessionTracker(SystemEvents events, ILogger<SessionTracker> logger)
    {
        _logger = logger;
        events.Session += OnSessionEvent;
        Refresh();
    }

    /// <summary>Raised whenever sessions or the lock state change.</summary>
    public event Action? Changed;

    public sealed record ConsoleSession(int Id, string? User, string State);

    /// <summary>Console (physical) session, or null if nobody is signed in.</summary>
    public ConsoleSession? Console
    {
        get
        {
            lock (_gate)
            {
                var consoleId = (int)Win32.WTSGetActiveConsoleSessionId();
                var session = _sessions.FirstOrDefault(s => s.Id == consoleId);
                if (session is null || string.IsNullOrEmpty(session.User))
                {
                    return null;
                }

                return new ConsoleSession(session.Id, Format(session), StateOf(session));
            }
        }
    }

    /// <summary>Sessions with a signed-in user.</summary>
    public IReadOnlyList<Win32.SessionInfo> UserSessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Where(s => !string.IsNullOrEmpty(s.User)).ToList();
            }
        }
    }

    /// <summary>Session state for the activity fallback: logged_off, locked, disconnected, active.</summary>
    public string ConsoleState => Console?.State ?? "logged_off";

    public void Refresh()
    {
        List<Win32.SessionInfo> sessions;
        try
        {
            sessions = Win32.GetSessions();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session enumeration failed");
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !Same(_sessions, sessions);
            _sessions = sessions;
            foreach (var stale in _lockedByEvent.Keys.Where(id => sessions.All(s => s.Id != id)).ToList())
            {
                _lockedByEvent.Remove(stale);
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Lock state reported by the tray agent (most reliable source).</summary>
    public void ReportLock(int sessionId, bool locked)
    {
        lock (_gate)
        {
            if (_lockedByEvent.TryGetValue(sessionId, out var current) && current == locked)
            {
                return;
            }
            _lockedByEvent[sessionId] = locked;
        }

        Changed?.Invoke();
    }

    public static string Format(Win32.SessionInfo session) =>
        string.IsNullOrEmpty(session.Domain) || session.Domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? session.User ?? string.Empty
            : $"{session.Domain}\\{session.User}";

    public string StateOf(Win32.SessionInfo session)
    {
        if (string.IsNullOrEmpty(session.User))
        {
            return "logged_off";
        }

        if (session.State == Win32.ConnectState.Disconnected)
        {
            return "disconnected";
        }

        bool locked;
        lock (_gate)
        {
            locked = _lockedByEvent.TryGetValue(session.Id, out var byEvent) ? byEvent : session.Locked ?? false;
        }

        return locked ? "locked" : "active";
    }

    private void OnSessionEvent(SessionChangeReason reason, int sessionId)
    {
        _logger.LogDebug("Session {Id}: {Reason}", sessionId, reason);
        switch (reason)
        {
            case SessionChangeReason.SessionLock:
                ReportLock(sessionId, true);
                break;
            case SessionChangeReason.SessionUnlock:
            case SessionChangeReason.SessionLogon:
                ReportLock(sessionId, false);
                break;
        }

        Refresh();
        Changed?.Invoke();
    }

    private static bool Same(List<Win32.SessionInfo> a, List<Win32.SessionInfo> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.State == p.Second.State && p.First.User == p.Second.User && p.First.Locked == p.Second.Locked);
}
