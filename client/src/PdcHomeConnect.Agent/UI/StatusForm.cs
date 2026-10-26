using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Modules;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent.UI;

/// <summary>Connection overview: device, Home Assistant connections, paired instances (with unpair).</summary>
public sealed class StatusForm : Form
{
    private readonly AgentHost _host;
    private readonly Label _summary;
    private readonly TextBox _fingerprint;
    private readonly ListView _clients;
    private readonly Button _remove;
    private readonly Label _details;

    public StatusForm(AgentHost host)
    {
        _host = host;
        Text = "PDC HomeConnect";
        Icon = LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(560, 460);
        Size = new Size(640, 520);
        ShowInTaskbar = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 7,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _summary = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(_summary);

        layout.Controls.Add(new Label { Text = "Certificate fingerprint (shown in Home Assistant when pairing):", AutoSize = true });
        _fingerprint = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5F), Margin = new Padding(0, 2, 0, 10) };
        layout.Controls.Add(_fingerprint);

        layout.Controls.Add(new Label { Text = "Paired Home Assistant instances:", AutoSize = true });
        _clients = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _clients.Columns.Add("Name", 170);
        _clients.Columns.Add("Status", 110);
        _clients.Columns.Add("Last address", 130);
        _clients.Columns.Add("Last seen", 150);
        _clients.SelectedIndexChanged += (_, _) => _remove!.Enabled = _clients.SelectedItems.Count > 0;
        layout.Controls.Add(_clients);

        _details = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(_details);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        _remove = new Button { Text = "Remove pairing", AutoSize = true, Enabled = false };
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += async (_, _) => await RefreshAsync();
        var logs = new Button { Text = "Open logs", AutoSize = true };
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo(AgentPaths.Logs) { UseShellExecute = true })?.Dispose();
        buttons.Controls.AddRange([close, _remove, refresh, logs]);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        CancelButton = close;
        Shown += async (_, _) => await RefreshAsync();
    }

    private static Icon? LoadAppIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        return stream is null ? null : new Icon(stream);
    }

    public void UpdateStatus(AgentStatus status)
    {
        if (IsDisposed)
        {
            return;
        }

        var service = status.ServiceStatus;
        var version = typeof(StatusForm).Assembly.GetName().Version?.ToString(3);
        var connected = service?.GetArray("connected_clients")?.OfType<JsonObject>().ToList() ?? [];
        var lines = new List<string>
        {
            $"Device: {service?.GetString("device_name") ?? Environment.MachineName}",
            $"Version: {version}" + (service?.GetLong("port") is { } port ? $"    Port: {port}" : string.Empty),
            !status.ServiceConnected ? "Service: not running (it starts automatically with Windows)"
                : connected.Count == 0 ? "Home Assistant: not connected"
                : $"Home Assistant: connected ({string.Join(", ", connected.Select(c => $"{c.GetString("name")} – {c.GetString("address")}"))})",
        };

        if (status.ServiceConnected && !status.Primary)
        {
            lines.Add("This session is not the active one: user entities come from the console session.");
        }

        if (status.Pairing is { } pairing)
        {
            lines.Add($"Pairing code: {pairing.Display} (requested by {pairing.Client})");
        }

        _summary.Text = string.Join(Environment.NewLine, lines);
        _fingerprint.Text = service?.GetString("fingerprint") ?? string.Empty;

        var connectedNames = connected.Select(c => c.GetString("name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = _clients.SelectedItems.Count > 0 ? _clients.SelectedItems[0].Tag as string : null;
        _clients.BeginUpdate();
        _clients.Items.Clear();
        foreach (var client in service?.GetArray("paired_clients")?.OfType<JsonObject>() ?? [])
        {
            var name = client.GetString("name") ?? "Home Assistant";
            var lastSeen = DateTimeOffset.TryParse(client.GetString("last_seen"), out var seen) ? seen.ToLocalTime().ToString("g") : "never";
            var item = new ListViewItem([name, connectedNames.Contains(name) ? "connected" : "offline", client.GetString("last_address") ?? "-", lastSeen])
            {
                Tag = client.GetString("id"),
            };
            _clients.Items.Add(item);
            item.Selected = item.Tag as string == selected;
        }

        _clients.EndUpdate();
        _remove.Enabled = _clients.SelectedItems.Count > 0;

        var details = new List<string>();
        if (_host.Module<DiscordModule>() is { } discord)
        {
            details.Add($"Discord: {DiscordSetupForm.Describe(discord.Status)}");
        }

        if (_host.Module<ClaudeModule>() is { } claude)
        {
            details.Add($"Claude Code hooks: {(claude.HooksInstalled ? "installed" : "not installed")}");
        }

        if (_host.Module<ActivityModule>() is { } activity && activity.PrivacyMode)
        {
            details.Add("Privacy mode is on.");
        }

        _details.Text = string.Join(Environment.NewLine, details);
    }

    private async Task RefreshAsync()
    {
        var result = await _host.RequestAsync("status");
        if (result.Success && result.Data is JsonObject data)
        {
            UpdateStatus(_host.Status with { ServiceStatus = data });
        }
        else
        {
            UpdateStatus(_host.Status);
        }
    }

    private async Task RemoveSelectedAsync()
    {
        if (_clients.SelectedItems.Count == 0 || _clients.SelectedItems[0].Tag is not string id)
        {
            return;
        }

        var name = _clients.SelectedItems[0].Text;
        var answer = MessageBox.Show(this,
            $"Remove the pairing with \"{name}\"?\n\nHome Assistant will lose access and has to be paired again.",
            "PDC HomeConnect", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        var result = await _host.RequestAsync("unpair", new JsonObject { ["id"] = id });
        if (!result.Success)
        {
            MessageBox.Show(this, result.ErrorMessage ?? "Failed.", "PDC HomeConnect", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        await RefreshAsync();
    }
}
