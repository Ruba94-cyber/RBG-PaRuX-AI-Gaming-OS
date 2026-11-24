using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace PaRuX.Server.Orchestration.Gaming;

internal sealed record PowerProfileApplyApiRequest(string? PlanId, bool? AllowPowerPlanChange);

internal sealed record PowerPlanDto(string Id, string Name);

internal sealed record PowerProfileStatusDto(bool Applied, string? PreviousPlanId, string? AppliedPlanId, string? ActivePlanId,
    string Message, IReadOnlyList<PowerPlanDto> Available);

/// <summary>Opt-in Console power plan API: desktop owner only, explicit consent in the apply request, restore is always allowed.</summary>
internal static class PowerProfileEndpoints
{
    internal const string StatusRoute = "/api/v1/gaming/power-profile";
    internal const string ApplyRoute = "/api/v1/gaming/power-profile/apply";
    internal const string RestoreRoute = "/api/v1/gaming/power-profile/restore";

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(StatusRoute, (PowerProfileService power) => Results.Ok(ToDto(power.Status()))).RequireDesktopOwner();

        app.MapPost(ApplyRoute, (PowerProfileApplyApiRequest request, PowerProfileService power) =>
        {
            if (request is null || request.AllowPowerPlanChange is null)
                return Results.BadRequest(new { error = "allowPowerPlanChange must be sent explicitly." });
            PowerProfileResult result = power.Apply(request.PlanId, request.AllowPowerPlanChange == true);
            return Reply(result, power);
        }).RequireDesktopOwner();

        // Restore only undoes what PaRuX changed, so it is always allowed.
        app.MapPost(RestoreRoute, (PowerProfileService power) => Reply(power.Restore(), power)).RequireDesktopOwner();
    }

    private static IResult Reply(PowerProfileResult result, PowerProfileService power) => result.Outcome switch
    {
        PowerProfileOutcome.Ok => Results.Ok(ToDto(power.Status(), result.Message)),
        PowerProfileOutcome.Refused => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status409Conflict),
    };

    internal static PowerProfileStatusDto ToDto(PowerProfileStatus s, string? message = null) => new(
        s.Applied, s.PreviousPlanId, s.AppliedPlanId, s.ActivePlanId, message ?? s.Message,
        s.Available.Select(p => new PowerPlanDto(p.Id, p.Name)).ToArray());
}

/// <summary>Restores a power plan a crashed or force-stopped Console session left behind. Never throws, never blocks startup.</summary>
internal static class PowerProfileStartupRecovery
{
    internal static void Run(PowerProfileService power, ILogger logger)
    {
        try
        {
            PowerProfileResult r = power.RecoverAfterCrash();
            if (r.Outcome == PowerProfileOutcome.Failed) logger.LogWarning("Power plan recovery: {Message}", r.Message);
            else logger.LogInformation("Power plan recovery: {Message}", r.Message);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Power plan recovery failed; the restore point stays recorded."); }
    }
}
