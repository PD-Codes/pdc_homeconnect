using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Ipc;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Service.Native;

namespace PdcHomeConnect.Service;

/// <summary>
/// Named pipe server for the tray agents (one per signed-in user). The agent of the console
/// session is the "primary" agent: its entities are exposed to Home Assistant. While no agent is
/// connected, the last known agent entities stay registered with their offline state.
/// </summary>
public sealed class AgentBridge : BackgroundService
{
    public const string Owner = "agent";

    private readonly EntityHub _hub;
    private readonly HubServer _server;
    private readonly PairingManager _pairing;
    private readonly ServiceConfig _config;
    private readonly ServicePaths _paths;
    private readonly SessionTracker _sessions;
    private readonly DeviceInfoService _device;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<AgentConnection> _agents = [];
    private readonly ConcurrentDictionary<long, (AgentConnection Agent, TaskCompletionSource<CommandResult> Completion)> _pending = new();
    private readonly HashSet<int> _exitedSessions = [];
    private List<EntityDescriptor> _cachedDescriptors = [];
    private AgentConnection? _primary;
    private long _nextId;

    public AgentBridge(
        EntityHub hub,
        HubServer server,
        PairingManager pairing,
        ServiceConfig config,
        ServicePaths paths,
        SessionTracker sessions,
        DeviceInfoService device,
        ILogger<AgentBridge> logger)
    {
        _hub = hub;
        _server = server;
        _pairing = pairing;
        _config = config;
        _paths = paths;
        _sessions = sessions;
        _device = device;
        _logger = logger;
        LoadCache();

        _hub.FallbackCommand = (owner, command, ct) => owner == Owner
            ? ForwardCommandAsync(command, ct)
            : Task.FromResult(CommandResult.NotSupported(command.Action));
        _hub.FallbackMethod = (parameters, ct) =>
        {
            var method = parameters.GetString("__method") ?? string.Empty;
            parameters.Remove("__method");
            return ForwardCallAsync(method, parameters, ct);
        };

        _sessions.Changed += OnSessionsChanged;
        _server.ClientsChanged += () => _ = BroadcastStatusAsync();
        _pairing.Tokens.Changed += () => _ = BroadcastStatusAsync();
    }

    private sealed class AgentConnection(JsonLineChannel channel, int sessionId, uint processId)
    {
        public JsonLineChannel Channel { get; } = channel;
        public int SessionId { get; } = sessionId;
        public uint ProcessId { get; } = processId;
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
        public string? User { get; set; }
        public string? Version { get; set; }
        public bool Ready { get; set; }
        public List<EntityDescriptor> Descriptors { get; set; } = [];
        public Dictionary<string, JsonObject> States { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, BlobEntry> Blobs { get; } = new(StringComparer.Ordinal);

        public async Task SendAsync(JsonObject message)
        {
            try
            {
                await Channel.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The agent disconnected; cleanup happens in the read loop.
            }
        }
    }

    /// <summary>Sessions with a connected agent.</summary>
    public bool HasAgent(int sessionId)
    {
        lock (_gate)
        {
            return _agents.Any(a => a.SessionId == sessionId);
        }
    }

    /// <summary>True if the user chose "Exit" in the tray menu (the watchdog then leaves the session alone).</summary>
    public bool UserExited(int sessionId)
    {
        lock (_gate)
        {
            return _exitedSessions.Contains(sessionId);
        }
    }

