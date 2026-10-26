using System.Diagnostics.Eventing.Reader;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Errors in the Windows event logs (24 h) and a live event entity for critical events.</summary>
public sealed class EventLogProvider : EntityProvider
{
    private readonly ServiceConfig _config;
    private readonly DeviceInfoService _device;
    private readonly List<EventLogWatcher> _watchers = [];

    public EventLogProvider(EntityHub hub, ServiceConfig config, DeviceInfoService device, ILogger<EventLogProvider> logger)
        : base("eventlog", hub, logger)
    {
        _config = config;
        _device = device;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.EventLog, _device.IsServer))
        {
            return;
        }

        Publish(
        [
            new() { Key = "event_log_errors", Platform = Platforms.Sensor, TranslationKey = "event_log_errors", Name = "Event log errors (24 h)", StateClass = Ha.Measurement, Precision = 0, EntityCategory = Ha.Diagnostic },
            new() { Key = "system_event", Platform = Platforms.Event, TranslationKey = "system_event", Name = "System event", EventTypes = ["critical", "error"] },
        ]);
        Set("system_event", (JsonNode?)null);

        StartWatchers();
        try
        {
            await RunPeriodicAsync(TimeSpan.FromMinutes(5), _ =>
            {
                UpdateCounts();
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var watcher in _watchers)
            {
                watcher.Enabled = false;
                watcher.Dispose();
            }
        }
    }

    private void StartWatchers()
    {
        var levels = _config.EventLog.EventsForErrors ? "Level=1 or Level=2" : "Level=1";
        foreach (var log in _config.EventLog.Logs)
        {
            try
            {
                var query = new EventLogQuery(log, PathType.LogName, $"*[System[({levels})]]");
                var watcher = new EventLogWatcher(query);
                watcher.EventRecordWritten += (_, args) =>
                {
                    using var record = args.EventRecord;
                    if (record is null)
                    {
                        return;
                    }

                    var type = record.Level == 1 ? "critical" : "error";
                    Hub.FireEvent("system_event", type, Describe(record, log));
                };
                watcher.Enabled = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Logger.LogInformation("Cannot watch event log {Log}: {Message}", log, ex.Message);
            }
        }
    }

    private void UpdateCounts()
    {
        var total = 0;
        var critical = 0;
        var latest = new List<(DateTime Time, JsonObject Data)>();
        foreach (var log in _config.EventLog.Logs)
        {
            try
            {
                var query = new EventLogQuery(log, PathType.LogName,
                    "*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 86400000]]]")
                {
                    ReverseDirection = true,
                };
                using var reader = new EventLogReader(query);
                var read = 0;
                for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        total++;
                        if (record.Level == 1)
                        {
                            critical++;
                        }

                        if (read++ < 5)
                        {
                            latest.Add((record.TimeCreated ?? DateTime.MinValue, Describe(record, log)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Cannot read event log {Log}", log);
            }
        }

        var recent = new JsonArray(latest.OrderByDescending(l => l.Time).Take(5).Select(l => (JsonNode)l.Data).ToArray());
        Set("event_log_errors", (long)total, new JsonObject { ["critical"] = critical, ["errors"] = total - critical, ["latest"] = recent });
    }

    private static JsonObject Describe(EventRecord record, string log)
    {
        string? message = null;
        try
        {
            message = record.FormatDescription();
        }
        catch
        {
            // Some providers have no message resources.
        }

        if (message is { Length: > 300 })
        {
            message = message[..300] + "...";
        }

        return new JsonObject
        {
            ["log"] = log,
            ["source"] = record.ProviderName,
            ["event_id"] = record.Id,
            ["time"] = record.TimeCreated?.ToUniversalTime().ToString("O"),
            ["message"] = message,
        };
    }
}
