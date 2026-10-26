using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Hub;

/// <summary>A Home Assistant instance that was paired with this PC.</summary>
public sealed class PairedClient
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    /// <summary>SHA-256 (hex) of the access token. The token itself is never stored.</summary>
    public required string TokenSha256 { get; init; }

    public DateTimeOffset Created { get; init; }

    public DateTimeOffset? LastSeen { get; set; }

    public string? LastAddress { get; set; }
}

/// <summary>Persists paired clients (token hashes) in a JSON file.</summary>
public sealed class TokenStore
{
    private sealed class FileModel
    {
        public List<PairedClient> Clients { get; set; } = [];
    }

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private List<PairedClient> _clients = [];

    public TokenStore(string? path, ILogger<TokenStore> logger, TimeProvider? time = null)
    {
        _path = path;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Load();
    }

    public event Action? Changed;

    public IReadOnlyList<PairedClient> Clients
    {
        get
        {
            lock (_gate)
            {
                return _clients.ToList();
            }
        }
    }

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public PairedClient Add(string name, string token, string? address)
    {
        var client = new PairedClient
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(name) ? "Home Assistant" : name.Trim(),
            TokenSha256 = HashToken(token),
            Created = _time.GetUtcNow(),
            LastSeen = _time.GetUtcNow(),
            LastAddress = address,
        };

        lock (_gate)
        {
            _clients.Add(client);
            Save();
        }

        Changed?.Invoke();
        return client;
    }

    public PairedClient? Validate(string token, string? address)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Encoding.ASCII.GetBytes(HashToken(token));
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(client.TokenSha256)))
                {
                    var now = _time.GetUtcNow();
                    var shouldSave = client.LastSeen is null || now - client.LastSeen > TimeSpan.FromHours(1) || client.LastAddress != address;
                    client.LastSeen = now;
                    client.LastAddress = address;
                    if (shouldSave)
                    {
                        Save();
                    }
                    return client;
                }
            }
        }

        return null;
    }

    public bool RemoveByToken(string token)
    {
        var hash = HashToken(token);
        bool removed;
        lock (_gate)
        {
            removed = _clients.RemoveAll(c => c.TokenSha256 == hash) > 0;
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }
        return removed;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _clients.RemoveAll(c => c.Id == id) > 0;
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }
        return removed;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _clients.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json.Options);
            _clients = model?.Clients ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read paired clients from {Path}", _path);
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Clients = _clients }, Json.Indented));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save paired clients to {Path}", _path);
        }
    }
}
