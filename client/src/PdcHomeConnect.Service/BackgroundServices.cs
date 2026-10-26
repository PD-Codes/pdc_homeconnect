using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Discovery;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using PdcHomeConnect.Service.Native;

namespace PdcHomeConnect.Service;

/// <summary>Starts the tray agent in user sessions where it is not running (crash recovery, after updates).</summary>
public sealed class AgentWatchdog : BackgroundService
{
    private readonly AgentBridge _bridge;
    private readonly SessionTracker _sessions;
    private readonly ServiceConfig _config;
    private readonly ILogger _logger;
    private readonly Dictionary<int, DateTimeOffset> _firstSeen = [];
    private readonly Dictionary<int, DateTimeOffset> _lastLaunch = [];

    public AgentWatchdog(AgentBridge bridge, SessionTracker sessions, ServiceConfig config, ILogger<AgentWatchdog> logger)
    {
        _bridge = bridge;
        _sessions = sessions;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.AgentWatchdog || !File.Exists(ServicePaths.AgentExecutable))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Watchdog check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
    }

    private void Check()
    {
        var now = DateTimeOffset.UtcNow;
        var active = _sessions.UserSessions.Where(s => s.State == Win32.ConnectState.Active).ToList();
        foreach (var stale in _firstSeen.Keys.Where(id => active.All(s => s.Id != id)).ToList())
        {
            _firstSeen.Remove(stale);
            _lastLaunch.Remove(stale);
            _bridge.ForgetExit(stale);
        }

        foreach (var session in active)
        {
            if (!_firstSeen.TryGetValue(session.Id, out var firstSeen))
            {
                _firstSeen[session.Id] = now;
                continue;
            }

            // Give the Run key a minute to start the agent after sign-in.
            if (now - firstSeen < TimeSpan.FromSeconds(60) || _bridge.HasAgent(session.Id) || _bridge.UserExited(session.Id))
            {
                continue;
            }

            if (_lastLaunch.TryGetValue(session.Id, out var last) && now - last < TimeSpan.FromMinutes(5))
            {
                continue;
            }

            _lastLaunch[session.Id] = now;
            try
            {
                var pid = Win32.StartInSession((uint)session.Id, ServicePaths.AgentExecutable, "--autostart");
                _logger.LogInformation("Started tray agent in session {Session} (pid {Pid})", session.Id, pid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not start the tray agent in session {Session}: {Message}", session.Id, ex.Message);
            }
        }
    }
}

/// <summary>Shows pairing codes: tray agents (notification), a file and the event log.</summary>
public sealed class PairingNotifier : IHostedService
{
    private const string EventSource = "PDC HomeConnect";
    private readonly PairingManager _pairing;
    private readonly AgentBridge _bridge;
    private readonly ServicePaths _paths;
    private readonly ILogger _logger;

    public PairingNotifier(PairingManager pairing, AgentBridge bridge, ServicePaths paths, ILogger<PairingNotifier> logger)
    {
        _pairing = pairing;
        _bridge = bridge;
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pairing.CodeIssued += OnCodeIssued;
        _pairing.CodeCleared += OnCodeCleared;
        TryDelete();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pairing.CodeIssued -= OnCodeIssued;
        _pairing.CodeCleared -= OnCodeCleared;
        TryDelete();
        return Task.CompletedTask;
    }

    private void OnCodeIssued(PairingCode code)
    {
        _ = _bridge.BroadcastAsync(AgentBridge.PairingMessage(code));
        try
        {
            File.WriteAllText(_paths.PairingCode, $"{code.Display}\r\nRequested by: {code.ClientName} ({code.RemoteAddress})\r\nValid until: {code.ExpiresAt.ToLocalTime():HH:mm:ss}\r\n");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write the pairing code file");
        }

        try
        {
            if (!System.Diagnostics.EventLog.SourceExists(EventSource))
            {
                System.Diagnostics.EventLog.CreateEventSource(EventSource, "Application");
            }

            System.Diagnostics.EventLog.WriteEntry(EventSource, $"Home Assistant pairing code: {code.Display} (requested by {code.ClientName} from {code.RemoteAddress}, valid for 5 minutes).", EventLogEntryType.Information, 100);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write the pairing code to the event log");
        }
    }

