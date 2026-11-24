using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace PaRuX.Server.Orchestration.Gaming;

internal sealed record GameBarApplyApiRequest(bool? AllowGameBarChange, bool? Permanent);

internal sealed record GameBarRestoreApiRequest(bool? IncludePermanent);

/// <summary>Xbox Game Bar block API: desktop owner only, explicit consent in the apply request, restore is always allowed.</summary>
internal static class GameBarEndpoints
{
    internal const string StatusRoute = "/api/v1/gaming/game-bar";
    internal const string ApplyRoute = "/api/v1/gaming/game-bar/apply";
    internal const string RestoreRoute = "/api/v1/gaming/game-bar/restore";

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(StatusRoute, (GameBarSuppressionService svc) => Results.Ok(svc.Status())).RequireDesktopOwner();

        app.MapPost(ApplyRoute, (GameBarApplyApiRequest request, GameBarSuppressionService svc) =>
        {
            if (request is null || request.AllowGameBarChange is null)
                return Results.BadRequest(new { error = "allowGameBarChange must be sent explicitly." });
            return Reply(svc.Apply(request.AllowGameBarChange == true, request.Permanent == true), svc);
        }).RequireDesktopOwner();

        // Restore only undoes what PaRuX changed. A permanent block is released only with includePermanent=true.
        app.MapPost(RestoreRoute, (GameBarRestoreApiRequest? request, GameBarSuppressionService svc) =>
            Reply(svc.Restore(request?.IncludePermanent == true), svc)).RequireDesktopOwner();
    }

    private static IResult Reply(GameBarResult result, GameBarSuppressionService svc) => result.Outcome switch
    {
        GameBarOutcome.Ok => Results.Ok(new { message = result.Message, status = svc.Status() }),
        GameBarOutcome.Refused => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status409Conflict),
    };
}

/// <summary>Restores Game Bar values a crashed or force-stopped Console session left behind. Never throws, never blocks startup.</summary>
internal static class GameBarStartupRecovery
{
    internal static void Run(GameBarSuppressionService svc, ILogger logger)
    {
        try
        {
            GameBarResult r = svc.RecoverAfterCrash();
            if (r.Outcome == GameBarOutcome.Failed) logger.LogWarning("Game Bar recovery: {Message}", r.Message);
            else logger.LogInformation("Game Bar recovery: {Message}", r.Message);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Game Bar recovery failed; the restore point stays recorded."); }
    }
}
