using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Plex Media Server: streams, transcodes, bandwidth, library scans and sizes.</summary>
public sealed class PlexProvider : EntityProvider
{
    private const string PlexKey = @"Software\Plex, Inc.\Plex Media Server";
    private readonly ServiceConfig _config;
    private readonly HttpClient _http;
    private readonly Dictionary<string, (string State, JsonObject Data)> _sessions = new(StringComparer.Ordinal);
    private Dictionary<string, (string Key, string Title, string Type)> _libraries = new(StringComparer.Ordinal);
    private string? _token;
    private string _librarySignature = string.Empty;
    private DateTimeOffset _lastLibraryUpdate = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTokenRead = DateTimeOffset.MinValue;
    private bool _firstSessionPoll = true;

    public PlexProvider(EntityHub hub, ServiceConfig config, ILogger<PlexProvider> logger)
        : base("plex", hub, logger)
    {
        _config = config;
        _http = new HttpClient(new SocketsHttpHandler
        {
            // Plex on localhost uses a plex.direct certificate; only relax validation for loopback.
            SslOptions = { RemoteCertificateValidationCallback = (sender, cert, chain, errors) => errors == System.Net.Security.SslPolicyErrors.None || IsLoopback() },
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("X-Plex-Product", "PDC HomeConnect");
        _http.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "pdc-homeconnect");
    }

    private bool IsLoopback() =>
        Uri.TryCreate(_config.Plex.Url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "127.0.0.1");

    private string BaseUrl => _config.Plex.Url.TrimEnd('/');

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        var installed = UserHivesWithPlex().Any() || await IdentityAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (!ModuleState.IsOn(_config.Modules.Plex, installed))
        {
            return;
        }

        Logger.LogInformation("Plex module enabled ({Url})", BaseUrl);
        PublishDescriptors();
        await RunPeriodicAsync(TimeSpan.FromSeconds(5), PollAsync, cancellationToken).ConfigureAwait(false);
    }

    private void PublishDescriptors()
    {
        var list = new List<EntityDescriptor>
        {
            new() { Key = "plex_online", Platform = Platforms.BinarySensor, TranslationKey = "plex_online", Name = "Plex Media Server", DeviceClass = "running" },
            new() { Key = "plex_streams", Platform = Platforms.Sensor, TranslationKey = "plex_streams", Name = "Plex streams", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "plex_transcodes", Platform = Platforms.Sensor, TranslationKey = "plex_transcodes", Name = "Plex transcodes", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "plex_bandwidth", Platform = Platforms.Sensor, TranslationKey = "plex_bandwidth", Name = "Plex bandwidth", DeviceClass = "data_rate", Unit = "Mbit/s", StateClass = Ha.Measurement, Precision = 1 },
            new() { Key = "plex_scanning", Platform = Platforms.BinarySensor, TranslationKey = "plex_scanning", Name = "Plex library scan" },
            new() { Key = "plex_scan", Platform = Platforms.Button, TranslationKey = "plex_scan", Name = "Scan Plex libraries" },
            new() { Key = "plex_playback", Platform = Platforms.Event, TranslationKey = "plex_playback", Name = "Plex playback", EventTypes = ["started", "stopped", "paused", "resumed"] },
        };

        foreach (var (key, library) in _libraries)
        {
            list.Add(new EntityDescriptor
            {
                Key = key,
                Platform = Platforms.Sensor,
                TranslationKey = "plex_library",
                Placeholders = new() { ["library"] = library.Title },
                Name = $"Plex {library.Title}",
                StateClass = Ha.Measurement,
                Precision = 0,
                Icon = library.Type switch { "movie" => "mdi:movie", "show" => "mdi:television-classic", "artist" => "mdi:music", "photo" => "mdi:image-multiple", _ => "mdi:folder" },
            });
        }

        Publish(list);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        RefreshToken();
        var identity = await IdentityAsync(cancellationToken).ConfigureAwait(false);
        Set("plex_online", identity is not null, Attrs(("version", identity?.GetString("version")), ("url", BaseUrl)));
        if (identity is null)
        {
            foreach (var key in new[] { "plex_streams", "plex_transcodes", "plex_bandwidth", "plex_scanning", "plex_scan" }.Concat(_libraries.Keys))
            {
                Hub.SetUnavailable(key);
            }
            return;
        }

        Set("plex_scan", (JsonNode?)null);
        Set("plex_playback", (JsonNode?)null);
        await UpdateSessionsAsync(cancellationToken).ConfigureAwait(false);
        await UpdateActivitiesAsync(cancellationToken).ConfigureAwait(false);
        if (DateTimeOffset.UtcNow - _lastLibraryUpdate > TimeSpan.FromMinutes(5))
        {
            await UpdateLibrariesAsync(cancellationToken).ConfigureAwait(false);
            _lastLibraryUpdate = DateTimeOffset.UtcNow;
        }
    }

