using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Hub;

public sealed class HubServerOptions
{
    /// <summary>Provides the current device info (must be fast, called while holding a lock).</summary>
    public required Func<DeviceInfo> DeviceInfo { get; init; }

    public int MaxConnections { get; init; } = 8;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan PreAuthTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromMilliseconds(60);
}

/// <summary>Snapshot of a connected Home Assistant client (for status displays).</summary>
public sealed record ConnectedClientInfo(string Name, string? Address, DateTimeOffset ConnectedSince, string? Version);

/// <summary>
/// Accepts WebSocket connections from Home Assistant, handles authentication/pairing and pushes hub changes.
/// </summary>
public sealed class HubServer : BackgroundService
{
    private const int MaxPreAuthMessageBytes = 64 * 1024;
    private const int MaxMessageBytes = 48 * 1024 * 1024;

    private readonly EntityHub _hub;
    private readonly PairingManager _pairing;
    private readonly HubServerOptions _options;
    private readonly ILogger _logger;
    private readonly object _broadcastGate = new();
    private readonly List<ClientConnection> _authenticated = [];
    private readonly HashSet<ClientConnection> _all = [];
    private readonly CancellationTokenSource _shutdown = new();

    public HubServer(EntityHub hub, PairingManager pairing, HubServerOptions options, ILogger<HubServer> logger)
    {
        _hub = hub;
        _pairing = pairing;
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised when an authenticated client connects or disconnects.</summary>
    public event Action? ClientsChanged;

    public IReadOnlyList<ConnectedClientInfo> GetClients()
    {
        lock (_broadcastGate)
        {
            return _authenticated
                .Select(c => new ConnectedClientInfo(c.Client?.Name ?? c.ClientName ?? "Home Assistant", c.RemoteAddress, c.ConnectedAt, c.ClientVersion))
                .ToList();
        }
    }

    public int AuthenticatedCount
    {
        get
        {
            lock (_broadcastGate)
            {
                return _authenticated.Count;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _hub.WaitForChangesAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_options.FlushDelay, stoppingToken).ConfigureAwait(false);
                Flush();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Flush loop error");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await SendByeAsync("shutdown", TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tells all clients that this PC goes away (shutdown, sleep) and closes the connections.</summary>
    public async Task SendByeAsync(string reason, TimeSpan wait)
    {
        List<ClientConnection> connections;
        lock (_broadcastGate)
        {
            connections = _all.ToList();
            var bye = new JsonObject { ["type"] = MessageTypes.Bye, ["reason"] = reason }.ToJsonString(Json.Options);
            foreach (var connection in connections)
            {
                connection.TryEnqueue(bye);
                connection.Complete();
            }
        }

        if (connections.Count == 0)
        {
            return;
        }

        await Task.Delay(wait).ConfigureAwait(false);
        foreach (var connection in connections)
        {
            try
            {
                if (connection.Socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                    await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, reason, cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                connection.Abort();
            }
        }
    }

    /// <summary>Handles one WebSocket connection until it closes.</summary>
    public async Task HandleConnectionAsync(WebSocket socket, IPAddress? remoteAddress, CancellationToken requestAborted)
    {
        var remote = remoteAddress is null ? null : (remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress).ToString();
        var connection = new ClientConnection(socket, remote);
        lock (_broadcastGate)
        {
            if (_all.Count >= _options.MaxConnections)
            {
                _logger.LogWarning("Rejected connection from {Remote}: too many connections", remote);
                connection.Abort();
                return;
            }
            _all.Add(connection);
        }

        _logger.LogInformation("Connection {Id} from {Remote}", connection.ConnectionId, remote);
        using var preAuthTimer = new CancellationTokenSource(_options.PreAuthTimeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _shutdown.Token, preAuthTimer.Token);
        var sender = connection.RunSenderAsync(cts.Token);
        var wasAuthenticated = false;

        try
        {
            connection.TryEnqueue(BuildHello());
            while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var text = await connection.ReceiveAsync(connection.IsAuthenticated ? MaxMessageBytes : MaxPreAuthMessageBytes, cts.Token).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                var message = Json.ParseObject(text);
                if (message is null)
                {
                    connection.TryEnqueue(Error(null, ErrorCodes.InvalidParams, "Invalid JSON."));
                    continue;
                }

                var keepOpen = HandleMessage(connection, message, cts.Token);
                if (connection.IsAuthenticated && !wasAuthenticated)
                {
                    wasAuthenticated = true;
                    preAuthTimer.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                if (!keepOpen)
                {
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Connection closed or server stopping.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("Connection {Id} closed: {Message}", connection.ConnectionId, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning("Connection {Id} sent invalid data: {Message}", connection.ConnectionId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection {Id} failed", connection.ConnectionId);
        }
        finally
        {
            lock (_broadcastGate)
            {
                _authenticated.Remove(connection);
                _all.Remove(connection);
            }

            connection.Complete();
            try
            {
                await sender.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Sender stops with the connection.
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    connection.Abort();
                }
            }

            _logger.LogInformation("Connection {Id} from {Remote} closed", connection.ConnectionId, remote);
            if (wasAuthenticated)
            {
                ClientsChanged?.Invoke();
            }
        }
    }

    /// <summary>Handles a message. Returns false if the connection should be closed.</summary>
    private bool HandleMessage(ClientConnection connection, JsonObject message, CancellationToken cancellationToken)
    {
        var type = message.GetString("type");
        var id = message.GetLong("id");
        switch (type)
        {
            case MessageTypes.Hello:
                connection.ClientName = message.GetString("client");
                connection.ClientVersion = message.GetString("version");
                return true;

            case MessageTypes.Ping:
                connection.TryEnqueue(new JsonObject { ["type"] = MessageTypes.Pong, ["id"] = id });
                return true;

            case MessageTypes.Auth:
                return HandleAuth(connection, message);

            case MessageTypes.PairRequest:
                HandlePairRequest(connection, message);
                return true;

            case MessageTypes.Pair:
                return HandlePair(connection, message);
        }

        if (!connection.IsAuthenticated)
        {
            connection.TryEnqueue(Error(id, ErrorCodes.Unauthorized, "Authenticate first."));
            return true;
        }

        switch (type)
        {
            case MessageTypes.Unpair:
                _pairing.Tokens.Remove(connection.Client!.Id);
                connection.TryEnqueue(CommandResult.Ok().ToJson(id ?? 0));
                return false;

            case MessageTypes.Command:
                if (id is null)
                {
                    connection.TryEnqueue(Error(null, ErrorCodes.InvalidParams, "Missing id."));
                    return true;
                }
                _ = RunCommandAsync(connection, id.Value, message, cancellationToken);
                return true;

            case MessageTypes.Call:
                if (id is null)
                {
                    connection.TryEnqueue(Error(null, ErrorCodes.InvalidParams, "Missing id."));
                    return true;
                }
                _ = RunCallAsync(connection, id.Value, message, cancellationToken);
                return true;

            case MessageTypes.GetBlob:
                HandleGetBlob(connection, id ?? 0, message);
                return true;

            default:
                connection.TryEnqueue(Error(id, ErrorCodes.NotSupported, $"Unknown message type '{type}'."));
                return true;
        }
    }

    private bool HandleAuth(ClientConnection connection, JsonObject message)
    {
        var token = message.GetString("token") ?? string.Empty;
        var client = _pairing.ValidateToken(token, connection.RemoteAddress);
        if (client is null)
        {
            _logger.LogWarning("Authentication from {Remote} failed", connection.RemoteAddress);
            connection.TryEnqueue(new JsonObject { ["type"] = MessageTypes.AuthFailed, ["reason"] = "invalid_token" });
            return false;
        }

        Authenticate(connection, client, new JsonObject { ["type"] = MessageTypes.AuthOk });
        return true;
    }

    private void HandlePairRequest(ClientConnection connection, JsonObject message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - connection.LastPairRequest < TimeSpan.FromSeconds(5))
        {
            connection.TryEnqueue(Error(message.GetLong("id"), ErrorCodes.Forbidden, "Too many pairing requests."));
            return;
        }

        connection.LastPairRequest = now;
        var name = message.GetString("client_name") ?? connection.ClientName ?? "Home Assistant";
        var code = _pairing.RequestCode(name, connection.RemoteAddress);
        connection.TryEnqueue(new JsonObject
        {
            ["type"] = MessageTypes.PairPending,
            ["expires_in"] = (int)Math.Max(0, (code.ExpiresAt - now).TotalSeconds),
        });
    }

    private bool HandlePair(ClientConnection connection, JsonObject message)
    {
        var name = message.GetString("client_name") ?? connection.ClientName ?? "Home Assistant";
        var outcome = _pairing.TryPair(message.GetString("code") ?? string.Empty, name, connection.RemoteAddress, out var token);
        if (outcome != PairOutcome.Success || token is null)
        {
            var reason = outcome switch
            {
                PairOutcome.NoActiveCode => "no_active_code",
                PairOutcome.TooManyAttempts => "too_many_attempts",
                _ => "invalid_code",
            };
            connection.TryEnqueue(new JsonObject { ["type"] = MessageTypes.PairFailed, ["reason"] = reason });
            return true;
        }

        var client = _pairing.ValidateToken(token, connection.RemoteAddress)!;
        Authenticate(connection, client, new JsonObject { ["type"] = MessageTypes.PairOk, ["token"] = token });
        return true;
    }

    private void Authenticate(ClientConnection connection, PairedClient client, JsonObject reply)
    {
        connection.Client = client;
        lock (_broadcastGate)
        {
            // Snapshot and registration happen atomically with respect to the flush loop,
            // so the client never receives an older snapshot after a newer delta.
            connection.TryEnqueue(reply);
            connection.TryEnqueue(BuildDeviceMessage());
            connection.TryEnqueue(_hub.BuildEntitiesMessage());
            connection.TryEnqueue(_hub.BuildFullStatesMessage());
            if (!_authenticated.Contains(connection))
            {
                _authenticated.Add(connection);
            }
        }

        _logger.LogInformation("Client {Name} authenticated from {Remote}", client.Name, connection.RemoteAddress);
        ClientsChanged?.Invoke();
    }

    private async Task RunCommandAsync(ClientConnection connection, long id, JsonObject message, CancellationToken cancellationToken)
    {
        var key = message.GetString("key") ?? string.Empty;
        var action = message.GetString("action") ?? string.Empty;
        var parameters = message["params"] as JsonObject ?? new JsonObject();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.CommandTimeout);
        _logger.LogDebug("Command {Action} for {Key}", action, key);
        var result = await _hub.ExecuteCommandAsync(new EntityCommand(key, action, (JsonObject)parameters.DeepClone()), cts.Token).ConfigureAwait(false);
        connection.TryEnqueue(result.ToJson(id));
    }

    private async Task RunCallAsync(ClientConnection connection, long id, JsonObject message, CancellationToken cancellationToken)
    {
        var method = message.GetString("method") ?? string.Empty;
        var parameters = message["params"] as JsonObject ?? new JsonObject();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.CommandTimeout);
        _logger.LogDebug("Call {Method}", method);
        var result = await _hub.CallMethodAsync(method, (JsonObject)parameters.DeepClone(), cts.Token).ConfigureAwait(false);
        connection.TryEnqueue(result.ToJson(id));
    }

    private void HandleGetBlob(ClientConnection connection, long id, JsonObject message)
    {
        var key = message.GetString("key") ?? string.Empty;
        var blob = _hub.GetBlob(key);
        if (blob is null)
        {
            connection.TryEnqueue(CommandResult.Fail(ErrorCodes.NotFound, $"No data for '{key}'.").ToJson(id));
            return;
        }

        connection.TryEnqueue(CommandResult.Ok(new JsonObject
        {
            ["content_type"] = blob.ContentType,
            ["hash"] = blob.Hash,
            ["data"] = Convert.ToBase64String(blob.Data),
        }).ToJson(id));
    }

    private void Flush()
    {
        lock (_broadcastGate)
        {
            var changes = _hub.DrainChanges();
            if (changes.IsEmpty || _authenticated.Count == 0)
            {
                return;
            }

            var payloads = new List<string>();
            if (changes.DeviceChanged)
            {
                payloads.Add(BuildDeviceMessage().ToJsonString(Json.Options));
            }

            if (changes.Entities is not null)
            {
                payloads.Add(changes.Entities.ToJsonString(Json.Options));
            }

            if (changes.States is not null)
            {
                payloads.Add(changes.States.ToJsonString(Json.Options));
            }

            payloads.AddRange(changes.Messages.Select(m => m.ToJsonString(Json.Options)));
            foreach (var connection in _authenticated.ToList())
            {
                foreach (var payload in payloads)
                {
                    if (!connection.TryEnqueue(payload))
                    {
                        _logger.LogWarning("Client {Remote} is too slow, dropping connection", connection.RemoteAddress);
                        _authenticated.Remove(connection);
                        connection.Abort();
                        break;
                    }
                }
            }
        }
    }

    private JsonObject BuildHello() => new()
    {
        ["type"] = MessageTypes.Hello,
        ["protocol"] = ProtocolConstants.Version,
        ["server"] = "pdc_homeconnect",
        ["device"] = Json.ToNode(_options.DeviceInfo()),
    };

    private JsonObject BuildDeviceMessage() => new()
    {
        ["type"] = MessageTypes.Device,
        ["device"] = Json.ToNode(_options.DeviceInfo()),
    };

    private static JsonObject Error(long? id, string code, string message) => new()
    {
        ["type"] = MessageTypes.Error,
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
