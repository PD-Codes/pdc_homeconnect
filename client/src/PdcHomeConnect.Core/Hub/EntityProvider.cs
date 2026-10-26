using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Hub;

/// <summary>
/// Base class for a group of related entities (e.g. "system stats", "Hyper-V").
/// Providers publish descriptors and states into the hub and handle commands for their entities.
/// </summary>
public abstract class EntityProvider : ICommandHandler
{
    protected EntityProvider(string id, EntityHub hub, ILogger logger)
    {
        Id = id;
        Hub = hub;
        Logger = logger;
    }

    public string Id { get; }

    protected EntityHub Hub { get; }

    protected ILogger Logger { get; }

    /// <summary>Main loop of the provider. Runs until cancelled.</summary>
    public abstract Task RunAsync(CancellationToken cancellationToken);

    public virtual Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.NotSupported(command.Action));

    protected void Publish(IEnumerable<EntityDescriptor> descriptors) => Hub.SetDescriptors(Id, descriptors);

    protected void Set(string key, JsonNode? state, JsonObject? attributes = null) => Hub.SetState(key, state, attributes);

    protected void Set(string key, string? state, JsonObject? attributes = null) =>
        Hub.SetState(key, state is null ? null : JsonValue.Create(state), attributes);

    protected void Set(string key, bool state, JsonObject? attributes = null) =>
        Hub.SetState(key, JsonValue.Create(state), attributes);

    protected void Set(string key, double? state, int digits, JsonObject? attributes = null) =>
        Hub.SetState(key, state is null || double.IsNaN(state.Value) || double.IsInfinity(state.Value)
            ? null
            : JsonValue.Create(Math.Round(state.Value, digits)), attributes);

    protected void Set(string key, long? state, JsonObject? attributes = null) =>
        Hub.SetState(key, state is null ? null : JsonValue.Create(state.Value), attributes);

    /// <summary>Runs <paramref name="tick"/> periodically, logging (but surviving) exceptions.</summary>
    protected async Task RunPeriodicAsync(Func<TimeSpan> interval, Func<CancellationToken, Task> tick, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await tick(cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                if (failures <= 3 || failures % 50 == 0)
                {
                    Logger.LogWarning(ex, "{Provider} update failed ({Count}x)", Id, failures);
                }
            }

            try
            {
                await Task.Delay(interval(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    protected Task RunPeriodicAsync(TimeSpan interval, Func<CancellationToken, Task> tick, CancellationToken cancellationToken) =>
        RunPeriodicAsync(() => interval, tick, cancellationToken);

    protected static JsonObject Attrs(params (string Key, object? Value)[] values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (value is not null)
            {
                obj[key] = ToNode(value);
            }
        }
        return obj;
    }

    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : null,
        float f => float.IsFinite(f) ? JsonValue.Create((double)f) : null,
        uint u => JsonValue.Create(u),
        ulong ul => JsonValue.Create(ul),
        DateTimeOffset dto => JsonValue.Create(dto.ToUniversalTime().ToString("O")),
        DateTime dt => JsonValue.Create(new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToString("O")),
        TimeSpan ts => JsonValue.Create(ts.TotalSeconds),
        _ => Json.ToNode(value),
    };

    /// <summary>ISO 8601 UTC timestamp for timestamp sensors.</summary>
    public static JsonNode Timestamp(DateTimeOffset value) => JsonValue.Create(value.ToUniversalTime().ToString("O"))!;
}

/// <summary>Starts all registered providers and wires them into the hub.</summary>
public sealed class ProviderHost : BackgroundService
{
    private readonly EntityHub _hub;
    private readonly IEnumerable<EntityProvider> _providers;
    private readonly ILogger _logger;

    public ProviderHost(EntityHub hub, IEnumerable<EntityProvider> providers, ILogger<ProviderHost> logger)
    {
        _hub = hub;
        _providers = providers;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        foreach (var provider in _providers)
        {
            _hub.RegisterCommandHandler(provider.Id, provider);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await provider.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Provider {Provider} stopped unexpectedly", provider.Id);
                }
            }, stoppingToken));
        }

        _logger.LogInformation("Started {Count} providers", tasks.Count);
        return Task.WhenAll(tasks);
    }
}
