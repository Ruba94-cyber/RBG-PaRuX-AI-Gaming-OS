using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace PaRuX.Server.Orchestration.Gaming;

internal sealed record StreamingDisplayRequest(string? DisplayId, string? Kind, string? Value);

/// <summary>Both consent flags must be present in the request; a missing flag is a 400, never an implied yes or no.</summary>
internal sealed record StreamingStartApiRequest(bool? AllowStartSunshine, bool? AllowDisplayChange, StreamingDisplayRequest? Display);

internal sealed record StreamingStepDto(string Name, string Outcome, string Detail);

internal sealed record StreamingStatusDto(string State, string Message, bool SunshineOwnedByPaRuX, bool DisplayRestorePending,
    IReadOnlyList<StreamingStepDto> Steps);

/// <summary>Streaming mode API: desktop owner only, explicit consent in the request, refused to start while Gaming owns the machine.</summary>
internal static class StreamingEndpoints
{
    internal const string StatusRoute = "/api/v1/gaming/streaming";
    internal const string StartRoute = "/api/v1/gaming/streaming/start";
    internal const string StopRoute = "/api/v1/gaming/streaming/stop";

    internal static void Map(IEndpointRouteBuilder app, Func<bool> gamingActive, Func<bool> displayChangePending)
    {
        app.MapGet(StatusRoute, (StreamingModeService streaming) => Results.Ok(ToDto(streaming.Status()))).RequireDesktopOwner();

        app.MapPost(StartRoute, async (StreamingStartApiRequest request, StreamingModeService streaming, CancellationToken ct) =>
        {
            if (request is null || request.AllowStartSunshine is null || request.AllowDisplayChange is null)
                return Results.BadRequest(new { error = "allowStartSunshine and allowDisplayChange must both be sent explicitly." });
            if (!TryParseDisplay(request.Display, out StreamingDisplayTarget? target, out string? invalid))
                return Results.BadRequest(new { error = invalid });
            if (target is not null && request.AllowDisplayChange != true)
                return Results.BadRequest(new { error = "A display change was requested but allowDisplayChange is false." });
            if (gamingActive())
                return Results.Conflict(new { error = "Leave Gaming before starting streaming mode." });
            if (displayChangePending())
                return Results.Conflict(new { error = "Confirm or revert the pending display change first." });
            try
            {
                StreamingStatus status = await streaming.StartAsync(
                    new StreamingStartRequest(request.AllowStartSunshine == true, target, request.AllowDisplayChange == true), ct);
                // A failed start has already been rolled back and is reported honestly with its step list.
                return status.State == StreamingState.Failed ? Results.Json(ToDto(status), statusCode: StatusCodes.Status409Conflict) : Results.Ok(ToDto(status));
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        // Stop is always allowed (it only undoes what PaRuX did), including while Gaming.
        app.MapPost(StopRoute, async (StreamingModeService streaming) =>
        {
            try { return Results.Ok(ToDto(await streaming.StopAsync())); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireDesktopOwner();
    }

    internal static bool TryParseDisplay(StreamingDisplayRequest? d, out StreamingDisplayTarget? target, out string? error)
    {
        target = null; error = null;
        if (d is null) return true;
        string id = d.DisplayId?.Trim() ?? string.Empty, value = d.Value?.Trim() ?? string.Empty;
        if (id.Length is 0 or > 64 || value.Length is 0 or > 32) { error = "display.displayId and display.value are required (displayId up to 64, value up to 32 characters)."; return false; }
        if (!Enum.TryParse(d.Kind, ignoreCase: true, out DisplayChangeKind kind) || !Enum.IsDefined(kind)
            || !d.Kind!.All(char.IsLetter)) { error = "display.kind must be Resolution or RefreshRate."; return false; }
        if (kind == DisplayChangeKind.Hdr) { error = "HDR control is not implemented."; return false; }
        target = new StreamingDisplayTarget(id, kind, value);
        return true;
    }

    internal static StreamingStatusDto ToDto(StreamingStatus s) => new(
        s.State.ToString(), s.Message, s.SunshineOwnedByPaRuX, s.DisplayRestorePending,
        s.Steps.Select(x => new StreamingStepDto(x.Name, x.Outcome.ToString(), x.Detail)).ToArray());
}

internal enum StreamingRecoveryOutcome { Clean, Incomplete, TimedOut, Failed }

/// <summary>Startup recovery of display changes a crashed streaming session left behind: off the caller's thread, bounded, never throws.</summary>
internal static class StreamingStartupRecovery
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<StreamingRecoveryOutcome> RunAsync(StreamingModeService streaming, ILogger logger, TimeSpan timeout)
    {
        try
        {
            DisplayRecoveryReport report = await Task.Run(streaming.RecoverAfterCrash).WaitAsync(timeout).ConfigureAwait(false);
            if (report.Restored + report.Discarded + report.Failed > 0 || report.JournalWasCorrupt)
                logger.LogWarning("Streaming display recovery: restored {Restored}, discarded {Discarded}, failed {Failed}, corrupt journal {Corrupt}.",
                    report.Restored, report.Discarded, report.Failed, report.JournalWasCorrupt);
            else
                logger.LogInformation("Streaming display recovery: nothing to restore.");
            return report.Clean ? StreamingRecoveryOutcome.Clean : StreamingRecoveryOutcome.Incomplete;
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Streaming display recovery did not finish within {Seconds} s; restore points stay recorded.", (int)timeout.TotalSeconds);
            return StreamingRecoveryOutcome.TimedOut;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Streaming display recovery failed; restore points stay recorded.");
            return StreamingRecoveryOutcome.Failed;
        }
    }
}
