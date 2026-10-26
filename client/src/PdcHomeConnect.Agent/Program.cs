using System.Diagnostics;
using System.Text.Json;
using Microsoft.Toolkit.Uwp.Notifications;
using PdcHomeConnect.Agent.Claude;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Agent.UI;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using Serilog;
using Serilog.Extensions.Logging;

namespace PdcHomeConnect.Agent;

internal static class Program
{
    private const string MutexName = @"Local\PdcHomeConnect.Agent";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--uninstall-cleanup", StringComparison.OrdinalIgnoreCase)))
        {
            return UninstallCleanup();
        }

        // One tray app per user session. "--restart" waits for the previous instance to exit.
        var restart = args.Any(a => a.Equals("--restart", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(false, MutexName);
        if (!Acquire(mutex, restart ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
        {
            return 0;
        }

        try
        {
            return Run(args);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Called by the (elevated) uninstaller: removes the Claude Code hooks of all user profiles
    /// (they would point to the deleted hook helper) and the toast registration of this user.
    /// </summary>
    private static int UninstallCleanup()
    {
        foreach (var settings in ClaudeHooks.AllProfileSettings())
        {
            try
            {
                ClaudeHooks.Uninstall(settings);
            }
            catch
            {
                // Best effort: unreadable or locked file.
            }
        }

        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch
        {
            // Best effort.
        }

        return 0;
    }

    private static bool Acquire(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed: the mutex is ours now.
            return true;
        }
    }

    private static int Run(string[] args)
    {
        AgentPaths.EnsureCreated();
        var debug = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase))
            || File.Exists(Path.Combine(AgentPaths.LocalRoot, "debug"));
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Is(debug ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(AgentPaths.Logs, "agent-.log"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        using var loggerFactory = new SerilogLoggerFactory(Serilog.Log.Logger, dispose: false);
        var logger = loggerFactory.CreateLogger("Agent");
        logger.LogInformation("PDC HomeConnect tray app {Version} starting (session {Session})",
            typeof(Program).Assembly.GetName().Version?.ToString(3), Process.GetCurrentProcess().SessionId);

        Application.ThreadException += (_, e) => logger.LogError(e.Exception, "Unhandled UI exception");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogDebug(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.System);

        var config = JsonConfig.Load<AgentConfig>(AgentPaths.Config, AgentConfig.DefaultTemplate(), logger);
        var state = AgentState.Load();

        // A hidden control gives background code access to the UI thread.
        using var dispatcherControl = new Control();
        _ = dispatcherControl.Handle;
        using var window = new MessageWindow();

        AgentHost? host = null;
        var context = new AgentContext
        {
            Hub = new EntityHub(loggerFactory.CreateLogger<EntityHub>()),
            Config = config,
            State = state,
            Signals = new AgentSignals { Privacy = state.PrivacyMode },
            Ui = new UiDispatcher(dispatcherControl),
            Window = window,
            LoggerFactory = loggerFactory,
            SendToService = message => host?.SendAsync(message) ?? Task.CompletedTask,
        };

        host = new AgentHost(context);
        host.Start();

        using var tray = new TrayContext(host, loggerFactory.CreateLogger<TrayContext>());
        using var configWatcher = JsonConfig.Watch(AgentPaths.Config, () => context.Ui.Post(() => OnConfigChanged(tray, logger)));
        Application.Run(tray);

        logger.LogInformation("Tray app stopping");
        try
        {
            host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Shutdown failed");
        }

        Serilog.Log.CloseAndFlush();
        return 0;
    }

    /// <summary>agent.json changed: restart to apply it, unless the file is invalid.</summary>
    private static void OnConfigChanged(TrayContext tray, Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            var text = File.ReadAllText(AgentPaths.Config);
            JsonSerializer.Deserialize<AgentConfig>(text, Json.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("agent.json is invalid: {Message}", ex.Message);
            tray.ShowMessage("Settings not applied", $"agent.json contains an error: {ex.Message}");
            return;
        }
        catch (IOException)
        {
            // Still being written: the watcher fires again.
            return;
        }

        logger.LogInformation("agent.json changed, restarting");
        tray.Restart();
    }
}
