using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Hub;

/// <summary>Sink used by providers to publish entities, states and events.</summary>
public interface IStateSink
{
    /// <summary>Replaces the complete descriptor set of one owner.</summary>
    void SetDescriptors(string owner, IEnumerable<EntityDescriptor> descriptors);

    /// <summary>Publishes the state of an entity. Unchanged states are suppressed.</summary>
    void SetState(string key, JsonNode? state, JsonObject? attributes = null, bool available = true);

    /// <summary>Marks an entity as unavailable.</summary>
    void SetUnavailable(string key);

    /// <summary>Triggers an event entity.</summary>
    void FireEvent(string key, string eventType, JsonObject? data = null);

    /// <summary>Fires a Home Assistant bus event (prefixed with the integration domain on the HA side).</summary>
    void FireBusEvent(string name, JsonObject? data = null);

    /// <summary>Stores binary data (e.g. media cover art) that Home Assistant can fetch on demand.</summary>
    void SetBlob(string key, byte[] data, string contentType);

    void RemoveBlob(string key);
}

/// <summary>An entity command coming from Home Assistant.</summary>
public sealed record EntityCommand(string Key, string Action, JsonObject Params);

/// <summary>Result of a command or method call.</summary>
public sealed record CommandResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null, JsonNode? Data = null)
{
    public static CommandResult Ok(JsonNode? data = null) => new(true, Data: data);

    public static CommandResult Fail(string code, string message) => new(false, code, message);

    public static CommandResult NotSupported(string action) =>
        Fail(ErrorCodes.NotSupported, $"Action '{action}' is not supported by this entity.");

    public JsonObject ToJson(long id)
    {
        var msg = new JsonObject
        {
            ["type"] = MessageTypes.Result,
            ["id"] = id,
            ["success"] = Success,
        };
        if (!Success)
        {
            msg["error"] = new JsonObject
            {
                ["code"] = ErrorCode ?? ErrorCodes.Failed,
                ["message"] = ErrorMessage ?? "Unknown error",
            };
        }
        if (Data is not null)
        {
            msg["data"] = Data.DeepClone();
        }
        return msg;
    }

    public static CommandResult FromJson(JsonObject msg)
    {
        var success = msg.GetBool("success") ?? false;
        var error = msg["error"] as JsonObject;
        return new CommandResult(
            success,
            error?.GetString("code"),
            error?.GetString("message"),
            msg["data"]?.DeepClone());
    }
}

/// <summary>Handles commands for the entities of one owner.</summary>
public interface ICommandHandler
{
    Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles a named method call (Home Assistant action).</summary>
public delegate Task<CommandResult> MethodHandler(JsonObject parameters, CancellationToken cancellationToken);

/// <summary>Binary payload stored in the hub.</summary>
public sealed record BlobEntry(byte[] Data, string ContentType, string Hash);
