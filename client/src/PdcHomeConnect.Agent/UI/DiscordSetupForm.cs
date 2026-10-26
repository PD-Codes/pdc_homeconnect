using System.Diagnostics;
using System.Reflection;
using PdcHomeConnect.Agent.Modules;

namespace PdcHomeConnect.Agent.UI;

/// <summary>Setup of the optional Discord RPC connection (own Discord application, one time).</summary>
public sealed class DiscordSetupForm : Form
{
    private const string PortalUrl = "https://discord.com/developers/applications";
    private const string GuideUrl = "https://github.com/PD-Codes/pdc_homeconnect/blob/main/docs/DISCORD.md";

    private readonly DiscordModule _module;
    private readonly TextBox _clientId;
    private readonly TextBox _secret;
    private readonly Label _status;
    private readonly Button _connect;
    private readonly Button _reset;

    public DiscordSetupForm(DiscordModule module)
    {
        _module = module;
        Text = "Discord – PDC HomeConnect";
        Icon = LoadAppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Padding = new Padding(14),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            Margin = new Padding(0, 0, 0, 10),
            Text = "Without setup, PDC HomeConnect detects Discord voice calls from the microphone usage. " +
                   "For the channel name, members and mute/deafen control it needs your own (free) Discord application:" + Environment.NewLine + Environment.NewLine +
                   "1. Open the Discord Developer Portal and create a new application." + Environment.NewLine +
                   "2. Under OAuth2, add the redirect URL http://127.0.0.1 and save." + Environment.NewLine +
                   "3. Copy the Client ID and the Client Secret into the fields below." + Environment.NewLine +
                   "4. Click Connect and confirm the dialog that appears in Discord.",
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);

        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        links.Controls.Add(Link("Open Discord Developer Portal", PortalUrl));
        links.Controls.Add(Link("Step-by-step guide", GuideUrl));
        layout.Controls.Add(links, 0, 1);
        layout.SetColumnSpan(links, 2);

        layout.Controls.Add(new Label { Text = "Client ID:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _clientId = new TextBox { Dock = DockStyle.Fill, Text = module.ClientId ?? string.Empty };
        layout.Controls.Add(_clientId, 1, 2);

        layout.Controls.Add(new Label { Text = "Client secret:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _secret = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        layout.Controls.Add(_secret, 1, 3);

        _status = new Label { AutoSize = true, MaximumSize = new Size(470, 0), Margin = new Padding(0, 10, 0, 10) };
        layout.Controls.Add(_status, 0, 4);
        layout.SetColumnSpan(_status, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        _connect = new Button { Text = "Connect", AutoSize = true };
        _connect.Click += async (_, _) => await ConnectAsync();
        _reset = new Button { Text = "Remove", AutoSize = true };
        _reset.Click += async (_, _) => await ResetAsync();
        buttons.Controls.AddRange([close, _connect, _reset]);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = _connect;
        CancelButton = close;

        module.StatusChanged += OnModuleStatusChanged;
        FormClosed += (_, _) => module.StatusChanged -= OnModuleStatusChanged;
        UpdateStatus();
    }

    /// <summary>Human readable module status.</summary>
    public static string Describe(string status) => status switch
    {
        "not_configured" => "basic (microphone detection)",
        "not_running" => "Discord is not running",
        "connecting" => "connecting",
        "needs_authorization" => "authorization required",
        "connected" => "connected",
        "error" => "error (see logs)",
        _ => status,
    };

    private static LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return link;
    }

    private static Icon? LoadAppIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        return stream is null ? null : new Icon(stream);
    }

    private void OnModuleStatusChanged()
    {
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(UpdateStatus);
        }
    }

    private void UpdateStatus() => _status.Text = $"Status: {Describe(_module.Status)}";

    private async Task ConnectAsync()
    {
        var clientId = _clientId.Text.Trim();
        var secret = _secret.Text.Trim();
        if (clientId.Length == 0 || !clientId.All(char.IsDigit) || secret.Length == 0)
        {
            _status.Text = "Please enter the numeric Client ID and the Client Secret.";
            return;
        }

        _connect.Enabled = false;
        _reset.Enabled = false;
        _status.Text = "Waiting for Discord… please confirm the authorization dialog in the Discord app.";
        try
        {
            var error = await _module.SetupAsync(clientId, secret);
            _status.Text = error is null ? "Connected. Discord voice entities are now available in Home Assistant." : error;
            if (error is null)
            {
                _secret.Clear();
            }
        }
        finally
        {
            _connect.Enabled = true;
            _reset.Enabled = true;
        }
    }

    private async Task ResetAsync()
    {
        await _module.ResetAsync();
        _clientId.Clear();
        _secret.Clear();
        _status.Text = "Removed. Discord voice detection uses the microphone again.";
    }
}
