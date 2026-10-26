using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Claude;
using PdcHomeConnect.Agent.Modules;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent.UI;

/// <summary>The notification area icon and its menu. The app has no other visible window.</summary>
public sealed class TrayContext : ApplicationContext
{
    private const string DocsUrl = "https://github.com/PD-Codes/pdc_homeconnect#readme";

    private readonly AgentHost _host;
    private readonly ILogger _logger;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _iconConnected;
    private readonly Icon _iconDisconnected;
    private readonly Icon _iconPairing;
    private AgentStatus _status = null!;
    private StatusForm? _statusForm;
    private DiscordSetupForm? _discordForm;
    private bool _exiting;

    public TrayContext(AgentHost host, ILogger<TrayContext> logger)
    {
        _host = host;
        _logger = logger;
        _iconConnected = LoadIcon("tray-connected.ico");
        _iconDisconnected = LoadIcon("tray-disconnected.ico");
        _iconPairing = LoadIcon("tray-pairing.ico");

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };

        _icon = new NotifyIcon
        {
            Icon = _iconDisconnected,
            Text = "PDC HomeConnect",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowStatus();
            }
        };

        // Subscribe first, then read: a status change in between must not be lost.
        host.StatusChanged += status => host.Context.Ui.Post(() => OnStatusChanged(status));
        _status = host.Status;
        OnStatusChanged(_status);
    }

    private static Icon LoadIcon(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private static int ClientCount(AgentStatus status) => status.ServiceStatus?.GetArray("connected_clients")?.Count ?? 0;

    private void OnStatusChanged(AgentStatus status)
    {
        if (_exiting)
        {
            return;
        }

        _status = status;
        var clients = ClientCount(status);
        string text;
        if (status.Pairing is not null)
        {
            _icon.Icon = _iconPairing;
            text = $"PDC HomeConnect\nPairing code: {status.Pairing.Display}";
        }
        else if (!status.ServiceConnected)
        {
            _icon.Icon = _iconDisconnected;
            text = "PDC HomeConnect\nService not running";
        }
        else if (clients > 0)
        {
            _icon.Icon = _iconConnected;
            text = "PDC HomeConnect\nConnected to Home Assistant";
        }
        else
        {
            _icon.Icon = _iconDisconnected;
            text = "PDC HomeConnect\nWaiting for Home Assistant";
        }

        _icon.Text = text.Length > 127 ? text[..127] : text;
        _statusForm?.UpdateStatus(status);
    }

    // ------------------------------------------------------------------ menu

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var version = typeof(TrayContext).Assembly.GetName().Version?.ToString(3);
        var header = new ToolStripMenuItem($"PDC HomeConnect {version}") { Enabled = false };
        header.Font = new Font(header.Font, FontStyle.Bold);
        _menu.Items.Add(header);

        var clients = ClientCount(_status);
        var line = !_status.ServiceConnected ? "Service: not running"
            : clients == 0 ? "Home Assistant: not connected"
            : clients == 1 ? "Home Assistant: connected"
            : $"Home Assistant: {clients} connections";
        _menu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
        if (_status.ServiceConnected && !_status.Primary)
        {
            _menu.Items.Add(new ToolStripMenuItem("Another user session is active") { Enabled = false });
        }

        if (_status.Pairing is { } pairing)
        {
            _menu.Items.Add(new ToolStripMenuItem($"Pairing code: {pairing.Display} (copy)", null, (_, _) => Clipboard.SetText(pairing.Code)));
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Status…", null, (_, _) => ShowStatus()));

        if (_host.Module<ActivityModule>() is { } activity)
        {
            _menu.Items.Add(new ToolStripMenuItem("Privacy mode", null, (_, _) => activity.SetPrivacy(!activity.PrivacyMode))
            {
                Checked = activity.PrivacyMode,
                ToolTipText = "Hides app names, window titles, media and Discord details from Home Assistant.",
            });
        }

        if (_host.Module<InputModule>() is { } input && input.TrayActions.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
            foreach (var action in input.TrayActions)
            {
                _menu.Items.Add(new ToolStripMenuItem(action.Name ?? action.Id, null, (_, _) => input.FireTrayAction(action.Id)));
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        if (_host.Module<DiscordModule>() is { } discord)
        {
            _menu.Items.Add(new ToolStripMenuItem($"Discord: {DiscordSetupForm.Describe(discord.Status)}…", null, (_, _) => ShowDiscordSetup()));
        }

        if (_host.Module<ClaudeModule>() is { } claude && ClaudeHooks.ClaudeCodeInstalled)
        {
            var installed = claude.HooksInstalled;
            _menu.Items.Add(new ToolStripMenuItem("Claude Code status (hooks)", null, (_, _) =>
            {
                var error = claude.SetHooks(!installed);
                if (error is not null)
                {
                    ShowMessage("Claude Code hooks", error);
                }
            })
            {
                Checked = installed,
                ToolTipText = "Reports whether Claude Code is working via hooks in ~/.claude/settings.json.",
            });
        }

        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add(new ToolStripMenuItem("Edit tray app settings (agent.json)", null, (_, _) => OpenEditor(AgentPaths.Config, elevated: false)));
        settings.DropDownItems.Add(new ToolStripMenuItem("Edit service settings (service.json)", null, (_, _) => OpenEditor(AgentPaths.ServiceConfig, elevated: true)));
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => Open(AgentPaths.Logs)));
        settings.DropDownItems.Add(new ToolStripMenuItem("Documentation", null, (_, _) => Open(DocsUrl)));
        _menu.Items.Add(settings);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync()));
    }

    // ------------------------------------------------------------------ actions

    private void ShowStatus()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_host);
            _statusForm.FormClosed += (_, _) => _statusForm = null;
        }

        _statusForm.UpdateStatus(_status);
        _statusForm.Show();
        _statusForm.Activate();
    }

    private void ShowDiscordSetup()
    {
        var module = _host.Module<DiscordModule>();
        if (module is null)
        {
            return;
        }

        if (_discordForm is null || _discordForm.IsDisposed)
        {
            _discordForm = new DiscordSetupForm(module);
            _discordForm.FormClosed += (_, _) => _discordForm = null;
        }

        _discordForm.Show();
        _discordForm.Activate();
    }

    private void OpenEditor(string path, bool elevated)
    {
        try
        {
            if (!elevated && !File.Exists(path))
            {
                File.WriteAllText(path, AgentConfig.DefaultTemplate());
            }

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty,
            })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC prompt cancelled.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {Path}", path);
            ShowMessage("PDC HomeConnect", ex.Message);
        }
    }

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {Target}", target);
        }
    }

    public void ShowMessage(string title, string text)
    {
        if (_host.Module<NotificationModule>() is { } notifications)
        {
            notifications.ShowInfo(title, text);
        }
        else
        {
            _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        }
    }

    /// <summary>Starts a new instance (it waits for this one) and exits.</summary>
    public void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restart failed");
            return;
        }

        Quit();
    }

    private async Task ExitAsync()
    {
        // Tell the service that the user closed the app on purpose (no automatic restart).
        try
        {
            await _host.SendAsync(new JsonObject { ["type"] = "exit" }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not notify the service");
        }

        Quit();
    }

    private void Quit()
    {
        _exiting = true;
        _icon.Visible = false;
        _statusForm?.Close();
        _discordForm?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _iconConnected.Dispose();
            _iconDisconnected.Dispose();
            _iconPairing.Dispose();
        }

        base.Dispose(disposing);
    }
}
