using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Util;

/// <summary>
/// Loads JSON config files that may contain comments (JSONC). Missing files are created from a template.
/// Config files are user-owned: the client never rewrites them (comments stay intact).
/// </summary>
public static class JsonConfig
{
    public static T Load<T>(string path, string defaultTemplate, ILogger logger)
        where T : new()
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, defaultTemplate);
                logger.LogInformation("Created default config {Path}", path);
            }

            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(text, Json.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            logger.LogError("Config {Path} is invalid ({Message}); using defaults until it is fixed", path, ex.Message);
            return TryParseTemplate<T>(defaultTemplate, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load config {Path}; using defaults", path);
            return TryParseTemplate<T>(defaultTemplate, logger);
        }
    }

    private static T TryParseTemplate<T>(string template, ILogger logger)
        where T : new()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(template, Json.Options) ?? new T();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Default config template is invalid");
            return new T();
        }
    }

    /// <summary>Watches a file and invokes the callback (debounced) when it changes.</summary>
    public static IDisposable Watch(string path, Action onChanged)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        Timer? timer = null;
        void Trigger(object? sender, FileSystemEventArgs e)
        {
            timer?.Dispose();
            timer = new Timer(_ => onChanged(), null, TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
        }

        watcher.Changed += Trigger;
        watcher.Created += Trigger;
        watcher.Renamed += (s, e) => Trigger(s, e);
        return new Disposer(() =>
        {
            watcher.Dispose();
            timer?.Dispose();
        });
    }

    private sealed class Disposer(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
