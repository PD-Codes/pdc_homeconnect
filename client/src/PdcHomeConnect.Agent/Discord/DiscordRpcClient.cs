using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Agent.Discord;

/// <summary>Error returned by the Discord client (RPC "ERROR" event or a close frame).</summary>
public sealed class DiscordRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Minimal client for the local Discord RPC (named pipe \\.\pipe\discord-ipc-N).
/// Frames: int32 opcode, int32 length (little endian), UTF-8 JSON payload.
/// </summary>
public sealed class DiscordRpcClient : IAsyncDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;
    private const int MaxFrame = 16 * 1024 * 1024;

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    private DiscordRpcClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    /// <summary>Raised for DISPATCH frames: (event name, data).</summary>
    public event Action<string, JsonObject>? EventReceived;

    /// <summary>Completes when the connection is closed (with the reason, if any).</summary>
    public Task Completion => _readLoop ?? Task.CompletedTask;

    /// <summary>The READY payload (contains the current user once authenticated).</summary>
    public JsonObject? Ready { get; private set; }

    /// <summary>Connects to the first available Discord pipe. Returns null if Discord is not running.</summary>
    public static async Task<DiscordRpcClient?> ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var client = new DiscordRpcClient(pipe);
            try
            {
                await client.HandshakeAsync(clientId, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        return null;
    }

    private async Task HandshakeAsync(string clientId, CancellationToken cancellationToken)
    {
        await WriteFrameAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var (op, payload) = await ReadFrameAsync(timeout.Token).ConfigureAwait(false);
        if (op == OpClose)
        {
            throw new DiscordRpcException((int)(payload?.GetLong("code") ?? 0), payload?.GetString("message") ?? "Discord closed the connection.");
        }

        if (op != OpFrame || payload?.GetString("evt") != "READY")
        {
            throw new DiscordRpcException(0, "Unexpected handshake response from Discord.");
        }

        Ready = payload["data"] as JsonObject;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    /// <summary>Sends a command and waits for its response data.</summary>
    public async Task<JsonNode?> SendAsync(string command, JsonObject? args = null, string? evt = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = completion;
        try
        {
            var payload = new JsonObject { ["cmd"] = command, ["args"] = args ?? [], ["nonce"] = nonce };
            if (evt is not null)
            {
                payload["evt"] = evt;
            }

            await WriteFrameAsync(OpFrame, payload, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    public Task SubscribeAsync(string evt, JsonObject? args = null, CancellationToken cancellationToken = default) =>
        SendAsync("SUBSCRIBE", args, evt, cancellationToken: cancellationToken);

    public Task UnsubscribeAsync(string evt, JsonObject? args = null, CancellationToken cancellationToken = default) =>
        SendAsync("UNSUBSCRIBE", args, evt, cancellationToken: cancellationToken);

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (op, payload) = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                switch (op)
                {
                    case OpFrame when payload is not null:
                        Dispatch(payload);
                        break;
                    case OpPing:
                        await WriteFrameAsync(OpPong, payload ?? [], cancellationToken).ConfigureAwait(false);
                        break;
                    case OpClose:
                        throw new DiscordRpcException((int)(payload?.GetLong("code") ?? 0), payload?.GetString("message") ?? "Discord closed the connection.");
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(error ?? new DiscordRpcException(0, "Connection closed."));
            }
        }

        if (error is not null and not OperationCanceledException)
        {
            throw error;
        }
    }

    private void Dispatch(JsonObject payload)
    {
        var nonce = payload.GetString("nonce");
        var evt = payload.GetString("evt");
        if (nonce is not null && _pending.TryRemove(nonce, out var completion))
        {
            if (evt == "ERROR")
            {
                var data = payload["data"] as JsonObject;
                completion.TrySetException(new DiscordRpcException((int)(data?.GetLong("code") ?? 0), data?.GetString("message") ?? "Discord error"));
            }
            else
            {
                completion.TrySetResult(payload["data"]?.DeepClone());
            }

            return;
        }

        if (payload.GetString("cmd") == "DISPATCH" && evt is not null)
        {
            EventReceived?.Invoke(evt, payload["data"] as JsonObject ?? []);
        }
    }

    private async Task WriteFrameAsync(int op, JsonObject payload, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<(int Op, JsonObject? Payload)> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await _pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > MaxFrame)
        {
            throw new InvalidDataException("Invalid Discord RPC frame.");
        }

        var body = new byte[length];
        await _pipe.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return (op, length == 0 ? null : JsonNode.Parse(body) as JsonObject);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore.
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch
            {
                // Closed.
            }
        }
    }
}
