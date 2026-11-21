using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Owner-only boundary for generic "prompt to mod" change sets. Chat/agent code cannot reach apply or
/// restore: both need the desktop owner and an explicit consent flag for the reviewed files.
/// </summary>
public static class GameModChangeSetEndpoints
{
    public static void MapGameModChangeSetEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/mods/changesets/candidates", (string gameId, string? folder,
            GameModChangeSetService service) => Guard(() => Results.Ok(service.ListCandidates(gameId, folder))))
            .RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/mods/changesets/prompt", async (
            GameModPromptRequest request, GameModChangeSetService service,
            LocalRuntimeService runtime, CancellationToken ct) =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.GamingCancellationToken);
                return Results.Ok(await service.ProposeFromPromptAsync(request, linked.Token)) as IResult;
            }
            catch (OperationCanceledException)
            { return Results.Conflict(new { error = "The AI proposal was interrupted by Gaming Mode or cancellation. No game file was changed." }); }
            catch (Exception ex) { return Map(ex); }
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(32_768));

        app.MapGet("/api/v1/gaming/mods/changesets/pending", (GameModChangeSetService service) =>
            Guard(() => Results.Ok(service.ListPending()))).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/mods/changesets/apply", (
            GameModChangeSetApplyRequest request, GameModChangeSetService service) =>
            Guard(() => Results.Ok(service.Apply(request))))
            .RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(16_384));

        app.MapGet("/api/v1/gaming/mods/changesets/backups", (GameModChangeSetService service) =>
            Guard(() => Results.Ok(service.ListBackups()))).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/mods/changesets/restore", (
            GameModChangeSetRestoreRequest request, GameModChangeSetService service) =>
            Guard(() => Results.Ok(service.Restore(request))))
            .RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(8_192));
    }

    private static IResult Guard(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception ex) { return Map(ex); }
    }

    /// <summary>Refusals are client errors with the honest reason; only unexpected I/O faults stay 500.</summary>
    internal static IResult Map(Exception ex) => ex switch
    {
        ArgumentException => Results.BadRequest(new { error = ex.Message }),
        InvalidOperationException => Results.Conflict(new { error = ex.Message }),
        InvalidDataException => Results.BadRequest(new { error = ex.Message }),
        PlatformNotSupportedException => Results.Conflict(new { error = ex.Message }),
        IOException or UnauthorizedAccessException or System.Security.SecurityException =>
            Results.Problem("PaRuX could not safely read or write the game files. Backups were kept; nothing was deleted."),
        _ => throw ex,
    };
}
