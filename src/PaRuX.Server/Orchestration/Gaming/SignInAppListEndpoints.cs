using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Owner-only API of the after-sign-in app list. Reads work while Gaming; every change and run is refused while
/// Gaming is active.
/// </summary>
public static class SignInAppListEndpoints
{
    public static void MapSignInAppListEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/sign-in-apps", (SignInAppListStore store, SignInAppListRunner runner) =>
            Results.Ok(BuildStatus(store.Load(), runner))).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/sign-in-apps", (
            SignInAppListSaveRequest request, SignInAppListStore store, SignInAppListRunner runner, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Leave Gaming before editing the after-sign-in list." });
            if (request is null) return Results.BadRequest(new { error = "A list is required." });
            var (state, error) = store.Save(request);
            return state is null
                ? Results.BadRequest(new { error = error ?? "The list could not be saved." })
                : Results.Ok(BuildStatus(state, runner));
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(SignInAppListPolicy.MaxFileBytes * 2));

        // The shell calls this once after it started; the decision (enabled, Gaming, once per sign-in) is made here.
        app.MapPost("/api/v1/gaming/sign-in-apps/trigger", (SignInTriggerRequest request, SignInAppListRunner runner) =>
            request is null ? Results.BadRequest(new { error = "A request is required." }) : Results.Ok(runner.Trigger(request)))
            .RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/sign-in-apps/run", (SignInAppListRunner runner, LocalRuntimeService runtime) =>
            runtime.GamingMode
                ? Results.Conflict(new { error = "Leave Gaming before running the after-sign-in list." })
                : Results.Ok(runner.RunNow())).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/sign-in-apps/cancel", (SignInAppListRunner runner) =>
        {
            runner.Cancel();
            return Results.Ok(runner.Snapshot());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/sign-in-apps/shell-state", (SignInShellStateRequest request, SignInAppListRunner runner) =>
        {
            if (request is null) return Results.BadRequest(new { error = "A state is required." });
            runner.ConsoleActive = request.ConsoleActive;
            return Results.NoContent();
        }).RequireDesktopOwner();

    }

    private static SignInAppListStatus BuildStatus(SignInAppListState state, SignInAppListRunner runner) => new(
        state.List.Enabled,
        state.List.Settings,
        state.List.Apps.Select(e => new SignInAppEntryStatus(e, SignInAppListPolicy.ValidateEntry(e))).ToArray(),
        runner.Snapshot(),
        state.LastRunSessionKey,
        state.Damaged,
        SignInAppListPolicy.MaxApps);
}
