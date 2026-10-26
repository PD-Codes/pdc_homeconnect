using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Media;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using Windows.Media.Control;
using Windows.Media.Playback;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>
/// Media player entity: "now playing" of the current Windows media session (Spotify, browsers, ...),
/// playback control, master volume/mute and audio playback (TTS announcements) on the PC.
/// </summary>
public sealed class MediaModule : EntityProvider, IDisposable
{
    private static readonly List<string> Features =
        ["play", "pause", "stop", "next_track", "previous_track", "volume_set", "volume_mute", "volume_step", "seek", "play_media"];

    private readonly AgentContext _context;
    private readonly AsyncSignal _changed = new();
    private readonly object _volumeGate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private AudioEndpoints? _endpoints;
    private EndpointVolume? _volume;
    private string? _coverKey;
    private double? _lastPosition;
    private DateTimeOffset _lastPositionAt;
    private string? _lastPositionKey;

    public MediaModule(AgentContext context, ILogger<MediaModule> logger)
        : base("media", context.Hub, logger)
    {
        _context = context;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new() { Key = "media", Platform = Platforms.MediaPlayer, TranslationKey = "media", Name = "Media", Features = Features },
        ]);

        try
        {
            _endpoints = new AudioEndpoints();
            _endpoints.Changed += () =>
            {
                ResetVolume();
                _changed.Set();
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Volume control is not available");
        }

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => AttachSession();
            _manager.SessionsChanged += (_, _) => AttachSession();
            AttachSession();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Windows media sessions are not available");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await UpdateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Media update failed");
            }

            // Event driven with a slow refresh for the playback position.
            await _changed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private void AttachSession()
    {
        var session = _manager?.GetCurrentSession();
        if (ReferenceEquals(session, _session))
        {
            _changed.Set();
            return;
        }

        if (_session is not null)
        {
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }

        _session = session;
        if (session is not null)
        {
            session.PlaybackInfoChanged += OnSessionChanged;
            session.MediaPropertiesChanged += OnSessionChanged;
            session.TimelinePropertiesChanged += OnSessionChanged;
        }

        _changed.Set();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _changed.Set();

    private EndpointVolume? Volume()
    {
        lock (_volumeGate)
        {
            if (_volume is null && _endpoints?.DefaultDevice(EDataFlow.Render) is { } device)
            {
                _volume = new EndpointVolume(device, _changed.Set);
            }

            return _volume;
        }
    }

    private void ResetVolume()
    {
        lock (_volumeGate)
        {
            _volume?.Dispose();
            _volume = null;
        }
    }

    private async Task UpdateAsync()
    {
        var attributes = new JsonObject();
        var volume = Volume();
        if (volume is not null)
        {
            attributes["volume_level"] = Math.Round(volume.Level, 2);
            attributes["is_volume_muted"] = volume.Muted;
        }

        var session = _session;
        string state;
        if (session is null)
        {
            state = "idle";
            await SetCoverAsync(null, null).ConfigureAwait(false);
        }
        else
        {
            var playback = session.GetPlaybackInfo();
            state = playback?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "buffering",
                _ => "idle",
            };

            attributes["source"] = SourceName(session.SourceAppUserModelId);
            attributes["app_id"] = session.SourceAppUserModelId;
            var privacy = _context.Signals.Privacy;
            if (!privacy)
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
                try
                {
                    properties = await session.TryGetMediaPropertiesAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Media properties not available");
                }

                if (properties is not null)
                {
                    Put(attributes, "media_title", properties.Title);
                    Put(attributes, "media_artist", properties.Artist);
                    Put(attributes, "media_album_name", properties.AlbumTitle);
                    Put(attributes, "media_album_artist", properties.AlbumArtist);
                    attributes["media_content_type"] = properties.PlaybackType switch
                    {
                        Windows.Media.MediaPlaybackType.Video => "video",
                        Windows.Media.MediaPlaybackType.Image => "image",
                        _ => "music",
                    };
                    await SetCoverAsync($"{session.SourceAppUserModelId}|{properties.Title}|{properties.Artist}|{properties.AlbumTitle}", properties).ConfigureAwait(false);
                }

                var timeline = session.GetTimelineProperties();
                if (timeline is not null && timeline.EndTime > TimeSpan.Zero)
                {
                    var position = Math.Round(Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds));
                    var updatedAt = timeline.LastUpdatedTime > DateTimeOffset.UnixEpoch ? timeline.LastUpdatedTime : DateTimeOffset.UtcNow;
                    var key = $"{state}|{attributes["media_title"]}";
                    if (_lastPosition is { } last && key == _lastPositionKey)
                    {
                        // Apps report the position often; only publish it when it jumps (seek), so Home Assistant
                        // does not get a state change every second. HA extrapolates the position while playing.
                        var expected = state == "playing" ? last + (updatedAt - _lastPositionAt).TotalSeconds : last;
                        if (Math.Abs(expected - position) < 3)
                        {
                            position = last;
                            updatedAt = _lastPositionAt;
                        }
                    }

                    _lastPosition = position;
                    _lastPositionAt = updatedAt;
                    _lastPositionKey = key;
                    attributes["media_duration"] = Math.Round((timeline.EndTime - timeline.StartTime).TotalSeconds);
                    attributes["media_position"] = position;
                    attributes["media_position_updated_at"] = updatedAt.ToUniversalTime().ToString("O");
                }
            }
            else
            {
                await SetCoverAsync(null, null).ConfigureAwait(false);
                attributes["privacy"] = true;
            }
        }

        if (Hub.GetBlob("media_cover") is { } cover)
        {
            attributes["media_image_hash"] = cover.Hash;
        }

        _context.Signals.MediaPlaying = state == "playing";
        Set("media", state, attributes);
    }

    private static void Put(JsonObject attributes, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes[key] = value;
        }
    }

    private async Task SetCoverAsync(string? key, GlobalSystemMediaTransportControlsSessionMediaProperties? properties)
    {
        if (key == _coverKey)
        {
            return;
        }

        _coverKey = key;
        byte[]? data = null;
        var contentType = "image/png";
        if (properties?.Thumbnail is { } thumbnail)
        {
            try
            {
                using var stream = await thumbnail.OpenReadAsync();
                if (stream.Size is > 0 and < 8 * 1024 * 1024)
                {
                    contentType = string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType;
                    using var input = stream.AsStreamForRead();
                    using var buffer = new MemoryStream();
                    await input.CopyToAsync(buffer).ConfigureAwait(false);
                    data = buffer.ToArray();
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Cover art not available");
                // Retry on the next update.
                _coverKey = null;
            }
        }

        await AgentHost.SetBlobAsync(_context, "media_cover", data, contentType).ConfigureAwait(false);
    }

    /// <summary>Friendly name for an app user model id ("Spotify.exe", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic").</summary>
    private static string SourceName(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return "Unknown";
        }

        var name = appId;
        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang];
            var underscore = name.IndexOf('_');
            if (underscore > 0)
            {
                name = name[..underscore];
            }

            name = name[(name.LastIndexOf('.') + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" or "308046b0af4a39cb" => "Firefox",
            "spotify" => "Spotify",
            "zunemusic" => "Media Player",
            "vlc" => "VLC",
            _ => name,
        };
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.Action)
            {
                case Actions.VolumeSet:
                    if (Volume() is not { } level)
                    {
                        return CommandResult.Fail(ErrorCodes.Unavailable, "No audio output device.");
                    }

                    level.Level = (float)(command.Params.GetDouble("volume") ?? 0);
                    break;
                case Actions.VolumeMute:
                    if (Volume() is not { } mute)
                    {
                        return CommandResult.Fail(ErrorCodes.Unavailable, "No audio output device.");
                    }

                    mute.Muted = command.Params.GetBool("mute") ?? true;
                    break;
                case Actions.VolumeUp:
                    Volume()?.StepUp();
                    break;
                case Actions.VolumeDown:
                    Volume()?.StepDown();
                    break;
                case Actions.PlayMedia:
                    return PlayMedia(command.Params);
                default:
                    return await SessionCommandAsync(command).ConfigureAwait(false);
            }

            _changed.Set();
            return CommandResult.Ok();
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ErrorCodes.Failed, ex.Message);
        }
    }

    private async Task<CommandResult> SessionCommandAsync(EntityCommand command)
    {
        var session = _session;
        if (session is null)
        {
            return CommandResult.Fail(ErrorCodes.Unavailable, "Nothing is playing.");
        }

        var ok = command.Action switch
        {
            Actions.MediaPlay => await session.TryPlayAsync(),
            Actions.MediaPause => await session.TryPauseAsync(),
            Actions.MediaPlayPause => await session.TryTogglePlayPauseAsync(),
            Actions.MediaStop => await session.TryStopAsync(),
            Actions.MediaNext => await session.TrySkipNextAsync(),
            Actions.MediaPrevious => await session.TrySkipPreviousAsync(),
            Actions.MediaSeek => await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(command.Params.GetDouble("position") ?? 0).Ticks),
            _ => (bool?)null,
        };

        _changed.Set();
        return ok switch
        {
            null => CommandResult.NotSupported(command.Action),
            true => CommandResult.Ok(),
            false => CommandResult.Fail(ErrorCodes.Failed, "The app did not accept the command."),
        };
    }

    private CommandResult PlayMedia(JsonObject parameters)
    {
        var category = parameters.GetBool("announce") == true ? MediaPlayerAudioCategory.Alerts : MediaPlayerAudioCategory.Media;
        if (parameters.GetString("data") is { } data)
        {
            AudioPlayback.Play(Convert.FromBase64String(data), parameters.GetString("content_type") ?? "audio/mpeg", category);
            return CommandResult.Ok();
        }

        if (parameters.GetString("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            AudioPlayback.Play(uri, category);
            return CommandResult.Ok();
        }

        return CommandResult.Fail(ErrorCodes.InvalidParams, "play_media needs inline audio data or an http(s) URL.");
    }

    public void Dispose()
    {
        ResetVolume();
        _endpoints?.Dispose();
        _changed.Set();
    }
}
