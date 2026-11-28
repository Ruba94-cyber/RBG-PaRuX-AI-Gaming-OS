using System.Net;
using PaRuX.Contracts;

namespace PaRuX.Server.Tunnel;

/// <summary>
/// The Internet boundary. cloudflared connects only to a dedicated loopback port (<see cref="TunnelListener.Port"/>), so the
/// local port a connection arrived on - not a header - decides that a request came through the tunnel. Cf-Connecting-Ip,
/// X-Forwarded-* and every other client-supplied header are ignored for every trust decision.
///
/// A tunnel request is never loopback-trusted, never the desktop owner, never a browser session and never an API key. It may
/// reach only <see cref="TunnelEndpointPolicy"/> routes, and everything except the two pairing steps needs a valid paired-device
/// token. Runs before the Host-header check, the browser door and the global authentication gate (which skip tunnel requests).
/// </summary>
internal sealed class TunnelBoundaryMiddleware
{
    internal const string ItemKey = "PaRuXViaTunnel";
    private readonly RequestDelegate _next;

    public TunnelBoundaryMiddleware(RequestDelegate next) => _next = next;

    internal static bool IsTunnel(HttpContext context) => context.Items.ContainsKey(ItemKey);

    public async Task InvokeAsync(HttpContext context, RemoteTunnelService tunnel, RemoteAccessService remote)
    {
        int port = tunnel.ListenerPort;
        if (port <= 0 || context.Connection.LocalPort != port)
        {
            await _next(context);
            return;
        }

        context.Items[ItemKey] = true;
        context.Response.Headers.CacheControl = "no-store";

        if (!tunnel.AcceptsTraffic)
        {
            await Refuse(context, StatusCodes.Status503ServiceUnavailable, "The Internet tunnel is off.");
            return;
        }
        // Only the current public host. A request that reaches the port with any other Host (a local process, a rebinding
        // attempt, a stale tunnel name) is refused.
        if (!tunnel.IsAcceptedHost(context.Request.Headers.Host.ToString()))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "This Host name is not allowed.");
            return;
        }
        // The Android client is not a browser. A page in someone's browser must never be able to use a paired device.
        if (context.Request.Headers.ContainsKey("Origin"))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "Browser-origin requests are not allowed through the tunnel.");
            return;
        }
        if (!TunnelEndpointPolicy.IsAllowed(context.Request.Method, context.Request.Path))
        {
            await Refuse(context, StatusCodes.Status403Forbidden,
                "This route is not available through the Internet tunnel. Only the paired-device API is.");
            return;
        }
        if (TunnelEndpointPolicy.IsPairingStep(context.Request.Method, context.Request.Path))
        {
            await _next(context);
            return;
        }

        string token = context.Request.Headers[PaRuXHeaders.DeviceToken].ToString();
        if (!remote.TryValidateDeviceToken(token, out string deviceId, out bool expired))
        {
            if (expired) context.Response.Headers["X-PaRuX-Pairing"] = "expired";
            await Refuse(context, StatusCodes.Status401Unauthorized,
                expired ? "This device's pairing has expired. Pair it again from PaRuX on the PC." : "A paired device token is required.");
            return;
        }
        context.Items["PaRuXOwner"] = "device:" + deviceId;
        await _next(context);
    }

    private static async Task Refuse(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}

