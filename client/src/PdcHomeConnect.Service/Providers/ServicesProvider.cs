using System.ServiceProcess;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Watched Windows services as switches (start/stop).</summary>
public sealed class ServicesProvider : EntityProvider
{
    private readonly ServiceConfig _config;
    private readonly Dictionary<string, string> _keyToService = new(StringComparer.Ordinal);

    public ServicesProvider(EntityHub hub, ServiceConfig config, ILogger<ServicesProvider> logger)
        : base("services", hub, logger)
    {
        _config = config;
        hub.RegisterMethod("service_control", ServiceControlAsync);
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.Services, _config.WatchedServices.Count > 0))
        {
            return Task.CompletedTask;
        }

        var installed = ServiceController.GetServices().ToDictionary(s => s.ServiceName, StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<EntityDescriptor>();
        foreach (var name in _config.WatchedServices.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!installed.TryGetValue(name, out var controller))
            {
                Logger.LogWarning("Watched service {Name} is not installed", name);
                continue;
            }

            var key = Slug.Key("service", controller.ServiceName);
            _keyToService[key] = controller.ServiceName;
            descriptors.Add(new EntityDescriptor
            {
                Key = key,
                Platform = Platforms.Switch,
                TranslationKey = "windows_service",
                Placeholders = new() { ["service"] = controller.DisplayName },
                Name = $"Service {controller.DisplayName}",
            });
        }

        foreach (var controller in installed.Values)
        {
            controller.Dispose();
        }

        Publish(descriptors);
        if (descriptors.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunPeriodicAsync(TimeSpan.FromSeconds(5), _ =>
        {
            foreach (var (key, name) in _keyToService)
            {
                try
                {
                    using var controller = new ServiceController(name);
                    controller.Refresh();
                    var status = controller.Status;
                    Set(key, status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending, Attrs(
                        ("service_name", name),
                        ("status", status.ToString().ToLowerInvariant()),
                        ("start_type", controller.StartType.ToString().ToLowerInvariant())));
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Service {Name} not readable", name);
                    Hub.SetUnavailable(key);
                }
            }
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (!_keyToService.TryGetValue(command.Key, out var name))
        {
            return CommandResult.Fail(ErrorCodes.NotFound, "Unknown service.");
        }

        return command.Action switch
        {
            Actions.TurnOn => await ControlAsync(name, "start", cancellationToken).ConfigureAwait(false),
            Actions.TurnOff => await ControlAsync(name, "stop", cancellationToken).ConfigureAwait(false),
            _ => CommandResult.NotSupported(command.Action),
        };
    }

    private Task<CommandResult> ServiceControlAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var requested = parameters.GetString("service") ?? string.Empty;
        var name = _keyToService.Values.FirstOrDefault(n => n.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return Task.FromResult(CommandResult.Fail(ErrorCodes.Forbidden, $"Service '{requested}' is not in watched_services."));
        }

        return ControlAsync(name, parameters.GetString("action") ?? string.Empty, cancellationToken);
    }

    private async Task<CommandResult> ControlAsync(string name, string action, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var controller = new ServiceController(name);
            var timeout = TimeSpan.FromSeconds(30);
            switch (action)
            {
                case "start":
                    if (controller.Status != ServiceControllerStatus.Running)
                    {
                        controller.Start();
                        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    }
                    break;
                case "stop":
                    if (controller.Status != ServiceControllerStatus.Stopped)
                    {
                        controller.Stop();
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    }
                    break;
                case "restart":
                    if (controller.Status != ServiceControllerStatus.Stopped)
                    {
                        controller.Stop();
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    }
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    break;
                default:
                    return CommandResult.Fail(ErrorCodes.InvalidParams, $"Unknown action '{action}'.");
            }

            Logger.LogInformation("Service {Name}: {Action}", name, action);
            Set(Slug.Key("service", name), controller.Status == ServiceControllerStatus.Running);
            return CommandResult.Ok();
        }, cancellationToken).ConfigureAwait(false);
    }
}