    public void ForgetExit(int sessionId)
    {
        lock (_gate)
        {
            _exitedSessions.Remove(sessionId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ApplyFallback();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot create agent pipe");
                await Task.Delay(5000, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent pipe connection failed");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleAgentAsync(pipe, stoppingToken), stoppingToken);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            IpcNames.ServicePipe,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task HandleAgentAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        uint processId = 0;
        uint sessionId = 0;
        try
        {
            var handle = pipe.SafePipeHandle.DangerousGetHandle();
            if (!Win32.GetNamedPipeClientProcessId(handle, out processId) || !Win32.ProcessIdToSessionId(processId, out sessionId))
            {
                _logger.LogWarning("Agent connection rejected: client process unknown");
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var path = Win32.GetProcessPath(processId);
            if (!_config.AllowAnyAgent && !string.Equals(Path.GetFullPath(path ?? string.Empty), Path.GetFullPath(ServicePaths.AgentExecutable), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Agent connection from {Path} (pid {Pid}) rejected: not the installed agent", path, processId);
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent verification failed");
            await pipe.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var channel = new JsonLineChannel(pipe);
        var agent = new AgentConnection(channel, (int)sessionId, processId);
        _logger.LogInformation("Agent connected from session {Session} (pid {Pid})", sessionId, processId);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                try
                {
                    await HandleMessageAsync(agent, message).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Invalid message from agent in session {Session}", sessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent connection in session {Session} ended", sessionId);
        }
        finally
        {
            Remove(agent);
            await channel.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Agent in session {Session} disconnected", sessionId);
        }
    }

    private async Task HandleMessageAsync(AgentConnection agent, JsonObject message)
    {
        var type = message.GetString("type");
        bool isPrimary;
        lock (_gate)
        {
            isPrimary = ReferenceEquals(agent, _primary);
        }

        switch (type)
        {
            case "hello":
                agent.User = message.GetString("user");
                agent.Version = message.GetString("version");
                agent.Ready = true;
                lock (_gate)
                {
                    _agents.Add(agent);
                    _exitedSessions.Remove(agent.SessionId);
                }

                SelectPrimary();
                lock (_gate)
                {
                    isPrimary = ReferenceEquals(agent, _primary);
                }

                await agent.SendAsync(new JsonObject
                {
                    ["type"] = "welcome",
                    ["primary"] = isPrimary,
                    ["device_id"] = _device.DeviceId,
                    ["version"] = _device.Version,
                }).ConfigureAwait(false);
                await agent.SendAsync(BuildStatus()).ConfigureAwait(false);
                if (_pairing.Current is { } code)
                {
                    await agent.SendAsync(PairingMessage(code)).ConfigureAwait(false);
                }
                break;

            case "entities":
                var descriptors = message.GetArray("entities")?
                    .Select(n => n.Deserialize<EntityDescriptor>(Json.Options))
                    .Where(d => d is not null)
                    .Select(d => d!)
                    .ToList() ?? [];
                agent.Descriptors = descriptors;
                if (isPrimary)
                {
                    _hub.SetDescriptors(Owner, descriptors);
                    SaveCache(descriptors);
                }
                break;

            case "states":
                foreach (var (stateKey, value) in message.GetObject("states"))
                {
                    if (value is not JsonObject state)
                    {
                        continue;
                    }

                    agent.States[stateKey] = state;
                    if (isPrimary)
                    {
                        ApplyState(stateKey, state);
                    }
                }
                break;

            case "event":
                if (isPrimary)
                {
                    _hub.FireEvent(message.GetString("key") ?? string.Empty, message.GetString("event_type") ?? "event", message["data"] as JsonObject);
                }
                break;

            case "bus_event":
                if (isPrimary)
                {
                    _hub.FireBusEvent(message.GetString("event") ?? "event", message["data"] as JsonObject);
                }
                break;

            case "blob":
                var blobKey = message.GetString("key") ?? string.Empty;
                var data = Convert.FromBase64String(message.GetString("data") ?? string.Empty);
                var contentType = message.GetString("content_type") ?? "application/octet-stream";
                agent.Blobs[blobKey] = new BlobEntry(data, contentType, string.Empty);
                if (isPrimary)
                {
                    _hub.SetBlob(blobKey, data, contentType);
                }
                break;

            case "blob_remove":
                var removeKey = message.GetString("key") ?? string.Empty;
                agent.Blobs.Remove(removeKey);
                if (isPrimary)
                {
                    _hub.RemoveBlob(removeKey);
                }
                break;

            case "result":
                var id = message.GetLong("id") ?? -1;
                if (_pending.TryRemove(id, out var pending))
                {
                    pending.Completion.TrySetResult(CommandResult.FromJson(message));
                }
                break;

            case "session":
                _sessions.ReportLock(agent.SessionId, message.GetBool("locked") ?? false);
                break;

            case "exit":
                lock (_gate)
                {
                    _exitedSessions.Add(agent.SessionId);
                }
                break;

            case "request":
                await HandleRequestAsync(agent, message).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Requests from the tray UI (status, unpair, cancel pairing).</summary>
    private async Task HandleRequestAsync(AgentConnection agent, JsonObject message)
    {
        var id = message.GetLong("id") ?? 0;
        var method = message.GetString("method");
        var parameters = message.GetObject("params");
        var result = method switch
        {
            "status" => CommandResult.Ok(BuildStatus()),
            "unpair" => _pairing.Tokens.Remove(parameters.GetString("id") ?? string.Empty)
                ? CommandResult.Ok()
                : CommandResult.Fail(ErrorCodes.NotFound, "Unknown client."),
            "unpair_all" => Do(() => _pairing.Tokens.Clear()),
            "cancel_pairing" => Do(_pairing.Cancel),
            _ => CommandResult.NotSupported(method ?? string.Empty),
        };
        await agent.SendAsync(result.ToJson(id)).ConfigureAwait(false);

        static CommandResult Do(Action action)
        {
            action();
            return CommandResult.Ok();
        }
    }

    private void ApplyState(string key, JsonObject state) =>
        _hub.SetState(key, state["state"]?.DeepClone(), state["attributes"] as JsonObject, state.GetBool("available") ?? true);

    private void Remove(AgentConnection agent)
    {
        lock (_gate)
        {
            _agents.Remove(agent);
        }

        foreach (var (id, pending) in _pending.Where(p => ReferenceEquals(p.Value.Agent, agent)).ToList())
        {
            if (_pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetResult(CommandResult.Fail(ErrorCodes.Unavailable, "The tray app disconnected."));
            }
        }

        SelectPrimary();
    }

    private void OnSessionsChanged()
    {
        SelectPrimary();
        bool hasPrimary;
        lock (_gate)
        {
            hasPrimary = _primary is not null;
        }

        if (!hasPrimary)
        {
            ApplyActivityFallback();
        }
    }

    private void SelectPrimary()
    {
        AgentConnection? previous;
        AgentConnection? next;
        lock (_gate)
        {
            var consoleId = (int)Win32.WTSGetActiveConsoleSessionId();
            next = _agents
                .Where(a => a.Ready)
                .OrderByDescending(a => a.SessionId == consoleId)
                .ThenByDescending(a => a.ConnectedAt)
                .FirstOrDefault();
            previous = _primary;
            if (ReferenceEquals(previous, next))
            {
                return;
            }

            _primary = next;
        }

        _logger.LogInformation("Primary agent: {User} (session {Session})", next?.User ?? "none", next?.SessionId);
        if (previous is not null)
        {
            _ = previous.SendAsync(new JsonObject { ["type"] = "primary", ["primary"] = false });
        }

        if (next is not null)
        {
            _ = next.SendAsync(new JsonObject { ["type"] = "primary", ["primary"] = true });
            _hub.SetDescriptors(Owner, next.Descriptors);
            if (next.Descriptors.Count > 0)
            {
                SaveCache(next.Descriptors);
            }

            foreach (var (key, state) in next.States)
            {
                ApplyState(key, state);
            }

            foreach (var (key, blob) in next.Blobs)
            {
                _hub.SetBlob(key, blob.Data, blob.ContentType);
            }
        }
        else
        {
            ApplyFallback();
        }

        _device.SetAgent(next is not null, next?.User);
        _hub.MarkDeviceChanged();
    }

    /// <summary>No agent: keep the last known agent entities with their offline state.</summary>
    private void ApplyFallback()
    {
        List<EntityDescriptor> cached;
        lock (_gate)
        {
            if (_primary is not null)
            {
                return;
            }
            cached = _cachedDescriptors;
        }

        _hub.SetDescriptors(Owner, cached);
        foreach (var descriptor in cached)
        {
            if (descriptor.OfflineState is not null)
            {
                _hub.SetState(descriptor.Key, descriptor.OfflineState.DeepClone());
            }
            else
            {
                _hub.SetUnavailable(descriptor.Key);
            }
        }

        ApplyActivityFallback();
    }

    private void ApplyActivityFallback()
    {
        if (_cachedDescriptors.All(d => d.Key != "activity"))
        {
            return;
        }

        var state = _sessions.ConsoleState switch
        {
            "logged_off" => "logged_off",
            "locked" => "locked",
            _ => "unknown",
        };
        _hub.SetState("activity", JsonValue.Create(state));
    }

    public async Task<CommandResult> ForwardCommandAsync(EntityCommand command, CancellationToken cancellationToken) =>
        await SendRequestAsync(id => new JsonObject
        {
            ["type"] = "command",
            ["id"] = id,
            ["key"] = command.Key,
            ["action"] = command.Action,
            ["params"] = command.Params.DeepClone(),
        }, cancellationToken).ConfigureAwait(false);

    public async Task<CommandResult> ForwardCallAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
        await SendRequestAsync(id => new JsonObject
        {
            ["type"] = "call",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters.DeepClone(),
        }, cancellationToken).ConfigureAwait(false);

    private async Task<CommandResult> SendRequestAsync(Func<long, JsonObject> build, CancellationToken cancellationToken)
    {
        AgentConnection? agent;
        lock (_gate)
        {
            agent = _primary;
        }

        if (agent is null)
        {
            return CommandResult.Fail(ErrorCodes.Unavailable, "The PDC HomeConnect tray app is not running (no user signed in?).");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (agent, completion);
        try
        {
            await agent.SendAsync(build(id)).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => completion.TrySetResult(CommandResult.Fail(ErrorCodes.Timeout, "The tray app did not answer in time.")));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a message to every connected agent (e.g. a pairing code).</summary>
    public async Task BroadcastAsync(JsonObject message)
    {
        List<AgentConnection> agents;
        lock (_gate)
        {
            agents = _agents.ToList();
        }

        foreach (var agent in agents)
        {
            await agent.SendAsync((JsonObject)message.DeepClone()).ConfigureAwait(false);
        }
    }

    public static JsonObject PairingMessage(PairingCode code) => new()
    {
        ["type"] = "pairing_code",
        ["code"] = code.Code,
        ["display"] = code.Display,
        ["client"] = code.ClientName,
        ["address"] = code.RemoteAddress,
        ["expires"] = code.ExpiresAt.ToString("O"),
    };

    private async Task BroadcastStatusAsync()
    {
        try
        {
            await BroadcastAsync(BuildStatus()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status broadcast failed");
        }
    }

    private JsonObject BuildStatus()
    {
        var clients = new JsonArray(_server.GetClients().Select(c => (JsonNode)new JsonObject
        {
            ["name"] = c.Name,
            ["address"] = c.Address,
            ["since"] = c.ConnectedSince.ToString("O"),
        }).ToArray());
        var paired = new JsonArray(_pairing.Tokens.Clients.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["created"] = c.Created.ToString("O"),
            ["last_seen"] = c.LastSeen?.ToString("O"),
            ["last_address"] = c.LastAddress,
        }).ToArray());
        return new JsonObject
        {
            ["type"] = "status",
            ["device_name"] = _device.Name,
            ["device_id"] = _device.DeviceId,
            ["version"] = _device.Version,
            ["port"] = _config.Port,
            ["fingerprint"] = _device.CertificateFingerprint,
            ["connected_clients"] = clients,
            ["paired_clients"] = paired,
        };
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_paths.AgentCache))
            {
                _cachedDescriptors = JsonSerializer.Deserialize<List<EntityDescriptor>>(File.ReadAllText(_paths.AgentCache), Json.Options) ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent entity cache is invalid");
            _cachedDescriptors = [];
        }
    }

    private void SaveCache(List<EntityDescriptor> descriptors)
    {
        _cachedDescriptors = descriptors;
        try
        {
            File.WriteAllText(_paths.AgentCache, JsonSerializer.Serialize(descriptors, Json.Indented));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not save the agent entity cache");
        }
    }
}