    private void OnCodeCleared()
    {
        _ = _bridge.BroadcastAsync(new JsonObject { ["type"] = "pairing_cleared" });
        TryDelete();
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_paths.PairingCode);
        }
        catch
        {
            // Ignore.
        }
    }
}

/// <summary>Maintains the Windows Firewall rules for the WebSocket port and mDNS.</summary>
public sealed class FirewallManager : IHostedService
{
    private const string RuleTcp = "PDC HomeConnect (Home Assistant)";
    private const string RuleMdns = "PDC HomeConnect (mDNS discovery)";
    private readonly ServiceConfig _config;
    private readonly ILogger _logger;

    public FirewallManager(ServiceConfig config, ILogger<FirewallManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.ManageFirewall)
        {
            return Task.CompletedTask;
        }

        try
        {
            var remote = _config.FirewallScope.Equals("any", StringComparison.OrdinalIgnoreCase) ? "*" : "LocalSubnet";
            EnsureRule(RuleTcp, 6, _config.Port.ToString(), remote);
            if (_config.Discovery)
            {
                EnsureRule(RuleMdns, 17, "5353", "LocalSubnet");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not configure the Windows Firewall");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void EnsureRule(string name, int protocol, string port, string remote)
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("Firewall API unavailable");
        dynamic policy = Activator.CreateInstance(policyType)!;
        dynamic rules = policy.Rules;
        var program = ServicePaths.ServiceExecutable;

        try
        {
            dynamic existing = rules.Item(name);
            if ((int)existing.Protocol == protocol &&
                string.Equals((string)existing.LocalPorts, port, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)existing.RemoteAddresses, remote == "*" ? "*" : "LocalSubnet", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)existing.ApplicationName, program, StringComparison.OrdinalIgnoreCase) &&
                (bool)existing.Enabled)
            {
                return;
            }
        }
        catch
        {
            // Rule does not exist yet.
        }

        for (var i = 0; i < 5; i++)
        {
            try
            {
                rules.Remove(name);
            }
            catch
            {
                break;
            }
        }

        var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")!;
        dynamic rule = Activator.CreateInstance(ruleType)!;
        rule.Name = name;
        rule.Description = "Allows Home Assistant to connect to PDC HomeConnect.";
        rule.ApplicationName = program;
        rule.Protocol = protocol;
        rule.LocalPorts = port;
        rule.RemoteAddresses = remote;
        rule.Direction = 1; // inbound
        rule.Action = 1;    // allow
        rule.Enabled = true;
        rule.Profiles = 0x7FFFFFFF;
        rule.Grouping = "PDC HomeConnect";
        rules.Add(rule);
        _logger.LogInformation("Firewall rule {Name} created ({Port}, {Remote})", name, port, remote);
    }
}

/// <summary>Advertises the PC via mDNS and handles sleep/resume announcements.</summary>
public sealed class DiscoveryService : IHostedService, IDisposable
{
    private readonly ServiceConfig _config;
    private readonly DeviceInfoService _device;
    private readonly SystemEvents _events;
    private readonly HubServer _server;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private MdnsResponder? _responder;

    public DiscoveryService(ServiceConfig config, DeviceInfoService device, SystemEvents events, HubServer server, ILoggerFactory loggerFactory)
    {
        _config = config;
        _device = device;
        _events = events;
        _server = server;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DiscoveryService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _events.Power += OnPower;
        if (!_config.Discovery)
        {
            return Task.CompletedTask;
        }

        _responder = new MdnsResponder(new MdnsServiceInfo
        {
            InstanceName = _device.Name,
            ServiceType = ProtocolConstants.ServiceType,
            HostName = "pdc-" + Slug.From(_device.Hostname, 40).Replace('_', '-'),
            Port = (ushort)_config.Port,
            Txt = new Dictionary<string, string>
            {
                ["id"] = _device.DeviceId,
                ["name"] = _device.Name,
                ["version"] = _device.Version,
                ["protocol"] = ProtocolConstants.Version.ToString(),
            },
        }, _loggerFactory.CreateLogger<MdnsResponder>());
        _responder.Start();
        return Task.CompletedTask;
    }