internal static class TunnelEndpoints
{
    internal static void MapTunnelEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/remote/tunnel/status", (RemoteTunnelService tunnel, HttpContext context) =>
            DesktopOwnerRouteExtensions.IsDesktopOwner(context) && !TunnelBoundaryMiddleware.IsTunnel(context)
                ? Results.Ok(tunnel.GetStatus()) : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapPost("/api/v1/remote/tunnel/config", async (RemoteTunnelConfigRequest request, RemoteTunnelService tunnel,
            HttpContext context, CancellationToken ct) =>
        {
            if (!IsOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                RemoteTunnelStatus status = await tunnel.ConfigureAsync(request, ct);
                AuditLogService.TryRecord("remote.tunnel", request.Enabled switch { true => "enabled", false => "disabled", _ => "configured" });
                return Results.Ok(status);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapPost("/api/v1/remote/tunnel/install", async (RemoteTunnelService tunnel, HttpContext context, CancellationToken ct) =>
        {
            if (!IsOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                await tunnel.InstallComponentAsync(ct);
                AuditLogService.TryRecord("remote.tunnel", "cloudflared installed");
                return Results.Ok(tunnel.GetStatus());
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway); }
        });

        app.MapPost("/api/v1/remote/tunnel/locate", (RemoteTunnelLocateRequest request, RemoteTunnelService tunnel, HttpContext context) =>
        {
            if (!IsOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                CloudflaredComponent.Locate(request.Path);
                AuditLogService.TryRecord("remote.tunnel", "cloudflared located");
                return Results.Ok(tunnel.GetStatus());
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v1/remote/tunnel/skip", (RemoteTunnelService tunnel, HttpContext context) =>
        {
            if (!IsOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            CloudflaredComponent.Skip();
            return Results.Ok(tunnel.GetStatus());
        });

        app.MapPost("/api/v1/remote/tunnel/pending/{id}/approve", (string id, RemoteTunnelService tunnel, HttpContext context) =>
        {
            if (!IsOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            bool ok = tunnel.Broker.Approve(id);
            if (ok) AuditLogService.TryRecord("remote.pairing", "approved through tunnel");
            return ok ? Results.NoContent() : Results.NotFound(new { error = "That pairing request is no longer waiting." });
        });

        app.MapPost("/api/v1/remote/tunnel/pending/{id}/deny", (string id, RemoteTunnelService tunnel, HttpContext context) =>
        {
            if (!IsOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            bool ok = tunnel.Broker.Deny(id);
            if (ok) AuditLogService.TryRecord("remote.pairing", "denied through tunnel");
            return ok ? Results.NoContent() : Results.NotFound(new { error = "That pairing request is no longer waiting." });
        });

        // The phone collects its token here after the owner approved. Reachable only through the tunnel; the boundary has already
        // required the tunnel Host, no Origin, and counts bad secrets against the tunnel pairing lockout.
        app.MapPost("/api/v1/remote/pair/poll", (PairingPollRequest request, RemoteTunnelService tunnel, HttpContext context) =>
        {
            if (!TunnelBoundaryMiddleware.IsTunnel(context)) return Results.NotFound();
            try
            {
                switch (tunnel.Broker.Poll(request, out PairingCompleteResponse? result))
                {
                    case TunnelPollOutcome.Approved:
                        AuditLogService.TryRecord("remote.pairing", "completed through tunnel");
                        return Results.Ok(result);
                    case TunnelPollOutcome.Denied:
                        return Results.Json(new { error = "The PC owner declined this pairing.", status = "denied" }, statusCode: StatusCodes.Status403Forbidden);
                    case TunnelPollOutcome.Pending:
                        return Results.Json(new { status = "pending" }, statusCode: StatusCodes.Status202Accepted);
                    default:
                        return Results.Json(new { error = "Unknown or expired pairing request.", status = "unknown" }, statusCode: StatusCodes.Status404NotFound);
                }
            }
            catch (RemoteAccessService.PairingLockedException ex) { return Locked(context, ex); }
        });
    }

    /// <summary>Tunnel branch of POST /api/v1/remote/pair/complete.</summary>
    internal static IResult CompleteThroughTunnel(PairingCompleteRequest request, RemoteTunnelService tunnel, HttpContext context)
    {
        try
        {
            PairingApprovalPending pending = tunnel.Broker.RequestApproval(request);
            AuditLogService.TryRecord("remote.pairing", "approval requested through tunnel");
            return Results.Json(pending, statusCode: StatusCodes.Status202Accepted);
        }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests); }
        catch (RemoteAccessService.PairingLockedException ex) { return Locked(context, ex); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static bool IsOwner(HttpContext context) =>
        DesktopOwnerRouteExtensions.IsDesktopOwner(context) && !TunnelBoundaryMiddleware.IsTunnel(context);

    private static IResult Locked(HttpContext context, RemoteAccessService.PairingLockedException ex)
    {
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(ex.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
}
