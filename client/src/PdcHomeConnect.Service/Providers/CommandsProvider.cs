using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Whitelisted commands that run as SYSTEM (buttons + run_command action).</summary>
public sealed class CommandsProvider : EntityProvider
{
    private readonly ServiceConfig _config;
    private readonly AgentBridge _agents;

    public CommandsProvider(EntityHub hub, ServiceConfig config, AgentBridge agents, ILogger<CommandsProvider> logger)
        : base("commands", hub, logger)
    {
        _config = config;
        _agents = agents;
        hub.RegisterMethod("run_command", RunCommandMethodAsync);
    }

    private IEnumerable<CommandConfig> ValidCommands => _config.Commands.Where(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Command));

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        var descriptors = ValidCommands.Where(c => c.Button).Select(c => new EntityDescriptor
        {
            Key = Slug.Key("scmd", c.Id),
            Platform = Platforms.Button,
            TranslationKey = "command",
            Placeholders = new() { ["command"] = c.Name ?? c.Id },
            Name = c.Name ?? c.Id,
        }).ToList();
        Publish(descriptors);
        foreach (var descriptor in descriptors)
        {
            Set(descriptor.Key, (JsonNode?)null);
        }

        return Task.CompletedTask;
    }

    public override Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        var config = ValidCommands.FirstOrDefault(c => Slug.Key("scmd", c.Id) == command.Key);
        return config is null
            ? Task.FromResult(CommandResult.Fail(ErrorCodes.NotFound, "Unknown command."))
            : RunAsync(config, cancellationToken);
    }

    private async Task<CommandResult> RunCommandMethodAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = parameters.GetString("command") ?? string.Empty;
        var config = ValidCommands.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (config is not null)
        {
            return await RunAsync(config, cancellationToken).ConfigureAwait(false);
        }

        // Not a system command: maybe a user command of the tray agent.
        return await _agents.ForwardCallAsync("run_command", parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> RunAsync(CommandConfig config, CancellationToken cancellationToken)
    {
        var (file, arguments) = CommandLine.Resolve(config.Command, config.Args);
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = config.Wait,
            RedirectStandardError = config.Wait,
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? Path.GetDirectoryName(file) ?? string.Empty : Environment.ExpandEnvironmentVariables(config.WorkingDirectory),
            StandardOutputEncoding = config.Wait ? Encoding.UTF8 : null,
        };

        Logger.LogInformation("Running command {Id}: {File} {Args}", config.Id, file, arguments);
        using var process = Process.Start(start);
        if (process is null)
        {
            return CommandResult.Fail(ErrorCodes.Failed, "Process could not be started.");
        }

        if (!config.Wait)
        {
            return CommandResult.Ok();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 3600)));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited.
            }
            return CommandResult.Fail(ErrorCodes.Timeout, "The command timed out.");
        }

        return CommandResult.Ok(new JsonObject
        {
            ["exit_code"] = process.ExitCode,
            ["output"] = Truncate(await output.ConfigureAwait(false)),
            ["error"] = Truncate(await error.ConfigureAwait(false)),
        });
    }

    private static string Truncate(string text) => text.Length > 4000 ? text[..4000] + "..." : text;
}
