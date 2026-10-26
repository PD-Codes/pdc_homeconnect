using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Service.Providers;

/// <summary>IIS web sites as switches (via appcmd.exe).</summary>
public sealed class IisProvider : EntityProvider
{
    private static readonly string AppCmd = Path.Combine(Environment.SystemDirectory, "inetsrv", "appcmd.exe");
    private readonly ServiceConfig _config;
    private readonly Dictionary<string, string> _sites = new(StringComparer.Ordinal);
    private string _signature = string.Empty;

    public IisProvider(EntityHub hub, ServiceConfig config, ILogger<IisProvider> logger)
        : base("iis", hub, logger)
    {
        _config = config;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.Iis, File.Exists(AppCmd)) || !File.Exists(AppCmd))
        {
            return Task.CompletedTask;
        }

        return RunPeriodicAsync(TimeSpan.FromSeconds(30), async ct =>
        {
            var output = await RunAppCmdAsync("list site /xml", ct).ConfigureAwait(false);
            var sites = XDocument.Parse(output).Root?.Elements("SITE")
                .Select(e => (Name: (string?)e.Attribute("SITE.NAME") ?? "site", State: (string?)e.Attribute("state") ?? "Unknown", Bindings: (string?)e.Attribute("bindings")))
                .ToList() ?? [];

            var signature = string.Join('|', sites.Select(s => s.Name));
            if (signature != _signature)
            {
                _sites.Clear();
                var descriptors = new List<EntityDescriptor>();
                foreach (var site in sites)
                {
                    var key = Slug.Key("iis", site.Name);
                    _sites[key] = site.Name;
                    descriptors.Add(new() { Key = key, Platform = Platforms.Switch, TranslationKey = "iis_site", Placeholders = new() { ["site"] = site.Name }, Name = $"IIS {site.Name}" });
                }
                Publish(descriptors);
                _signature = signature;
            }

            foreach (var site in sites)
            {
                Set(Slug.Key("iis", site.Name), site.State.Equals("Started", StringComparison.OrdinalIgnoreCase), Attrs(("state", site.State.ToLowerInvariant()), ("bindings", site.Bindings)));
            }
        }, cancellationToken);
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (!_sites.TryGetValue(command.Key, out var site))
        {
            return CommandResult.Fail(ErrorCodes.NotFound, "Unknown site.");
        }

        var verb = command.Action switch
        {
            Actions.TurnOn => "start",
            Actions.TurnOff => "stop",
            _ => null,
        };
        if (verb is null)
        {
            return CommandResult.NotSupported(command.Action);
        }

        await RunAppCmdAsync($"{verb} site /site.name:\"{site.Replace("\"", string.Empty)}\"", cancellationToken).ConfigureAwait(false);
        Set(command.Key, verb == "start");
        return CommandResult.Ok();
    }

    private static async Task<string> RunAppCmdAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(AppCmd, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return output;
    }
}

/// <summary>Docker containers as switches (Docker Engine / Docker Desktop named pipe).</summary>
public sealed class DockerProvider : EntityProvider
{
    private const string PipeName = "docker_engine";
    private readonly ServiceConfig _config;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _containers = new(StringComparer.Ordinal);
    private string _signature = string.Empty;

