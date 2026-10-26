using System.IO.Pipes;
using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Modules;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Ipc;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent;

/// <summary>Runs code on the UI thread.</summary>
public sealed class UiDispatcher
{
    private readonly Control _control;

    public UiDispatcher(Control control)
    {
        _control = control;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (!_control.InvokeRequired)
        {
            return Task.FromResult(func());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _control.BeginInvoke(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    public void Post(Action action)
    {
        if (_control.IsHandleCreated)
        {
            _control.BeginInvoke(action);
        }
    }
}

/// <summary>Everything modules need: hub, config, state, signals, UI thread, message window.</summary>
public sealed class AgentContext
{
    public required EntityHub Hub { get; init; }
    public required AgentConfig Config { get; init; }
    public required AgentState State { get; init; }
    public required AgentSignals Signals { get; init; }
    public required UiDispatcher Ui { get; init; }
    public required MessageWindow Window { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required Func<JsonObject, Task> SendToService { get; init; }
    public int SessionId { get; } = System.Diagnostics.Process.GetCurrentProcess().SessionId;
}

/// <summary>Connection status shown in the tray.</summary>
public sealed record AgentStatus(bool ServiceConnected, bool Primary, JsonObject? ServiceStatus, PairingInfo? Pairing);

public sealed record PairingInfo(string Code, string Display, string Client, string? Address, DateTimeOffset Expires);

/// <summary>
/// Owns the modules and the named pipe connection to the service. Modules publish into a local
/// <see cref="EntityHub"/>; changes are forwarded to the service, commands come back the same way.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly AgentContext _context;
    private readonly List<EntityProvider> _modules = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<CommandResult>> _requests = [];
    private JsonLineChannel? _channel;
    private long _nextRequestId;
    private bool _primary;
    private JsonObject? _serviceStatus;
    private PairingInfo? _pairing;

    public AgentHost(AgentContext context)
    {
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<AgentHost>();
    }

    public event Action<AgentStatus>? StatusChanged;

    public AgentContext Context => _context;

    public AgentStatus Status => new(_channel is not null, _primary, _serviceStatus, _pairing);

    public IReadOnlyList<EntityProvider> Modules => _modules;

    public T? Module<T>() where T : EntityProvider => _modules.OfType<T>().FirstOrDefault();

    public void Start()
    {
        var c = _context;
        var lf = c.LoggerFactory;
        _modules.Add(new SessionModule(c, lf.CreateLogger<SessionModule>()));
        _modules.Add(new AppsModule(c, lf.CreateLogger<AppsModule>()));
        _modules.Add(new PresenceModule(c, lf.CreateLogger<PresenceModule>()));
        _modules.Add(new GameModule(c, lf.CreateLogger<GameModule>()));
        _modules.Add(new DiscordModule(c, lf.CreateLogger<DiscordModule>()));
        if (c.Config.Features.Media)
        {
            _modules.Add(new MediaModule(c, lf.CreateLogger<MediaModule>()));
        }

        if (c.Config.Features.AudioDevices)
        {
            _modules.Add(new AudioDevicesModule(c, lf.CreateLogger<AudioDevicesModule>()));
        }

        _modules.Add(new NotificationModule(c, lf.CreateLogger<NotificationModule>()));
        _modules.Add(new InputModule(c, lf.CreateLogger<InputModule>()));
        _modules.Add(new DisplayModule(c, lf.CreateLogger<DisplayModule>()));
        if (c.Config.Features.ClaudeCode)
        {
            _modules.Add(new ClaudeModule(c, lf.CreateLogger<ClaudeModule>()));
        }

        _modules.Add(new CommandsModule(c, lf.CreateLogger<CommandsModule>()));
        _modules.Add(new ActivityModule(c, lf.CreateLogger<ActivityModule>()));

        foreach (var module in _modules)
        {
            c.Hub.RegisterCommandHandler(module.Id, module);
            _ = Task.Run(async () =>
            {
                try
                {
                    await module.RunAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Module {Module} stopped", module.Id);
                }
            });
        }

        _ = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        _ = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    /// <summary>Sends a request to the service (tray UI: status, unpair, ...).</summary>
    public async Task<CommandResult> RequestAsync(string method, JsonObject? parameters = null)
    {
        var channel = _channel;
        if (channel is null)
        {
            return CommandResult.Fail(ErrorCodes.Unavailable, "Not connected to the PDC HomeConnect service.");
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_requests)
        {
            _requests[id] = completion;
        }

        try
        {
            await SendAsync(new JsonObject { ["type"] = "request", ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] }).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CommandResult.Fail(ErrorCodes.Timeout, "The service did not answer.");
        }
        finally
        {
            lock (_requests)
            {
                _requests.Remove(id);
            }
        }
    }

    public Task SendAsync(JsonObject message) => SendInternalAsync(message);

    private async Task SendInternalAsync(JsonObject message)
    {
        var channel = _channel;
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.SendAsync(message, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Send to service failed");
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", IpcNames.ServicePipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
                var channel = new JsonLineChannel(pipe);
                await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Hello + full snapshot are sent atomically with respect to the flush loop.
                    _context.Hub.DrainChanges();
                    await channel.SendAsync(new JsonObject
                    {
                        ["type"] = "hello",
                        ["user"] = $"{Environment.UserDomainName}\\{Environment.UserName}",
                        ["session_id"] = _context.SessionId,
                        ["version"] = typeof(AgentHost).Assembly.GetName().Version?.ToString(3),
                    }, cancellationToken).ConfigureAwait(false);
                    await channel.SendAsync(_context.Hub.BuildEntitiesMessage(), cancellationToken).ConfigureAwait(false);
                    await channel.SendAsync(_context.Hub.BuildFullStatesMessage(), cancellationToken).ConfigureAwait(false);
                    _channel = channel;
                }
                finally
                {
                    _sendGate.Release();
                }

                _logger.LogInformation("Connected to the service");
                delay = TimeSpan.FromSeconds(1);
                RaiseStatus();
                await ResendBlobsAsync().ConfigureAwait(false);
                await ReadLoopAsync(channel, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (TimeoutException)
            {
                // Service not running yet.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Service connection failed");
            }
            finally
            {
                if (_channel is not null)
                {
                    _logger.LogInformation("Disconnected from the service");
                }

                _channel = null;
                _primary = false;
                await pipe.DisposeAsync().ConfigureAwait(false);
                RaiseStatus();
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
        }
    }

    private async Task ResendBlobsAsync()
    {
        if (_context.Hub.GetBlob("media_cover") is { } blob)
        {
            await SendAsync(new JsonObject
            {
                ["type"] = "blob",
                ["key"] = "media_cover",
                ["content_type"] = blob.ContentType,
                ["data"] = Convert.ToBase64String(blob.Data),
            }).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(JsonLineChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            try
            {
                HandleServiceMessage(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid message from the service");
            }
        }
    }

    private void HandleServiceMessage(JsonObject message)
    {
        switch (message.GetString("type"))
        {
            case "welcome":
            case "primary":
                _primary = message.GetBool("primary") ?? false;
                RaiseStatus();
                break;

            case "status":
                _serviceStatus = message;
                RaiseStatus();
                break;

            case "pairing_code":
                _pairing = new PairingInfo(
                    message.GetString("code") ?? string.Empty,
                    message.GetString("display") ?? message.GetString("code") ?? string.Empty,
                    message.GetString("client") ?? "Home Assistant",
                    message.GetString("address"),
                    DateTimeOffset.TryParse(message.GetString("expires"), out var expires) ? expires : DateTimeOffset.Now.AddMinutes(5));
                Module<NotificationModule>()?.ShowPairingCode(_pairing);
                RaiseStatus();
                break;

            case "pairing_cleared":
                _pairing = null;
                Module<NotificationModule>()?.ClearPairingCode();
                RaiseStatus();
                break;

            case "command":
                _ = RunCommandAsync(message);
                break;

            case "call":
                _ = RunCallAsync(message);
                break;

            case "result":
                var id = message.GetLong("id") ?? -1;
                TaskCompletionSource<CommandResult>? completion;
                lock (_requests)
                {
                    _requests.TryGetValue(id, out completion);
                }
                completion?.TrySetResult(CommandResult.FromJson(message));
                break;
        }
    }

    private async Task RunCommandAsync(JsonObject message)
    {
        var id = message.GetLong("id") ?? 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var command = new EntityCommand(
            message.GetString("key") ?? string.Empty,
            message.GetString("action") ?? string.Empty,
            message["params"] as JsonObject ?? []);
        var result = await _context.Hub.ExecuteCommandAsync(new EntityCommand(command.Key, command.Action, (JsonObject)command.Params.DeepClone()), timeout.Token).ConfigureAwait(false);
        await SendAsync(result.ToJson(id)).ConfigureAwait(false);
    }

    private async Task RunCallAsync(JsonObject message)
    {
        var id = message.GetLong("id") ?? 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var parameters = message["params"] as JsonObject ?? [];
        var result = await _context.Hub.CallMethodAsync(message.GetString("method") ?? string.Empty, (JsonObject)parameters.DeepClone(), timeout.Token).ConfigureAwait(false);
        await SendAsync(result.ToJson(id)).ConfigureAwait(false);
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _context.Hub.WaitForChangesAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var changes = _context.Hub.DrainChanges();
                    var channel = _channel;
                    if (channel is null || changes.IsEmpty)
                    {
                        continue;
                    }

                    if (changes.Entities is not null)
                    {
                        await channel.SendAsync(changes.Entities, cancellationToken).ConfigureAwait(false);
                    }

                    if (changes.States is not null)
                    {
                        await channel.SendAsync(changes.States, cancellationToken).ConfigureAwait(false);
                    }

                    foreach (var msg in changes.Messages)
                    {
                        await channel.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Flush failed");
            }
        }
    }

    /// <summary>Publishes a blob (e.g. cover art) locally and to the service.</summary>
    public static async Task SetBlobAsync(AgentContext context, string key, byte[]? data, string contentType)
    {
        if (data is null || data.Length == 0)
        {
            context.Hub.RemoveBlob(key);
            await context.SendToService(new JsonObject { ["type"] = "blob_remove", ["key"] = key }).ConfigureAwait(false);
            return;
        }

        context.Hub.SetBlob(key, data, contentType);
        await context.SendToService(new JsonObject
        {
            ["type"] = "blob",
            ["key"] = key,
            ["content_type"] = contentType,
            ["data"] = Convert.ToBase64String(data),
        }).ConfigureAwait(false);
    }

    private void RaiseStatus()
    {
        try
        {
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status listener failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var module in _modules.OfType<IDisposable>())
        {
            try
            {
                module.Dispose();
            }
            catch
            {
                // Ignore during shutdown.
            }
        }
    }
}
