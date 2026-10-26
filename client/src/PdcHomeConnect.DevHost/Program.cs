using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Discovery;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Security;
using PdcHomeConnect.DevHost;

// Development host: runs the real hub/pairing/TLS stack with simulated entities.
// Environment variables:
//   PDC_PORT  (default 48733)   PDC_DATA (data dir)   PDC_MDNS=1 (enable mDNS)   PDC_NAME (device name)
var port = int.TryParse(Environment.GetEnvironmentVariable("PDC_PORT"), out var p) ? p : ProtocolConstants.DefaultPort;
var dataDir = Environment.GetEnvironmentVariable("PDC_DATA") ?? Path.Combine(Path.GetTempPath(), "pdc-homeconnect-devhost");
var deviceName = Environment.GetEnvironmentVariable("PDC_NAME") ?? "DevHost";
Directory.CreateDirectory(dataDir);

var builder = WebApplication.CreateBuilder(args);
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var certificate = CertificateManager.LoadOrCreate(Path.Combine(dataDir, "cert.pfx"), deviceName.ToLowerInvariant(), machineKeySet: false, loggerFactory.CreateLogger("Cert"));
var fingerprint = CertificateManager.Fingerprint(certificate);

var device = new DeviceInfo
{
    Id = "devhost" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deviceName)))[..9],
    Name = deviceName,
    Hostname = deviceName.ToLowerInvariant(),
    Manufacturer = "PD-Codes",
    Model = "Development host",
    Os = "Simulated Windows",
    OsVersion = "10.0.26100",
    IsServer = false,
    Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
    CertSha256 = fingerprint,
    Macs = ["02:00:00:00:00:01"],
    AgentConnected = true,
    AgentUser = "dev",
};

builder.Services.AddSingleton<EntityHub>();
builder.Services.AddSingleton(sp => new TokenStore(Path.Combine(dataDir, "clients.json"), sp.GetRequiredService<ILogger<TokenStore>>()));
builder.Services.AddSingleton<PairingManager>();
builder.Services.AddSingleton(new HubServerOptions { DeviceInfo = () => device });
builder.Services.AddSingleton<HubServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubServer>());
builder.Services.AddSingleton<EntityProvider, SimulatedSystemProvider>();
builder.Services.AddSingleton<EntityProvider, SimulatedUserProvider>();
builder.Services.AddHostedService<ProviderHost>();
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port, listen => listen.UseHttps(certificate)));

var app = builder.Build();
var hub = app.Services.GetRequiredService<EntityHub>();
var pairing = app.Services.GetRequiredService<PairingManager>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

pairing.CodeIssued += code =>
{
    logger.LogWarning("PAIRING CODE for {Client}: {Code}", code.ClientName, code.Display);
    File.WriteAllText(Path.Combine(dataDir, "pairing-code.txt"), code.Code);
};

SimulatedUserProvider.RegisterMethods(hub, logger);
app.UseHomeConnectHub();

MdnsResponder? mdns = null;
if (Environment.GetEnvironmentVariable("PDC_MDNS") == "1")
{
    mdns = new MdnsResponder(new MdnsServiceInfo
    {
        InstanceName = deviceName,
        ServiceType = ProtocolConstants.ServiceType,
        HostName = "pdc-" + deviceName.ToLowerInvariant(),
        Port = (ushort)port,
        Txt = new Dictionary<string, string> { ["id"] = device.Id, ["name"] = deviceName, ["version"] = device.Version, ["protocol"] = "1" },
    }, app.Services.GetRequiredService<ILogger<MdnsResponder>>());
    mdns.Start();
}

logger.LogInformation("DevHost {Name} ({Id}) listening on wss://0.0.0.0:{Port}/ws, fingerprint {Fp}", deviceName, device.Id, port, fingerprint);
await app.RunAsync();
mdns?.Dispose();

/// <summary>Marker for logging categories.</summary>
public partial class Program;