    private async Task UpdateSessionsAsync(CancellationToken cancellationToken)
    {
        var container = await GetAsync("/status/sessions", cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            Hub.SetUnavailable("plex_streams");
            return;
        }

        var items = container.GetArray("Metadata")?.OfType<JsonObject>().ToList() ?? [];
        var sessions = new JsonArray();
        var transcodes = 0;
        double bandwidthKbps = 0;
        var current = new Dictionary<string, (string State, JsonObject Data)>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var player = item.GetObject("Player");
            var session = item.GetObject("Session");
            var transcode = item["TranscodeSession"] as JsonObject;
            var decision = transcode?.GetString("videoDecision") ?? transcode?.GetString("audioDecision") ?? "direct play";
            if (decision == "transcode")
            {
                transcodes++;
            }

            bandwidthKbps += session.GetDouble("bandwidth") ?? 0;
            var title = item.GetString("grandparentTitle") is { Length: > 0 } show
                ? $"{show} - {item.GetString("title")}"
                : item.GetString("title");
            var duration = item.GetDouble("duration") ?? 0;
            var offset = item.GetDouble("viewOffset") ?? 0;
            var data = new JsonObject
            {
                ["user"] = item.GetObject("User").GetString("title"),
                ["title"] = title,
                ["type"] = item.GetString("type"),
                ["player"] = player.GetString("title") ?? player.GetString("product"),
                ["state"] = player.GetString("state"),
                ["decision"] = decision,
                ["location"] = session.GetString("location"),
                ["progress"] = duration > 0 ? Math.Round(offset * 100 / duration) : null,
            };
            sessions.Add(data.DeepClone());
            var id = session.GetString("id") ?? item.GetString("sessionKey") ?? Guid.NewGuid().ToString();
            current[id] = (player.GetString("state") ?? "playing", data);
        }

        Set("plex_streams", (long)items.Count, new JsonObject { ["sessions"] = sessions });
        Set("plex_transcodes", (long)transcodes);
        Set("plex_bandwidth", bandwidthKbps / 1000.0, 1);

        if (!_firstSessionPoll)
        {
            foreach (var (id, value) in current)
            {
                if (!_sessions.TryGetValue(id, out var previous))
                {
                    Hub.FireEvent("plex_playback", "started", value.Data);
                }
                else if (previous.State != value.State)
                {
                    if (value.State == "paused")
                    {
                        Hub.FireEvent("plex_playback", "paused", value.Data);
                    }
                    else if (previous.State == "paused" && value.State == "playing")
                    {
                        Hub.FireEvent("plex_playback", "resumed", value.Data);
                    }
                }
            }

            foreach (var (id, previous) in _sessions)
            {
                if (!current.ContainsKey(id))
                {
                    Hub.FireEvent("plex_playback", "stopped", previous.Data);
                }
            }
        }

