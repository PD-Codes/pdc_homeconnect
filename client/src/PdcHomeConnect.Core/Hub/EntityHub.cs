using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Core.Hub;

/// <summary>
/// Central registry of entity descriptors, states, events and blobs. Thread-safe.
/// Providers write into the hub; the <see cref="HubServer"/> drains changes and pushes them to Home Assistant.
/// </summary>
public sealed class EntityHub : IStateSink
{
    private sealed record Owned(EntityDescriptor Descriptor, string Owner, string Json);

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly Dictionary<string, Owned> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _ownerKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ICommandHandler> _commandHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MethodHandler> _methods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonObject> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stateJson = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingStates = new(StringComparer.Ordinal);
    private readonly List<JsonObject> _pendingMessages = [];
    private readonly Dictionary<string, BlobEntry> _blobs = new(StringComparer.Ordinal);
    private readonly AsyncSignal _signal = new();
    private int _revision;
    private bool _descriptorsDirty;
    private bool _deviceDirty;

    public EntityHub(ILogger<EntityHub> logger)
    {
        _logger = logger;
    }

    /// <summary>Called for unknown methods (used to forward calls to the tray agent).</summary>
    public MethodHandler? FallbackMethod { get; set; }

    /// <summary>Called for commands whose owner has no registered handler.</summary>
    public Func<string, EntityCommand, CancellationToken, Task<CommandResult>>? FallbackCommand { get; set; }

    public int Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    public void RegisterCommandHandler(string owner, ICommandHandler handler)
    {
        lock (_gate)
        {
            _commandHandlers[owner] = handler;
        }
    }

    public void RegisterMethod(string name, MethodHandler handler)
    {
        lock (_gate)
        {
            _methods[name] = handler;
        }
    }

    public void SetDescriptors(string owner, IEnumerable<EntityDescriptor> descriptors)
    {
        var changed = false;
        lock (_gate)
        {
            var oldKeys = _ownerKeys.GetValueOrDefault(owner) ?? [];
            var newKeys = new List<string>();
            foreach (var descriptor in descriptors)
            {
                if (newKeys.Contains(descriptor.Key))
                {
                    _logger.LogWarning("Duplicate entity key {Key} from {Owner} ignored", descriptor.Key, owner);
                    continue;
                }

                if (_entities.TryGetValue(descriptor.Key, out var existing) && existing.Owner != owner)
                {
                    _logger.LogWarning("Entity key {Key} from {Owner} conflicts with {Existing}", descriptor.Key, owner, existing.Owner);
                    continue;
                }

                descriptor.Owner = owner;
                var json = Json.Serialize(descriptor);
                newKeys.Add(descriptor.Key);
                if (existing is null || existing.Json != json)
                {
                    _entities[descriptor.Key] = new Owned(descriptor, owner, json);
                    changed = true;

                    // A state may have been published before the descriptor existed: resend it.
                    if (_states.ContainsKey(descriptor.Key))
                    {
                        _pendingStates.Add(descriptor.Key);
                    }
                }
            }

            foreach (var removed in oldKeys.Where(k => !newKeys.Contains(k)))
            {
                _entities.Remove(removed);
                _states.Remove(removed);
                _stateJson.Remove(removed);
                _pendingStates.Remove(removed);
                changed = true;
            }

            _ownerKeys[owner] = newKeys;
            if (changed)
            {
                _revision++;
                _descriptorsDirty = true;
            }
        }

        if (changed)
        {
            _signal.Set();
        }
    }

    public void RemoveOwner(string owner) => SetDescriptors(owner, []);

    public void SetState(string key, JsonNode? state, JsonObject? attributes = null, bool available = true)
    {
        var obj = new JsonObject
        {
            ["state"] = state?.DeepClone(),
            ["available"] = available,
        };
        if (attributes is { Count: > 0 })
        {
            obj["attributes"] = attributes.DeepClone();
        }

        var json = obj.ToJsonString(Json.Options);
        lock (_gate)
        {
            if (_stateJson.TryGetValue(key, out var previous) && previous == json)
            {
                return;
            }

            _stateJson[key] = json;
            _states[key] = obj;
            _pendingStates.Add(key);
        }

        _signal.Set();
    }

