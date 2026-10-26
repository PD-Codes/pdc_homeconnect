namespace PdcHomeConnect.Agent;

/// <summary>
/// Shared live signals between modules (used to derive the activity and other combined states).
/// All setters raise <see cref="Changed"/> only when the value actually changed.
/// </summary>
public sealed class AgentSignals
{
    private readonly object _gate = new();
    private bool _locked;
    private bool _afk;
    private bool _gaming;
    private string? _game;
    private string? _foregroundProcess;
    private string _fullscreen = "none";
    private bool _mediaPlaying;
    private bool _claudeWorking;
    private bool _discordVoice;
    private bool _privacy;
    private IReadOnlyList<string> _microphoneApps = [];
    private IReadOnlyList<string> _webcamApps = [];
    private IReadOnlyCollection<string> _runningProcesses = [];

    public event Action? Changed;

    public bool Locked { get => Get(() => _locked); set => SetValue(ref _locked, value); }

    public bool Afk { get => Get(() => _afk); set => SetValue(ref _afk, value); }

    public bool Gaming { get => Get(() => _gaming); set => SetValue(ref _gaming, value); }

    public string? Game { get => Get(() => _game); set => SetValue(ref _game, value); }

    /// <summary>Executable name of the foreground app, e.g. "Code.exe".</summary>
    public string? ForegroundProcess { get => Get(() => _foregroundProcess); set => SetValue(ref _foregroundProcess, value); }

    /// <summary>none, fullscreen, game, presentation, quiet_time, app, away.</summary>
    public string Fullscreen { get => Get(() => _fullscreen); set => SetValue(ref _fullscreen, value); }

    public bool MediaPlaying { get => Get(() => _mediaPlaying); set => SetValue(ref _mediaPlaying, value); }

    public bool ClaudeWorking { get => Get(() => _claudeWorking); set => SetValue(ref _claudeWorking, value); }

    public bool DiscordVoice { get => Get(() => _discordVoice); set => SetValue(ref _discordVoice, value); }

    public bool Privacy { get => Get(() => _privacy); set => SetValue(ref _privacy, value); }

    /// <summary>Executable names of apps currently using the microphone.</summary>
    public IReadOnlyList<string> MicrophoneApps { get => Get(() => _microphoneApps); set => SetList(ref _microphoneApps, value); }

    public IReadOnlyList<string> WebcamApps { get => Get(() => _webcamApps); set => SetList(ref _webcamApps, value); }

    /// <summary>Lower-case executable names running in this session (updated every few seconds).</summary>
    public IReadOnlyCollection<string> RunningProcesses
    {
        get => Get(() => _runningProcesses);
        set
        {
            lock (_gate)
            {
                _runningProcesses = value;
            }
        }
    }

    private T Get<T>(Func<T> getter)
    {
        lock (_gate)
        {
            return getter();
        }
    }

    private void SetValue<T>(ref T field, T value)
    {
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
        }

        Changed?.Invoke();
    }

    private void SetList(ref IReadOnlyList<string> field, IReadOnlyList<string> value)
    {
        lock (_gate)
        {
            if (field.SequenceEqual(value, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            field = value;
        }

        Changed?.Invoke();
    }
}