        _firstSessionPoll = false;
        _sessions.Clear();
        foreach (var (id, value) in current)
        {
            _sessions[id] = value;
        }
    }

    private async Task UpdateActivitiesAsync(CancellationToken cancellationToken)
    {
        var container = await GetAsync("/activities", cancellationToken).ConfigureAwait(false);
        var activities = container?.GetArray("Activity")?.OfType<JsonObject>().ToList() ?? [];
        var scans = activities.Where(a => (a.GetString("type") ?? string.Empty).StartsWith("library.", StringComparison.OrdinalIgnoreCase)).ToList();
        Set("plex_scanning", scans.Count > 0, new JsonObject
        {
            ["activities"] = new JsonArray(scans.Select(a => (JsonNode)new JsonObject
            {
                ["title"] = a.GetString("title"),
                ["subtitle"] = a.GetString("subtitle"),
                ["progress"] = a.GetDouble("progress"),
            }).ToArray()),
        });
    }

    private async Task UpdateLibrariesAsync(CancellationToken cancellationToken)
    {
        var container = await GetAsync("/library/sections", cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return;
        }

        var libraries = new Dictionary<string, (string Key, string Title, string Type)>(StringComparer.Ordinal);
        foreach (var directory in container.GetArray("Directory")?.OfType<JsonObject>() ?? [])
        {
            var sectionKey = directory.GetString("key");
            var title = directory.GetString("title");
            if (sectionKey is null || title is null)
            {
                continue;
            }

            libraries[Slug.Key("plex_library", title)] = (sectionKey, title, directory.GetString("type") ?? "other");
        }

        _libraries = libraries;
        var signature = string.Join('|', libraries.Keys.Order());
        if (signature != _librarySignature)
        {
            PublishDescriptors();
            _librarySignature = signature;
        }

        foreach (var (key, library) in libraries)
        {
            var section = await GetAsync($"/library/sections/{library.Key}/all?X-Plex-Container-Start=0&X-Plex-Container-Size=0", cancellationToken).ConfigureAwait(false);
            var total = section?.GetLong("totalSize") ?? section?.GetLong("size");
            Set(key, total, Attrs(("type", library.Type), ("section", library.Key)));
        }
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (command.Key != "plex_scan")
        {
            return CommandResult.NotSupported(command.Action);
        }

        var result = await GetAsync("/library/sections/all/refresh", cancellationToken, expectJson: false).ConfigureAwait(false);
        return result is null ? CommandResult.Fail(ErrorCodes.Failed, "Plex did not accept the scan request.") : CommandResult.Ok();
    }

    private async Task<JsonObject?> IdentityAsync(CancellationToken cancellationToken) =>
        await GetAsync("/identity", cancellationToken, withToken: false).ConfigureAwait(false);

    private async Task<JsonObject?> GetAsync(string path, CancellationToken cancellationToken, bool withToken = true, bool expectJson = true)
    {
        try
        {
            var separator = path.Contains('?') ? '&' : '?';
            var url = BaseUrl + path + (withToken && !string.IsNullOrEmpty(_token) ? $"{separator}X-Plex-Token={Uri.EscapeDataString(_token)}" : string.Empty);
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (!expectJson)
            {
                return new JsonObject();
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (Json.ParseObject(text)?["MediaContainer"] as JsonObject) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private void RefreshToken()
    {
        if (!string.IsNullOrWhiteSpace(_config.Plex.Token))
        {
            _token = _config.Plex.Token.Trim();
            return;
        }

        if (_token is not null && DateTimeOffset.UtcNow - _lastTokenRead < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastTokenRead = DateTimeOffset.UtcNow;
        foreach (var hive in UserHivesWithPlex())
        {
            try
            {
                using var key = Registry.Users.OpenSubKey($@"{hive}\{PlexKey}");
                if (key?.GetValue("PlexOnlineToken") is string token && token.Length > 0)
                {
                    _token = token;
                    return;
                }
            }
            catch
            {
                // Hive not readable.
            }
        }
    }

    private static IEnumerable<string> UserHivesWithPlex()
    {
        string[] hives;
        try
        {
            hives = Registry.Users.GetSubKeyNames();
        }
        catch
        {
            yield break;
        }

        foreach (var hive in hives.Where(h => !h.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)))
        {
            RegistryKey? key = null;
            try
            {
                key = Registry.Users.OpenSubKey($@"{hive}\{PlexKey}");
            }
            catch
            {
                // Ignore unreadable hives.
            }

            if (key is not null)
            {
                key.Dispose();
                yield return hive;
            }
        }
    }
}
