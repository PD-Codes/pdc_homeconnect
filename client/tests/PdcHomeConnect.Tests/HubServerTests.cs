using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Security;

namespace PdcHomeConnect.Tests;

/// <summary>End-to-end test of the WebSocket hub over TLS: pairing, auth, snapshot, commands, blobs.</summary>
public class HubServerTests : IAsyncLifetime
{
    private WebApplication? _app;
    private int _port;
    private string _fingerprint = string.Empty;
    private EntityHub _hub = null!;
    private PairingManager _pairing = null!;

    public async Task InitializeAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pdc-hubtest-{Guid.NewGuid():N}");
        var cert = CertificateManager.LoadOrCreate(Path.Combine(dir, "cert.pfx"), "testhost", false, NullLogger.Instance);
        _fingerprint = CertificateManager.Fingerprint(cert);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<EntityHub>();
        builder.Services.AddSingleton(sp => new TokenStore(null, NullLogger<TokenStore>.Instance));
        builder.Services.AddSingleton<PairingManager>();
        builder.Services.AddSingleton(new HubServerOptions
        {
            DeviceInfo = () => new DeviceInfo { Id = "dev1", Name = "Test PC", Version = "0.1.0", CertSha256 = _fingerprint },
            FlushDelay = TimeSpan.FromMilliseconds(10),
        });
        builder.Services.AddSingleton<HubServer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HubServer>());
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));

        _app = builder.Build();
        _app.UseHomeConnectHub();
        await _app.StartAsync();
        _port = new Uri(_app.Urls.First()).Port;
        _hub = _app.Services.GetRequiredService<EntityHub>();
        _pairing = _app.Services.GetRequiredService<PairingManager>();
        _hub.SetDescriptors("svc", [new EntityDescriptor { Key = "cpu_load", Platform = Platforms.Sensor, Unit = "%" }, new EntityDescriptor { Key = "lock", Platform = Platforms.Button }]);
        _hub.SetState("cpu_load", JsonValue.Create(12));
        _hub.RegisterCommandHandler("svc", new LockHandler());
        _hub.SetBlob("cover", [1, 2, 3], "image/png");
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())) == _fingerprint;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{_port}/ws"), CancellationToken.None);
        return ws;
    }

    private static async Task SendAsync(ClientWebSocket ws, JsonObject msg) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject> ReceiveAsync(ClientWebSocket ws, string? type = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var buffer = new byte[1 << 20];
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            var msg = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!.AsObject();
            if (type is null || msg["type"]!.GetValue<string>() == type)
            {
                return msg;
            }
        }
    }

    [Fact]
    public async Task Full_pairing_and_command_flow()
    {
        using var ws = await ConnectAsync();
        var hello = await ReceiveAsync(ws, "hello");
        Assert.Equal(1, hello["protocol"]!.GetValue<int>());
        Assert.Equal(_fingerprint, hello["device"]!["cert_sha256"]!.GetValue<string>());

        await SendAsync(ws, new JsonObject { ["type"] = "command", ["id"] = 1, ["key"] = "lock", ["action"] = "press" });
        var unauthorized = await ReceiveAsync(ws, "error");
        Assert.Equal("unauthorized", unauthorized["error"]!["code"]!.GetValue<string>());

        await SendAsync(ws, new JsonObject { ["type"] = "pair_request", ["client_name"] = "pytest" });
        await ReceiveAsync(ws, "pair_pending");
        var code = _pairing.Current!.Code;

        await SendAsync(ws, new JsonObject { ["type"] = "pair", ["code"] = "000000" == code ? "111111" : "000000" });
        Assert.Equal("invalid_code", (await ReceiveAsync(ws, "pair_failed"))["reason"]!.GetValue<string>());

        await SendAsync(ws, new JsonObject { ["type"] = "pair", ["code"] = code, ["client_name"] = "pytest" });
        var ok = await ReceiveAsync(ws, "pair_ok");
        var token = ok["token"]!.GetValue<string>();
        var entities = await ReceiveAsync(ws, "entities");
        Assert.Equal(2, entities["entities"]!.AsArray().Count);
        var states = await ReceiveAsync(ws, "states");
        Assert.Equal(12, states["states"]!["cpu_load"]!["state"]!.GetValue<int>());

        await SendAsync(ws, new JsonObject { ["type"] = "command", ["id"] = 7, ["key"] = "lock", ["action"] = "press" });
        var result = await ReceiveAsync(ws, "result");
        Assert.Equal(7, result["id"]!.GetValue<int>());
        Assert.True(result["success"]!.GetValue<bool>());

        _hub.SetState("cpu_load", JsonValue.Create(55));
        var delta = await ReceiveAsync(ws, "states");
        Assert.Equal(55, delta["states"]!["cpu_load"]!["state"]!.GetValue<int>());

        await SendAsync(ws, new JsonObject { ["type"] = "get_blob", ["id"] = 9, ["key"] = "cover" });
        var blob = await ReceiveAsync(ws, "result");
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), blob["data"]!["data"]!.GetValue<string>());

        // A second connection authenticates with the token.
        using var ws2 = await ConnectAsync();
        await ReceiveAsync(ws2, "hello");
        await SendAsync(ws2, new JsonObject { ["type"] = "auth", ["token"] = token });
        await ReceiveAsync(ws2, "auth_ok");
        await ReceiveAsync(ws2, "entities");

        using var ws3 = await ConnectAsync();
        await ReceiveAsync(ws3, "hello");
        await SendAsync(ws3, new JsonObject { ["type"] = "auth", ["token"] = "wrong" });
        Assert.Equal("invalid_token", (await ReceiveAsync(ws3, "auth_failed"))["reason"]!.GetValue<string>());
    }

    private sealed class LockHandler : ICommandHandler
    {
        public Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(command.Action == "press" ? CommandResult.Ok() : CommandResult.NotSupported(command.Action));
    }
}
