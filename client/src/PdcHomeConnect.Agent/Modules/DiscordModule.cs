using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Discord;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>
/// Discord voice status. Without setup: heuristic (Discord uses the microphone = in a voice channel).
/// With an own Discord application (RPC): channel, members, mute/deafen switches, leave button, speaking.
/// </summary>
public sealed class DiscordModule : EntityProvider, IDisposable
{
    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write"];
    private static readonly string[] DiscordProcesses = ["Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private sealed record Member(string Id, string Name, bool Muted, bool Deafened);

    private readonly AgentContext _context;
    private readonly AsyncSignal _wake = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Member> _members = [];
    private readonly ConcurrentDictionary<string, string> _guildNames = new();
    private DiscordRpcClient? _rpc;
    private CancellationToken _token;
    private bool _authorizeRequested;
    private TaskCompletionSource<string?>? _authorizeCompletion;
    private string? _selfId;
    private string? _channelId;
    private string? _channelName;
    private string? _guildId;
    private bool _mute;
    private bool _deaf;
    private bool _speaking;
    private bool _rpcPublished;
    private string _status = "not_configured";

    public DiscordModule(AgentContext context, ILogger<DiscordModule> logger)
        : base("discord", context.Hub, logger)
    {
        _context = context;
    }

    /// <summary>not_configured, not_running, connecting, needs_authorization, connected, error.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                try
                {
                    StatusChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Status listener failed");
                }
            }
        }
    }

    public event Action? StatusChanged;

    public string? ClientId => !string.IsNullOrWhiteSpace(_context.Config.Discord.ClientId)
        ? _context.Config.Discord.ClientId.Trim()
        : _context.State.DiscordClientId;

    private string? ClientSecret => AgentState.Unprotect(_context.State.DiscordClientSecret);

    private bool Configured => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

    private bool Connected => _rpc is not null && Status == "connected";

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        _token = cancellationToken;
        PublishDescriptors();
        _context.Signals.Changed += OnSignalsChanged;
        UpdateHeuristic();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Configured)
            {
                Status = "not_configured";
                await _wake.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!DiscordRunning())
            {
                Status = "not_running";
                if (_authorizeRequested)
                {
                    _authorizeRequested = false;
                    CompleteSetup("Discord is not running. Start Discord and try again.");
                }

                await _wake.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var delay = TimeSpan.FromSeconds(15);
            try
            {
                Status = "connecting";
                await using var rpc = await DiscordRpcClient.ConnectAsync(ClientId!, cancellationToken).ConfigureAwait(false);
                if (rpc is null)
                {
                    Status = "not_running";
                    delay = TimeSpan.FromSeconds(5);
                    if (_authorizeRequested)
                    {
                        _authorizeRequested = false;
                        CompleteSetup("Could not connect to Discord. Is the Discord desktop app running?");
                    }
                }
                else if (await AuthenticateAsync(rpc, cancellationToken).ConfigureAwait(false))
                {
                    await RunSessionAsync(rpc, cancellationToken).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(3);
                }
                else
                {
                    Status = "needs_authorization";
                    delay = TimeSpan.FromMinutes(10);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (DiscordRpcException ex)
            {
                Logger.LogWarning("Discord RPC error {Code}: {Message}", ex.Code, ex.Message);
                Status = "error";
                delay = TimeSpan.FromMinutes(ex.Code == 4000 ? 5 : 1);
                if (_authorizeRequested)
                {
                    _authorizeRequested = false;
                    CompleteSetup(ex.Code == 4000 ? "Discord rejected the application id." : $"Discord: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Discord RPC session ended");
                if (Status != "needs_authorization")
                {
                    Status = DiscordRunning() ? "error" : "not_running";
                }
            }
            finally
            {
                lock (_gate)
                {
                    _rpc = null;
                    ResetVoice();
                }

                PublishState();
            }

            await _wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void CompleteSetup(string? error)
    {
        var completion = Interlocked.Exchange(ref _authorizeCompletion, null);
        completion?.TrySetResult(error);
    }

    // ------------------------------------------------------------------ setup (tray dialog)

    /// <summary>Saves the application credentials and starts the authorization (Discord shows a dialog).</summary>
    public async Task<string?> SetupAsync(string clientId, string clientSecret)
    {
        _context.State.DiscordClientId = clientId.Trim();
        _context.State.DiscordClientSecret = AgentState.Protect(clientSecret.Trim());
        _context.State.DiscordAccessToken = null;
        _context.State.DiscordRefreshToken = null;
        _context.State.DiscordTokenExpires = null;
        _context.State.Save();

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _authorizeCompletion = completion;
        _authorizeRequested = true;
        var rpc = _rpc;
        if (rpc is not null)
        {
            // Reconnect with the new credentials.
            await rpc.DisposeAsync().ConfigureAwait(false);
        }

        _wake.Set();
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "Timed out waiting for Discord.";
        }
    }

    /// <summary>Removes the Discord credentials.</summary>
    public async Task ResetAsync()
    {
        _context.State.DiscordClientId = null;
        _context.State.DiscordClientSecret = null;
        _context.State.DiscordAccessToken = null;
        _context.State.DiscordRefreshToken = null;
        _context.State.DiscordTokenExpires = null;
        _context.State.Save();
        var rpc = _rpc;
        if (rpc is not null)
        {
            await rpc.DisposeAsync().ConfigureAwait(false);
        }

        PublishDescriptors();
        _wake.Set();
    }

    // ------------------------------------------------------------------ authentication

    private async Task<bool> AuthenticateAsync(DiscordRpcClient rpc, CancellationToken cancellationToken)
    {
        var state = _context.State;
        var accessToken = AgentState.Unprotect(state.DiscordAccessToken);
        var refreshToken = AgentState.Unprotect(state.DiscordRefreshToken);

        if (accessToken is not null && state.DiscordTokenExpires is { } expires && expires < DateTimeOffset.UtcNow.AddHours(1) && refreshToken is not null)
        {
            accessToken = await ExchangeTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, cancellationToken).ConfigureAwait(false);
        }

        if (accessToken is not null && await TryAuthenticateAsync(rpc, accessToken, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (refreshToken is not null)
        {
            accessToken = await ExchangeTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, cancellationToken).ConfigureAwait(false);
            if (accessToken is not null && await TryAuthenticateAsync(rpc, accessToken, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        if (!_authorizeRequested)
        {
            // Never pop up the Discord authorization dialog on its own: the user starts it from the tray.
            return false;
        }

        _authorizeRequested = false;
        Status = "needs_authorization";
        Logger.LogInformation("Requesting Discord authorization");
        try
        {
            var response = await rpc.SendAsync("AUTHORIZE", new JsonObject
            {
                ["client_id"] = ClientId,
                ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            }, timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);
            var code = (response as JsonObject)?.GetString("code");
            if (code is null)
            {
                CompleteSetup("Discord did not return an authorization code.");
                return false;
            }

            var parameters = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code };
            if (!string.IsNullOrWhiteSpace(_context.Config.Discord.RedirectUri))
            {
                parameters["redirect_uri"] = _context.Config.Discord.RedirectUri;
            }

            accessToken = await ExchangeTokenAsync(parameters, cancellationToken).ConfigureAwait(false);
            if (accessToken is null)
            {
                CompleteSetup("The token exchange failed. Check the client secret and the redirect URI (see docs/DISCORD.md).");
                return false;
            }

            if (!await TryAuthenticateAsync(rpc, accessToken, cancellationToken).ConfigureAwait(false))
            {
                CompleteSetup("Discord rejected the access token.");
                return false;
            }

            CompleteSetup(null);
            return true;
        }
        catch (DiscordRpcException ex)
        {
            Logger.LogWarning("Discord authorization failed: {Message}", ex.Message);
            CompleteSetup($"Discord: {ex.Message}");
            return false;
        }
        catch (TimeoutException)
        {
            CompleteSetup("The authorization was not confirmed in Discord.");
            return false;
        }
    }

    private async Task<bool> TryAuthenticateAsync(DiscordRpcClient rpc, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var response = await rpc.SendAsync("AUTHENTICATE", new JsonObject { ["access_token"] = accessToken }, cancellationToken: cancellationToken).ConfigureAwait(false);
            _selfId = (response as JsonObject)?.GetObject("user").GetString("id");
            return true;
        }
        catch (DiscordRpcException ex)
        {
            Logger.LogDebug("Discord AUTHENTICATE failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>OAuth2 token request. Stores the new tokens and returns the access token.</summary>
    private async Task<string?> ExchangeTokenAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        parameters["client_id"] = ClientId!;
        parameters["client_secret"] = ClientSecret ?? string.Empty;
        try
        {
            using var response = await Http.PostAsync("https://discord.com/api/v10/oauth2/token", new FormUrlEncodedContent(parameters), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Logger.LogWarning("Discord token request failed ({Status}): {Body}", (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
                if ((int)response.StatusCode is 400 or 401)
                {
                    // Invalid refresh token: forget it.
                    _context.State.DiscordAccessToken = null;
                    _context.State.DiscordRefreshToken = null;
                    _context.State.Save();
                }
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
            var accessToken = json?.GetString("access_token");
            if (json is null || accessToken is null)
            {
                return null;
            }

            _context.State.DiscordAccessToken = AgentState.Protect(accessToken);
            _context.State.DiscordRefreshToken = AgentState.Protect(json.GetString("refresh_token")) ?? _context.State.DiscordRefreshToken;
            _context.State.DiscordTokenExpires = DateTimeOffset.UtcNow.AddSeconds(json.GetLong("expires_in") ?? 604800);
            _context.State.Save();
            return accessToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.LogWarning("Discord token request failed: {Message}", ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ session

    private async Task RunSessionAsync(DiscordRpcClient rpc, CancellationToken cancellationToken)
    {
        rpc.EventReceived += (evt, data) => _ = OnEventAsync(rpc, evt, data);
        foreach (var evt in new[] { "VOICE_CHANNEL_SELECT", "VOICE_SETTINGS_UPDATE" })
        {
            await rpc.SubscribeAsync(evt, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _rpc = rpc;
        }

        Status = "connected";
        Logger.LogInformation("Connected to Discord RPC");
        if (!_rpcPublished)
        {
            PublishDescriptors();
        }

        var settings = await rpc.SendAsync("GET_VOICE_SETTINGS", cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject;
        ApplySettings(settings);
        var channel = await rpc.SendAsync("GET_SELECTED_VOICE_CHANNEL", cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject;
        await JoinChannelAsync(rpc, channel, cancellationToken).ConfigureAwait(false);

        await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OnEventAsync(DiscordRpcClient rpc, string evt, JsonObject data)
    {
        try
        {
            switch (evt)
            {
                case "VOICE_SETTINGS_UPDATE":
                    ApplySettings(data);
                    break;

                case "VOICE_CHANNEL_SELECT":
                    var channelId = data.GetString("channel_id");
                    if (channelId == _channelId)
                    {
                        break;
                    }

                    JsonObject? channel = null;
                    if (channelId is not null)
                    {
                        channel = await rpc.SendAsync("GET_CHANNEL", new JsonObject { ["channel_id"] = channelId }, cancellationToken: _token).ConfigureAwait(false) as JsonObject;
                    }

                    await JoinChannelAsync(rpc, channel, _token).ConfigureAwait(false);
                    break;

                case "VOICE_STATE_CREATE":
                case "VOICE_STATE_UPDATE":
                    lock (_gate)
                    {
                        var member = ParseMember(data);
                        if (member is not null)
                        {
                            _members[member.Id] = member;
                        }
                    }
                    PublishState();
                    break;

                case "VOICE_STATE_DELETE":
                    lock (_gate)
                    {
                        var id = data.GetObject("user").GetString("id");
                        if (id is not null)
                        {
                            _members.Remove(id);
                        }
                    }
                    PublishState();
                    break;

                case "SPEAKING_START":
                case "SPEAKING_STOP":
                    if (data.GetString("user_id") == _selfId)
                    {
                        _speaking = evt == "SPEAKING_START";
                        PublishState();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Discord event {Event} failed", evt);
        }
    }

    private async Task JoinChannelAsync(DiscordRpcClient rpc, JsonObject? channel, CancellationToken cancellationToken)
    {
        string? previous;
        lock (_gate)
        {
            previous = _channelId;
            ResetVoice();
        }

        if (previous is not null)
        {
            await SetChannelSubscriptionsAsync(rpc, previous, subscribe: false, cancellationToken).ConfigureAwait(false);
        }

        if (channel is not null && channel.GetString("id") is { } id)
        {
            var guildId = channel.GetString("guild_id");
            lock (_gate)
            {
                _channelId = id;
                _channelName = channel.GetString("name");
                _guildId = guildId;
                foreach (var state in channel.GetArray("voice_states") ?? [])
                {
                    if (state is JsonObject obj && ParseMember(obj) is { } member)
                    {
                        _members[member.Id] = member;
                    }
                }
            }

            if (guildId is not null && !_guildNames.ContainsKey(guildId))
            {
                try
                {
                    var guild = await rpc.SendAsync("GET_GUILD", new JsonObject { ["guild_id"] = guildId }, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject;
                    _guildNames[guildId] = guild?.GetString("name") ?? string.Empty;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "GET_GUILD failed");
                }
            }

            await SetChannelSubscriptionsAsync(rpc, id, subscribe: true, cancellationToken).ConfigureAwait(false);
        }

        PublishState();
    }

    private static async Task SetChannelSubscriptionsAsync(DiscordRpcClient rpc, string channelId, bool subscribe, CancellationToken cancellationToken)
    {
        foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" })
        {
            try
            {
                var args = new JsonObject { ["channel_id"] = channelId };
                if (subscribe)
                {
                    await rpc.SubscribeAsync(evt, args, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await rpc.UnsubscribeAsync(evt, args, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DiscordRpcException)
            {
                // Not allowed for this channel type (e.g. stage) - keep going.
            }
        }
    }

    private static Member? ParseMember(JsonObject data)
    {
        var user = data.GetObject("user");
        var id = user.GetString("id");
        if (id is null)
        {
            return null;
        }

        var voice = data.GetObject("voice_state");
        var name = data.GetString("nick") ?? user.GetString("global_name") ?? user.GetString("username") ?? id;
        var muted = (voice.GetBool("self_mute") ?? false) || (voice.GetBool("mute") ?? false);
        var deafened = (voice.GetBool("self_deaf") ?? false) || (voice.GetBool("deaf") ?? false);
        return new Member(id, name, muted, deafened);
    }

    private void ApplySettings(JsonObject? settings)
    {
        if (settings is null)
        {
            return;
        }

        _mute = settings.GetBool("mute") ?? _mute;
        _deaf = settings.GetBool("deaf") ?? _deaf;
        PublishState();
    }

    private void ResetVoice()
    {
        _channelId = null;
        _channelName = null;
        _guildId = null;
        _speaking = false;
        _members.Clear();
    }

    // ------------------------------------------------------------------ entities

    private void PublishDescriptors()
    {
        var descriptors = new List<EntityDescriptor>
        {
            new() { Key = "discord_voice", Platform = Platforms.BinarySensor, TranslationKey = "discord_voice", Name = "Discord voice", OfflineState = JsonValue.Create(false) },
        };
        _rpcPublished = Configured;
        if (_rpcPublished)
        {
            descriptors.Add(new() { Key = "discord_voice_channel", Platform = Platforms.Sensor, TranslationKey = "discord_voice_channel", Name = "Discord voice channel" });
            descriptors.Add(new() { Key = "discord_mute", Platform = Platforms.Switch, TranslationKey = "discord_mute", Name = "Discord mute" });
            descriptors.Add(new() { Key = "discord_deafen", Platform = Platforms.Switch, TranslationKey = "discord_deafen", Name = "Discord deafen" });
            descriptors.Add(new() { Key = "discord_leave", Platform = Platforms.Button, TranslationKey = "discord_leave", Name = "Leave Discord voice" });
            descriptors.Add(new() { Key = "discord_speaking", Platform = Platforms.BinarySensor, TranslationKey = "discord_speaking", Name = "Speaking in Discord", EnabledByDefault = false, OfflineState = JsonValue.Create(false) });
        }

        Publish(descriptors);
        PublishState();
    }

    private void OnSignalsChanged()
    {
        if (!Connected)
        {
            UpdateHeuristic();
        }
    }

    private void UpdateHeuristic() => PublishState();

    private void PublishState()
    {
        if (Connected)
        {
            List<Member> members;
            string? channelName;
            string? guildName;
            string? channelId;
            string? guildId;
            lock (_gate)
            {
                members = _members.Values.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                channelName = _channelName;
                channelId = _channelId;
                guildId = _guildId;
                guildName = guildId is not null ? _guildNames.GetValueOrDefault(guildId) : null;
            }

            var inVoice = channelId is not null;
            _context.Signals.DiscordVoice = inVoice;
            var privacy = _context.Signals.Privacy;
            var attributes = inVoice && !privacy
                ? Attrs(
                    ("channel", channelName),
                    ("server", string.IsNullOrEmpty(guildName) ? null : guildName),
                    ("channel_id", channelId),
                    ("guild_id", guildId),
                    ("member_count", (long)members.Count),
                    ("members", new JsonArray(members.Select(m => (JsonNode)JsonValue.Create(m.Name)!).ToArray())),
                    ("source", "rpc"))
                : Attrs(("source", "rpc"));
            Set("discord_voice", inVoice, attributes);
            Set("discord_voice_channel", inVoice ? (privacy ? "private" : channelName ?? "Private call") : null, attributes);
            Set("discord_mute", _mute);
            Set("discord_deafen", _deaf);
            Set("discord_leave", (JsonNode?)null);
            Set("discord_speaking", _speaking);
            return;
        }

        // Heuristic: Discord keeps the microphone open while connected to a voice channel.
        var microphone = _context.Signals.MicrophoneApps;
        var heuristic = DiscordProcesses.Any(p => microphone.Contains(p, StringComparer.OrdinalIgnoreCase));
        _context.Signals.DiscordVoice = heuristic;
        Set("discord_voice", heuristic, Attrs(("source", "microphone")));
        if (_rpcPublished)
        {
            foreach (var key in new[] { "discord_voice_channel", "discord_mute", "discord_deafen", "discord_leave", "discord_speaking" })
            {
                Hub.SetUnavailable(key);
            }
        }
    }

    private bool DiscordRunning()
    {
        var running = _context.Signals.RunningProcesses;
        // The process list may not be populated yet right after start: then just try the pipe.
        return running.Count == 0 || DiscordProcesses.Any(running.Contains);
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        var rpc = _rpc;
        if (rpc is null || !Connected)
        {
            return CommandResult.Fail(ErrorCodes.Unavailable, "Discord is not connected.");
        }

        try
        {
            switch (command.Key, command.Action)
            {
                case ("discord_mute", Actions.TurnOn or Actions.TurnOff):
                    var mute = command.Action == Actions.TurnOn;
                    await rpc.SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = mute }, cancellationToken: cancellationToken).ConfigureAwait(false);
                    _mute = mute;
                    PublishState();
                    return CommandResult.Ok();

                case ("discord_deafen", Actions.TurnOn or Actions.TurnOff):
                    var deaf = command.Action == Actions.TurnOn;
                    await rpc.SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = deaf }, cancellationToken: cancellationToken).ConfigureAwait(false);
                    _deaf = deaf;
                    PublishState();
                    return CommandResult.Ok();

                case ("discord_leave", Actions.Press):
                    await rpc.SendAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null }, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return CommandResult.Ok();

                default:
                    return CommandResult.NotSupported(command.Action);
            }
        }
        catch (DiscordRpcException ex)
        {
            return CommandResult.Fail(ErrorCodes.Failed, $"Discord: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _context.Signals.Changed -= OnSignalsChanged;
        _wake.Set();
    }
}
