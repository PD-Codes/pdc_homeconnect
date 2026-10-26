using System.Text.Json.Nodes;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>Default audio output/input device as select entities (switches the Windows default device).</summary>
public sealed class AudioDevicesModule : EntityProvider, IDisposable
{
    private readonly AgentContext _context;
    private readonly AsyncSignal _changed = new();
    private AudioEndpoints? _endpoints;
    private List<string> _outputOptions = [];
    private List<string> _inputOptions = [];
    private Dictionary<string, string> _outputIds = [];
    private Dictionary<string, string> _inputIds = [];

    public AudioDevicesModule(AgentContext context, ILogger<AudioDevicesModule> logger)
        : base("audio_devices", context.Hub, logger)
    {
        _context = context;
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _endpoints = new AudioEndpoints();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Audio devices are not available");
            return;
        }

        _endpoints.Changed += _changed.Set;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Audio device refresh failed");
            }

            // Debounce bursts of notifications; refresh at least every 5 minutes.
            await _changed.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refresh()
    {
        var endpoints = _endpoints!;
        var outputs = endpoints.List(EDataFlow.Render);
        var inputs = endpoints.List(EDataFlow.Capture);
        var (outputOptions, outputIds) = Options(outputs);
        var (inputOptions, inputIds) = Options(inputs);
        if (!outputOptions.SequenceEqual(_outputOptions) || !inputOptions.SequenceEqual(_inputOptions))
        {
            _outputOptions = outputOptions;
            _inputOptions = inputOptions;
            var descriptors = new List<EntityDescriptor>();
            if (outputOptions.Count > 0)
            {
                descriptors.Add(new() { Key = "audio_output", Platform = Platforms.Select, TranslationKey = "audio_output", Name = "Audio output", Options = outputOptions });
            }

            if (inputOptions.Count > 0)
            {
                descriptors.Add(new() { Key = "audio_input", Platform = Platforms.Select, TranslationKey = "audio_input", Name = "Audio input", Options = inputOptions });
            }

            Publish(descriptors);
        }

        _outputIds = outputIds;
        _inputIds = inputIds;
        var output = endpoints.Default(EDataFlow.Render);
        var input = endpoints.Default(EDataFlow.Capture);
        Set("audio_output", NameOf(output, outputIds));
        Set("audio_input", NameOf(input, inputIds));
    }

    private static string? NameOf(AudioEndpoint? endpoint, Dictionary<string, string> ids) =>
        endpoint is null ? null : ids.FirstOrDefault(p => p.Value == endpoint.Id).Key;

    /// <summary>Unique option labels (duplicate names get a counter).</summary>
    private static (List<string> Options, Dictionary<string, string> Ids) Options(IReadOnlyList<AudioEndpoint> endpoints)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = endpoint.Name;
            var counter = 2;
            while (ids.ContainsKey(name))
            {
                name = $"{endpoint.Name} ({counter++})";
            }

            ids[name] = endpoint.Id;
        }

        return (ids.Keys.ToList(), ids);
    }

    public override Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (command.Action != Actions.SelectOption)
        {
            return Task.FromResult(CommandResult.NotSupported(command.Action));
        }

        var option = command.Params.GetString("option") ?? string.Empty;
        var ids = command.Key == "audio_input" ? _inputIds : _outputIds;
        if (!ids.TryGetValue(option, out var id))
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.InvalidParams, $"Unknown audio device '{option}'."));
        }

        try
        {
            AudioEndpoints.SetDefault(id);
            Set(command.Key, option);
            return Task.FromResult(CommandResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.Failed, ex.Message));
        }
    }

    public void Dispose()
    {
        _endpoints?.Dispose();
        _changed.Set();
    }
}