    private void OnPower(PowerBroadcastStatus status)
    {
        switch (status)
        {
            case PowerBroadcastStatus.Suspend:
                _logger.LogInformation("System is suspending");
                _server.SendByeAsync("sleep", TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
                _responder?.SendGoodbye();
                break;
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeSuspend:
                _logger.LogInformation("System resumed");
                _ = Task.Run(async () =>
                {
                    // The network needs a moment after resume.
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    _responder?.Announce();
                });
                break;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Power -= OnPower;
        _responder?.Dispose();
        _responder = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _responder?.Dispose();
}

/// <summary>Restarts the service when service.json changes.</summary>
public sealed class ConfigWatcher : IHostedService
{
    private readonly ServicePaths _paths;
    private readonly SystemEvents _events;
    private readonly ILogger _logger;
    private IDisposable? _watch;
    private string _hash = string.Empty;

    public ConfigWatcher(ServicePaths paths, SystemEvents events, ILogger<ConfigWatcher> logger)
    {
        _paths = paths;
        _events = events;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hash = Hash();
        _watch = JsonConfig.Watch(_paths.Config, () =>
        {
            var hash = Hash();
            if (hash == _hash)
            {
                return;
            }

            _hash = hash;
            _logger.LogInformation("service.json changed, restarting the service");
            _events.RequestRestart();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watch?.Dispose();
        return Task.CompletedTask;
    }

    private string Hash()
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_paths.Config)));
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>Downloads and installs the latest client release from GitHub (update entity in Home Assistant).</summary>
public sealed class ClientUpdater
{
    private readonly ServicePaths _paths;
    private readonly DeviceInfoService _device;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientUpdater(EntityHub hub, ServicePaths paths, DeviceInfoService device, ILogger<ClientUpdater> logger)
    {
        _paths = paths;
        _device = device;
        _logger = logger;
        hub.RegisterMethod("update_client", (parameters, ct) => UpdateAsync(ct));
    }

    private async Task<CommandResult> UpdateAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return CommandResult.Fail(ErrorCodes.Failed, "An update is already running.");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"PDC-HomeConnect/{_device.Version}");
            var release = await http.GetFromJsonAsync<JsonObject>(
                $"https://api.github.com/repos/{ProtocolConstants.GitHubOwner}/{ProtocolConstants.GitHubRepo}/releases/latest",
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("No release found.");

            var tag = (release.GetString("tag_name") ?? string.Empty).TrimStart('v', 'V');
            if (Version.TryParse(tag, out var latest) && Version.TryParse(_device.Version, out var current) && latest <= current)
            {
                return CommandResult.Ok(new JsonObject { ["status"] = "up_to_date", ["version"] = tag });
            }

            var assets = release.GetArray("assets")?.OfType<JsonObject>().ToList() ?? [];
            var installer = assets.FirstOrDefault(a => (a.GetString("name") ?? string.Empty).StartsWith("PDC-HomeConnect-Setup", StringComparison.OrdinalIgnoreCase) &&
                                                       (a.GetString("name") ?? string.Empty).EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var sums = assets.FirstOrDefault(a => (a.GetString("name") ?? string.Empty).Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (installer is null || sums is null)
            {
                return CommandResult.Fail(ErrorCodes.NotFound, "The release has no installer or checksum file.");
            }

            var fileName = installer.GetString("name")!;
            var target = Path.Combine(_paths.Updates, fileName);
            var checksums = await http.GetStringAsync(sums.GetString("browser_download_url"), cancellationToken).ConfigureAwait(false);
            var expected = checksums.Split('\n')
                .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length >= 2 && p[^1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))?[0];
            if (expected is null)
            {
                return CommandResult.Fail(ErrorCodes.Failed, "No checksum for the installer.");
            }

            var bytes = await http.GetByteArrayAsync(installer.GetString("browser_download_url"), cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail(ErrorCodes.Failed, "Checksum mismatch, update aborted.");
            }

            await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Installing update {Version}", tag);
            Process.Start(new ProcessStartInfo(target, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=\"{Path.Combine(_paths.Logs, "update.log")}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _paths.Updates,
            });
            return CommandResult.Ok(new JsonObject { ["status"] = "installing", ["version"] = tag });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update failed");
            return CommandResult.Fail(ErrorCodes.Failed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}