    public DockerProvider(EntityHub hub, ServiceConfig config, ILogger<DockerProvider> logger)
        : base("docker", hub, logger)
    {
        _config = config;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
                return pipe;
            },
        })
        {
            BaseAddress = new Uri("http://localhost/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static bool PipeExists()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(300);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.Docker, true))
        {
            return;
        }

        await RunPeriodicAsync(TimeSpan.FromSeconds(15), async ct =>
        {
            if (!PipeExists())
            {
                if (_containers.Count > 0)
                {
                    foreach (var key in _containers.Keys)
                    {
                        Hub.SetUnavailable(key);
                    }
                }
                return;
            }

            var list = await _http.GetFromJsonAsync<JsonArray>("containers/json?all=1", ct).ConfigureAwait(false) ?? [];
            var containers = list.OfType<JsonObject>()
                .Select(c => (
                    Id: c.GetString("Id") ?? string.Empty,
                    Name: (c["Names"] as JsonArray)?.FirstOrDefault()?.ToString().TrimStart('/') ?? "container",
                    State: c.GetString("State") ?? "unknown",
                    Status: c.GetString("Status"),
                    Image: c.GetString("Image")))
                .Where(c => c.Id.Length > 0)
                .ToList();

            var signature = string.Join('|', containers.Select(c => c.Name).Order());
            if (signature != _signature)
            {
                _containers.Clear();
                var descriptors = new List<EntityDescriptor>();
                foreach (var container in containers)
                {
                    var key = Slug.Key("docker", container.Name);
                    _containers[key] = container.Name;
                    descriptors.Add(new() { Key = key, Platform = Platforms.Switch, TranslationKey = "docker_container", Placeholders = new() { ["container"] = container.Name }, Name = $"Container {container.Name}" });
                }
                Publish(descriptors);
                _signature = signature;
            }

            foreach (var container in containers)
            {
                Set(Slug.Key("docker", container.Name), container.State == "running", Attrs(("state", container.State), ("status", container.Status), ("image", container.Image)));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (!_containers.TryGetValue(command.Key, out var name))
        {
            return CommandResult.Fail(ErrorCodes.NotFound, "Unknown container.");
        }

        var verb = command.Action switch
        {
            Actions.TurnOn => "start",
            Actions.TurnOff => "stop",
            _ => null,
        };
        if (verb is null)
        {
            return CommandResult.NotSupported(command.Action);
        }

        using var response = await _http.PostAsync($"containers/{Uri.EscapeDataString(name)}/{verb}", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 304)
        {
            return CommandResult.Fail(ErrorCodes.Failed, $"Docker returned {(int)response.StatusCode}.");
        }

        Set(command.Key, verb == "start");
        return CommandResult.Ok();
    }
}

/// <summary>SMB sessions and open files.</summary>
public sealed class SmbProvider : EntityProvider
{
    private const string Namespace = @"root\Microsoft\Windows\SMB";
    private readonly ServiceConfig _config;
    private readonly DeviceInfoService _device;

    public SmbProvider(EntityHub hub, ServiceConfig config, DeviceInfoService device, ILogger<SmbProvider> logger)
        : base("smb", hub, logger)
    {
        _config = config;
        _device = device;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (!ModuleState.IsOn(_config.Modules.Smb, _device.IsServer && HasUserShares()))
        {
            return Task.CompletedTask;
        }

        Publish(
        [
            new() { Key = "smb_sessions", Platform = Platforms.Sensor, TranslationKey = "smb_sessions", Name = "SMB sessions", StateClass = Ha.Measurement, Precision = 0 },
            new() { Key = "smb_open_files", Platform = Platforms.Sensor, TranslationKey = "smb_open_files", Name = "SMB open files", StateClass = Ha.Measurement, Precision = 0 },
        ]);

        return RunPeriodicAsync(TimeSpan.FromSeconds(30), _ =>
        {
            var sessions = new JsonArray();
            using (var searcher = new ManagementObjectSearcher(Namespace, "SELECT ClientComputerName, ClientUserName, NumOpens FROM MSFT_SmbSession"))
            {
                foreach (var session in searcher.Get())
                {
                    sessions.Add(new JsonObject
                    {
                        ["user"] = session["ClientUserName"] as string,
                        ["computer"] = session["ClientComputerName"] as string,
                        ["open_files"] = Convert.ToInt32(session["NumOpens"] ?? 0),
                    });
                }
            }

            var files = new JsonArray();
            var fileCount = 0;
            using (var searcher = new ManagementObjectSearcher(Namespace, "SELECT Path, ClientUserName FROM MSFT_SmbOpenFile"))
            {
                foreach (var file in searcher.Get())
                {
                    fileCount++;
                    if (files.Count < 20)
                    {
                        files.Add(new JsonObject { ["path"] = file["Path"] as string, ["user"] = file["ClientUserName"] as string });
                    }
                }
            }

            Set("smb_sessions", (long)sessions.Count, new JsonObject { ["sessions"] = sessions });
            Set("smb_open_files", (long)fileCount, new JsonObject { ["files"] = files });
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private static bool HasUserShares()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, "SELECT Name FROM MSFT_SmbShare WHERE Special = FALSE");
            return searcher.Get().Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
