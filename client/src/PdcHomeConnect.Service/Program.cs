using Microsoft.Extensions.Hosting.WindowsServices;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Security;
using PdcHomeConnect.Core.Util;
using PdcHomeConnect.Service;
using PdcHomeConnect.Service.Providers;
using Serilog;
using Serilog.Events;

// PDC HomeConnect Windows service (LocalSystem). Installed and started by the setup; never shows a window.
var paths = new ServicePaths();
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .WriteTo.File(
        Path.Combine(paths.Logs, "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    using var bootstrapFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger));
    var bootstrapLogger = bootstrapFactory.CreateLogger("Startup");
    var config = JsonConfig.Load<ServiceConfig>(paths.Config, ServiceConfig.DefaultTemplate(), bootstrapLogger);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
    builder.Host.UseWindowsService(options => options.ServiceName = "PdcHomeConnect");
    if (WindowsServiceHelpers.IsWindowsService())
    {
        builder.Services.AddSingleton<IHostLifetime, PdcServiceLifetime>();
    }

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();

    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<SystemEvents>();
    builder.Services.AddSingleton<DeviceInfoService>();
    builder.Services.AddSingleton<SessionTracker>();
    builder.Services.AddSingleton<EntityHub>();
    builder.Services.AddSingleton(sp => new TokenStore(paths.Clients, sp.GetRequiredService<ILogger<TokenStore>>()));
    builder.Services.AddSingleton<PairingManager>();
    builder.Services.AddSingleton(sp =>
    {
        var device = sp.GetRequiredService<DeviceInfoService>();
        return new HubServerOptions { DeviceInfo = () => device.Current };
    });
    builder.Services.AddSingleton<HubServer>();
    builder.Services.AddSingleton<AgentBridge>();
    builder.Services.AddSingleton<ClientUpdater>();

    builder.Services.AddSingleton<EntityProvider, SystemProvider>();
    builder.Services.AddSingleton<EntityProvider, HardwareProvider>();
    builder.Services.AddSingleton<EntityProvider, PowerProvider>();
    builder.Services.AddSingleton<EntityProvider, SessionProvider>();
    builder.Services.AddSingleton<EntityProvider, MaintenanceProvider>();
    builder.Services.AddSingleton<EntityProvider, ServicesProvider>();
    builder.Services.AddSingleton<EntityProvider, HyperVProvider>();
    builder.Services.AddSingleton<EntityProvider, EventLogProvider>();
    builder.Services.AddSingleton<EntityProvider, ScheduledTasksProvider>();
    builder.Services.AddSingleton<EntityProvider, BackupProvider>();
    builder.Services.AddSingleton<EntityProvider, StorageHealthProvider>();
    builder.Services.AddSingleton<EntityProvider, IisProvider>();
    builder.Services.AddSingleton<EntityProvider, DockerProvider>();
    builder.Services.AddSingleton<EntityProvider, SmbProvider>();
    builder.Services.AddSingleton<EntityProvider, PlexProvider>();
    builder.Services.AddSingleton<EntityProvider, CommandsProvider>();

    builder.Services.AddHostedService<FirewallManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HubServer>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentBridge>());
    builder.Services.AddHostedService<PairingNotifier>();
    builder.Services.AddHostedService<ProviderHost>();
    builder.Services.AddHostedService<DiscoveryService>();
    builder.Services.AddHostedService<AgentWatchdog>();
    builder.Services.AddHostedService<ConfigWatcher>();

    var certificate = CertificateManager.LoadOrCreate(paths.Certificate, Environment.MachineName, machineKeySet: true, bootstrapLogger);
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.AddServerHeader = false;
        kestrel.ListenAnyIP(config.Port, listen => listen.UseHttps(certificate));
    });

    var app = builder.Build();
    var device = app.Services.GetRequiredService<DeviceInfoService>();
    device.CertificateFingerprint = CertificateManager.Fingerprint(certificate);

    // Resolve services that register methods in their constructors.
    app.Services.GetRequiredService<ClientUpdater>();
    app.Services.GetRequiredService<AgentBridge>();

    app.UseHomeConnectHub();
    Log.Information("PDC HomeConnect {Version} starting: {Name} ({Id}) on port {Port}", device.Version, device.Name, device.DeviceId, config.Port);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PDC HomeConnect service terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
