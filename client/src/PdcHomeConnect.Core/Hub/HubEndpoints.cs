using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Core.Hub;

/// <summary>Maps the Home Assistant WebSocket endpoint.</summary>
public static class HubEndpoints
{
    public static WebApplication UseHomeConnectHub(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });

        app.Map(ProtocolConstants.WebSocketPath, async (HttpContext context, HubServer server) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket endpoint.").ConfigureAwait(false);
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20),
                KeepAliveTimeout = TimeSpan.FromSeconds(30),
            }).ConfigureAwait(false);
            await server.HandleConnectionAsync(socket, context.Connection.RemoteIpAddress, context.RequestAborted).ConfigureAwait(false);
        });

        app.MapGet("/", () => Results.Json(new
        {
            service = "pdc_homeconnect",
            protocol = ProtocolConstants.Version,
        }));

        return app;
    }
}
