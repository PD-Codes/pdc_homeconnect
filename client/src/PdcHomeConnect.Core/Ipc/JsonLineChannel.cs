using System.Text;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Ipc;

/// <summary>Constants for the local IPC between the service, the tray agent and the hook helper.</summary>
public static class IpcNames
{
    /// <summary>Named pipe hosted by the service; tray agents connect to it.</summary>
    public const string ServicePipe = "PdcHomeConnect.Service";

    /// <summary>Named pipe hosted by the tray agent (per session) for the Claude Code hook helper.</summary>
    public static string AgentHookPipe(int sessionId) => $"PdcHomeConnect.Agent.{sessionId}";
}

/// <summary>Newline-delimited JSON messages over a duplex stream (named pipe).</summary>
public sealed class JsonLineChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly int _maxLineLength;

    public JsonLineChannel(Stream stream, int maxLineLength = 64 * 1024 * 1024)
    {
        _stream = stream;
        _maxLineLength = maxLineLength;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
    }

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Json.Options) + "\n");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Reads the next message. Returns null at end of stream.</summary>
    public async Task<JsonObject?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length > _maxLineLength)
            {
                throw new InvalidDataException("IPC message too large.");
            }

            var message = Json.ParseObject(line);
            if (message is not null)
            {
                return message;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