    public void SetUnavailable(string key)
    {
        JsonNode? lastState = null;
        JsonObject? lastAttributes = null;
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var current))
            {
                lastState = current["state"]?.DeepClone();
                lastAttributes = current["attributes"]?.DeepClone() as JsonObject;
            }
        }

        SetState(key, lastState, lastAttributes, available: false);
    }

    public void FireEvent(string key, string eventType, JsonObject? data = null)
    {
        var msg = new JsonObject
        {
            ["type"] = MessageTypes.Event,
            ["key"] = key,
            ["event_type"] = eventType,
            ["data"] = data?.DeepClone() ?? new JsonObject(),
        };
        lock (_gate)
        {
            _pendingMessages.Add(msg);
        }

        _signal.Set();
    }

    public void FireBusEvent(string name, JsonObject? data = null)
    {
        var msg = new JsonObject
        {
            ["type"] = MessageTypes.BusEvent,
            ["event"] = name,
            ["data"] = data?.DeepClone() ?? new JsonObject(),
        };
        lock (_gate)
        {
            _pendingMessages.Add(msg);
        }

        _signal.Set();
    }

    public void SetBlob(string key, byte[] data, string contentType)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(data))[..16];
        lock (_gate)
        {
            _blobs[key] = new BlobEntry(data, contentType, hash);
        }
    }

    public void RemoveBlob(string key)
    {
        lock (_gate)
        {
            _blobs.Remove(key);
        }
    }

    public BlobEntry? GetBlob(string key)
    {
        lock (_gate)
        {
            return _blobs.GetValueOrDefault(key);
        }
    }

    /// <summary>Signals that the device info changed (pushed with the next flush).</summary>
    public void MarkDeviceChanged()
    {
        lock (_gate)
        {
            _deviceDirty = true;
        }

        _signal.Set();
    }

    public EntityDescriptor? GetDescriptor(string key)
    {
        lock (_gate)
        {
            return _entities.TryGetValue(key, out var owned) ? owned.Descriptor : null;
        }
    }

    public IReadOnlyList<EntityDescriptor> GetDescriptors()
    {
        lock (_gate)
        {
            return _entities.Values.Select(o => o.Descriptor).OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyList<EntityDescriptor> GetDescriptors(string owner)
    {
        lock (_gate)
        {
            return _entities.Values.Where(o => o.Owner == owner).Select(o => o.Descriptor).ToList();
        }
    }

    public JsonObject? GetState(string key)
    {
        lock (_gate)
        {
            return _states.TryGetValue(key, out var s) ? (JsonObject)s.DeepClone() : null;
        }
    }

    /// <summary>Builds the full "entities" message.</summary>
    public JsonObject BuildEntitiesMessage()
    {
        lock (_gate)
        {
            return BuildEntitiesMessageLocked();
        }
    }

    /// <summary>Builds a "states" message with every known state.</summary>
    public JsonObject BuildFullStatesMessage()
    {
        lock (_gate)
        {
            var states = new JsonObject();
            foreach (var (key, value) in _states)
            {
                if (_entities.ContainsKey(key))
                {
                    states[key] = value.DeepClone();
                }
            }
            return new JsonObject { ["type"] = MessageTypes.States, ["states"] = states };
        }
    }

    /// <summary>Waits until something changed.</summary>
    public Task WaitForChangesAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

    /// <summary>
    /// Collects all pending outbound messages in protocol order (device, entities, states, events)
    /// and resets the pending state.
    /// </summary>
    public HubChanges DrainChanges()
    {
        lock (_gate)
        {
            var changes = new HubChanges
            {
                DeviceChanged = _deviceDirty,
                Entities = _descriptorsDirty ? BuildEntitiesMessageLocked() : null,
            };

            if (_pendingStates.Count > 0)
            {
                var states = new JsonObject();
                foreach (var key in _pendingStates)
                {
                    if (_states.TryGetValue(key, out var value) && _entities.ContainsKey(key))
                    {
                        states[key] = value.DeepClone();
                    }
                }

                if (states.Count > 0)
                {
                    changes.States = new JsonObject { ["type"] = MessageTypes.States, ["states"] = states };
                }
            }

            changes.Messages.AddRange(_pendingMessages);
            _pendingMessages.Clear();
            _pendingStates.Clear();
            _descriptorsDirty = false;
            _deviceDirty = false;
            return changes;
        }
    }

    /// <summary>Routes a command to the owning provider.</summary>
    public async Task<CommandResult> ExecuteCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        ICommandHandler? handler;
        string? owner;
        lock (_gate)
        {
            if (!_entities.TryGetValue(command.Key, out var owned))
            {
                return CommandResult.Fail(ErrorCodes.NotFound, $"Unknown entity '{command.Key}'.");
            }

            owner = owned.Owner;
            handler = _commandHandlers.GetValueOrDefault(owner);
        }

        try
        {
            if (handler is not null)
            {
                return await handler.HandleCommandAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (FallbackCommand is not null)
            {
                return await FallbackCommand(owner, command, cancellationToken).ConfigureAwait(false);
            }

            return CommandResult.Fail(ErrorCodes.NotSupported, $"Entity '{command.Key}' does not accept commands.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Fail(ErrorCodes.Timeout, "The command timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command {Action} for {Key} failed", command.Action, command.Key);
            return CommandResult.Fail(ErrorCodes.Failed, ex.Message);
        }
    }

    /// <summary>Routes a method call (Home Assistant action).</summary>
    public async Task<CommandResult> CallMethodAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        MethodHandler? handler;
        lock (_gate)
        {
            handler = _methods.GetValueOrDefault(method) ?? FallbackMethodFor(method);
        }

        if (handler is null)
        {
            return CommandResult.Fail(ErrorCodes.NotSupported, $"Method '{method}' is not supported.");
        }

        try
        {
            return await handler(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Fail(ErrorCodes.Timeout, "The call timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Method {Method} failed", method);
            return CommandResult.Fail(ErrorCodes.Failed, ex.Message);
        }
    }

    private MethodHandler? FallbackMethodFor(string method)
    {
        var fallback = FallbackMethod;
        if (fallback is null)
        {
            return null;
        }

        return (parameters, ct) =>
        {
            var copy = (JsonObject)parameters.DeepClone();
            copy["__method"] = method;
            return fallback(copy, ct);
        };
    }

    private JsonObject BuildEntitiesMessageLocked()
    {
        var array = new JsonArray();
        foreach (var owned in _entities.Values.OrderBy(o => o.Descriptor.Key, StringComparer.Ordinal))
        {
            array.Add(JsonNode.Parse(owned.Json));
        }

        return new JsonObject
        {
            ["type"] = MessageTypes.Entities,
            ["revision"] = _revision,
            ["entities"] = array,
        };
    }
}

/// <summary>Pending outbound changes collected by <see cref="EntityHub.DrainChanges"/>.</summary>
public sealed class HubChanges
{
    public bool DeviceChanged { get; init; }

    public JsonObject? Entities { get; init; }

    public JsonObject? States { get; set; }

    public List<JsonObject> Messages { get; } = [];

    public bool IsEmpty => !DeviceChanged && Entities is null && States is null && Messages.Count == 0;
}
