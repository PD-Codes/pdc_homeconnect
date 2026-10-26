using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PdcHomeConnect.Core.Hub;

/// <summary>An active pairing code.</summary>
public sealed record PairingCode(string Code, string ClientName, string? RemoteAddress, DateTimeOffset ExpiresAt)
{
    /// <summary>Code formatted for display, e.g. "482 913".</summary>
    public string Display => Code.Length == 6 ? $"{Code[..3]} {Code[3..]}" : Code;
}

public enum PairOutcome
{
    Success,
    InvalidCode,
    NoActiveCode,
    TooManyAttempts,
}

/// <summary>
/// Handles the pairing handshake: Home Assistant requests a code, the PC displays it and the user
/// types it into Home Assistant. A successful pairing yields a long random access token.
/// </summary>
public sealed class PairingManager
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 5;

    private readonly object _gate = new();
    private readonly TokenStore _tokens;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private PairingCode? _current;
    private int _attemptsLeft;

    public PairingManager(TokenStore tokens, ILogger<PairingManager> logger, TimeProvider? time = null)
    {
        _tokens = tokens;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when a new code must be shown to the user.</summary>
    public event Action<PairingCode>? CodeIssued;

    /// <summary>Raised when the active code was used or expired.</summary>
    public event Action? CodeCleared;

    public TokenStore Tokens => _tokens;

    public PairingCode? Current
    {
        get
        {
            lock (_gate)
            {
                ExpireLocked();
                return _current;
            }
        }
    }

    /// <summary>Creates (or re-uses) a pairing code. Re-uses a fresh code to avoid notification spam.</summary>
    public PairingCode RequestCode(string clientName, string? remoteAddress)
    {
        PairingCode code;
        bool isNew;
        lock (_gate)
        {
            ExpireLocked();
            var now = _time.GetUtcNow();
            if (_current is not null && _current.ExpiresAt - now > CodeLifetime - TimeSpan.FromSeconds(60))
            {
                // A code was issued less than a minute ago: show the same one again.
                code = _current;
                isNew = false;
            }
            else
            {
                code = new PairingCode(GenerateCode(), Sanitize(clientName), remoteAddress, now + CodeLifetime);
                _current = code;
                _attemptsLeft = MaxAttempts;
                isNew = true;
            }
        }

        _logger.LogInformation("Pairing requested by {Client} ({Address}), code valid until {Expires:t}", clientName, remoteAddress, code.ExpiresAt.ToLocalTime());
        if (isNew)
        {
            CodeIssued?.Invoke(code);
        }
        return code;
    }

    /// <summary>Validates a code and creates an access token on success.</summary>
    public PairOutcome TryPair(string code, string clientName, string? remoteAddress, out string? token)
    {
        token = null;
        var cleared = false;
        PairOutcome outcome;
        lock (_gate)
        {
            ExpireLocked();
            if (_current is null)
            {
                return PairOutcome.NoActiveCode;
            }

            var normalized = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
            var expected = Encoding.ASCII.GetBytes(_current.Code);
            var given = Encoding.ASCII.GetBytes(normalized);
            if (given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected))
            {
                token = GenerateToken();
                _tokens.Add(Sanitize(clientName), token, remoteAddress);
                _current = null;
                cleared = true;
                outcome = PairOutcome.Success;
            }
            else
            {
                _attemptsLeft--;
                if (_attemptsLeft <= 0)
                {
                    _current = null;
                    cleared = true;
                    outcome = PairOutcome.TooManyAttempts;
                }
                else
                {
                    outcome = PairOutcome.InvalidCode;
                }
            }
        }

        _logger.LogInformation("Pairing attempt from {Address}: {Outcome}", remoteAddress, outcome);
        if (cleared)
        {
            CodeCleared?.Invoke();
        }
        return outcome;
    }

    public PairedClient? ValidateToken(string token, string? remoteAddress) => _tokens.Validate(token, remoteAddress);

    public void Cancel()
    {
        lock (_gate)
        {
            _current = null;
        }
        CodeCleared?.Invoke();
    }

    private void ExpireLocked()
    {
        if (_current is not null && _current.ExpiresAt <= _time.GetUtcNow())
        {
            _current = null;
            ThreadPool.QueueUserWorkItem(_ => CodeCleared?.Invoke());
        }
    }

    private static string GenerateCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Sanitize(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "Home Assistant";
        }
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }
}
