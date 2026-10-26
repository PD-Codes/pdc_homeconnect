using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace PdcHomeConnect.Service;

/// <summary>Power and session events from the service control manager.</summary>
public sealed class SystemEvents
{
    public event Action<PowerBroadcastStatus>? Power;

    public event Action<SessionChangeReason, int>? Session;

    /// <summary>Set by the lifetime when running as a Windows service.</summary>
    public Action? RestartRequested { get; set; }

    internal void RaisePower(PowerBroadcastStatus status) => Power?.Invoke(status);

    internal void RaiseSession(SessionChangeReason reason, int sessionId) => Session?.Invoke(reason, sessionId);

    /// <summary>Restarts the service (used after config changes). Relies on the SCM recovery settings.</summary>
    public void RequestRestart() => RestartRequested?.Invoke();
}

/// <summary>
/// Windows service lifetime that additionally receives power (sleep/resume) and session (lock/logon)
/// notifications from the service control manager.
/// </summary>
public sealed class PdcServiceLifetime : WindowsServiceLifetime
{
    private readonly SystemEvents _events;
    private readonly IHostApplicationLifetime _applicationLifetime;

    public PdcServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        SystemEvents events)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _events = events;
        _applicationLifetime = applicationLifetime;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
        _events.RestartRequested = () =>
        {
            // A non-zero exit code makes the SCM apply the recovery action (restart).
            ExitCode = 1;
            _applicationLifetime.StopApplication();
        };
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        try
        {
            _events.RaisePower(powerStatus);
        }
        catch
        {
            // Never block power transitions.
        }

        return base.OnPowerEvent(powerStatus);
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        try
        {
            _events.RaiseSession(changeDescription.Reason, changeDescription.SessionId);
        }
        catch
        {
            // Ignore listener errors.
        }

        base.OnSessionChange(changeDescription);
    }
}
