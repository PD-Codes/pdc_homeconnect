using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Hub;

/// <summary>One WebSocket connection from a Home Assistant instance.</summary>
internal sealed class ClientConnection
{
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(20_000)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private int _queued;

    public ClientConnection(WebSocket socket, string? remoteAddress)
    {
        Socket = socket;
        RemoteAddress = remoteAddress;
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];

    public WebSocket Socket { get; }

    public string? RemoteAddress { get; }

    public DateTimeOffset ConnectedAt { get; }

    public string? ClientName { get; set; }

    public string? ClientVersion { get; set; }

    public PairedClient? Client { get; set; }

    public bool IsAuthenticated => Client is not null;

    public DateTimeOffset LastPairRequest { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Queues a message. Returns false if the client is too slow (queue full).</summary>
    public bool TryEnqueue(string json)
    {
        if (Volatile.Read(ref _queued) > 15_000)
        {
            return false;
        }

        if (_outbox.Writer.TryWrite(json))
        {
            Interlocked.Increment(ref _queued);
            return true;
        }
        return false;
    }

    public bool TryEnqueue(JsonObject message) => TryEnqueue(message.ToJsonString(Json.Options));

    public void Complete() => _outbox.Writer.TryComplete();

    public async Task RunSenderAsync(CancellationToken cancellationToken)
    {
        await foreach (var json in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _queued);
            if (Socket.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Receives one complete text message. Returns null when the peer closed the connection.</summary>
    public async Task<string?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await Socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                stream.Write(buffer, 0, result.Count);
                if (stream.Length > maxBytes)
                {
                    throw new InvalidDataException($"Message exceeds {maxBytes} bytes.");
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Abort()
    {
        try
        {
            Socket.Abort();
        }
        catch
        {
            // Ignore: the socket is already gone.
        }
    }
}
