using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>
/// User commands (run as the signed-in user, defined in agent.json) as buttons and for
/// pdc_homeconnect.run_command, plus the open_url and set_clipboard actions.
/// </summary>
public sealed class CommandsModule : EntityProvider
{
    private readonly AgentContext _context;

    public CommandsModule(AgentContext context, ILogger<CommandsModule> logger)
        : base("user_commands", context.Hub, logger)
    {
        _context = context;
    }

    private IEnumerable<UserCommandConfig> Commands =>
        _context.Config.Commands.Where(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Command));

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        var descriptors = Commands.Where(c => c.Button).Select(c => new EntityDescriptor
        {
            Key = Slug.Key("cmd", c.Id),
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

        Hub.RegisterMethod("run_command", RunCommandMethodAsync);
        Hub.RegisterMethod("open_url", OpenUrlAsync);
        Hub.RegisterMethod("set_clipboard", SetClipboardAsync);
        return Task.CompletedTask;
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        var config = Commands.FirstOrDefault(c => Slug.Key("cmd", c.Id) == command.Key);
        if (config is null || command.Action != Actions.Press)
        {
            return CommandResult.NotSupported(command.Action);
        }

        return await RunAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> RunCommandMethodAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = parameters.GetString("command") ?? string.Empty;
        var config = Commands.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || Slug.From(c.Id) == Slug.From(id));
        if (config is null)
        {
            // Only whitelisted commands can run: Home Assistant can never execute arbitrary command lines.
            return CommandResult.Fail(ErrorCodes.NotFound, $"Unknown command '{id}'. Define it in agent.json (commands) or service.json.");
        }

        return await RunAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> RunAsync(UserCommandConfig config, CancellationToken cancellationToken)
    {
        var (file, arguments) = CommandLine.Resolve(config.Command, config.Args);
        var startInfo = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Environment.ExpandEnvironmentVariables(config.WorkingDirectory),
        };

        var hidden = config.Window.Equals("hidden", StringComparison.OrdinalIgnoreCase);
        if (config.Wait && hidden)
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }
        else
        {
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = config.Window.ToLowerInvariant() switch
            {
                "normal" => ProcessWindowStyle.Normal,
                "minimized" => ProcessWindowStyle.Minimized,
                "maximized" => ProcessWindowStyle.Maximized,
                _ => ProcessWindowStyle.Hidden,
            };
        }

        Logger.LogInformation("Running user command {Id}", config.Id);
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ErrorCodes.Failed, $"Could not start '{config.Command}': {ex.Message}");
        }

        if (process is null)
        {
            // Shell execute handed over to an already running app.
            return CommandResult.Ok();
        }

        using (process)
        {
            if (!config.Wait)
            {
                return CommandResult.Ok(new JsonObject { ["pid"] = process.Id });
            }

            var output = startInfo.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
            var error = startInfo.RedirectStandardError ? process.StandardError.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 3600)));
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
    }

    private static string Truncate(string text) => text.Length > 4000 ? text[..4000] + "..." : text;

    private Task<CommandResult> OpenUrlAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var url = parameters.GetString("url");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.InvalidParams, "Invalid URL."));
        }

        if (!_context.Config.AllowedUrlSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.Forbidden, $"The URL scheme '{uri.Scheme}' is not allowed (allowed_url_schemes in agent.json)."));
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return Task.FromResult(CommandResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.Failed, ex.Message));
        }
    }

    private async Task<CommandResult> SetClipboardAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var text = parameters.GetString("text") ?? string.Empty;
        // The clipboard needs an STA thread: use the UI thread.
        await _context.Ui.InvokeAsync(() =>
        {
            if (text.Length == 0)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetText(text);
            }
        }).ConfigureAwait(false);
        return CommandResult.Ok();
    }
}
