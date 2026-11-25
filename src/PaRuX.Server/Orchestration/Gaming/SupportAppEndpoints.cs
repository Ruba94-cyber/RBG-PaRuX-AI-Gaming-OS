using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Owner-only API of the support apps. Reads work while Gaming; every change, download, removal and start is refused while Gaming is
/// active. A browser session (web client) has no access to any of these routes.
/// </summary>
public static class SupportAppEndpoints
{
    public static void MapSupportAppEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/support-apps", (SupportAppStore store, SupportAppRunner runner) =>
            Results.Ok(BuildStatus(store.Load(), runner))).RequireDesktopOwner();

        app.MapGet("/api/v1/gaming/support-apps/catalog", () => Results.Ok(SupportAppCatalog.All)).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/support-apps", (SupportAppSaveRequest request, SupportAppStore store, SupportAppRunner runner, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Leave Gaming before editing the support apps." });
            if (request is null) return Results.BadRequest(new { error = "A list is required." });
            var (state, error) = store.Save(request);
            return state is null ? Results.BadRequest(new { error = error ?? "The list could not be saved." }) : Results.Ok(BuildStatus(state, runner));
        }).RequireDesktopOwner().WithMetadata(new RequestSizeLimitAttribute(SupportAppLimits.MaxFileBytes * 2));

        app.MapPost("/api/v1/gaming/support-apps/start", async (SupportAppStore store, SupportAppRunner runner, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Leave Gaming before starting the support apps." });
            await runner.StartAsync(ct);
            return Results.Ok(BuildStatus(store.Load(), runner));
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/support-apps/stop", async (SupportAppStore store, SupportAppRunner runner, CancellationToken ct) =>
        {
            await runner.StopAsync(ct);
            return Results.Ok(BuildStatus(store.Load(), runner));
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/support-apps/install", async (SupportAppInstallRequest request, SupportAppInstaller installer, SupportAppStore store, SupportAppRunner runner, CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "A request is required." });
            SupportAppInstallResult r = await installer.InstallAsync(request, ct);
            return Results.Ok(new SupportAppActionResult(r.Ok, r.Message, BuildStatus(store.Load(), runner)));
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/support-apps/remove", (SupportAppRemoveRequest request, SupportAppInstaller installer, SupportAppStore store, SupportAppRunner runner, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Leave Gaming before removing a support app." });
            if (request is null) return Results.BadRequest(new { error = "A request is required." });
            SupportAppInstallResult r = installer.Remove(request.CatalogId);
            return Results.Ok(new SupportAppActionResult(r.Ok, r.Message, BuildStatus(store.Load(), runner)));
        }).RequireDesktopOwner();
    }

    private static SupportAppStatus BuildStatus(SupportAppListState state, SupportAppRunner runner)
    {
        string root = SupportAppPolicy.ManagedRoot;
        return new SupportAppStatus(
            state.Enabled,
            state.Apps.Select(e => new SupportAppRow(e, SupportAppPolicy.OwnershipOf(e, root), SupportAppPolicy.Validate(e), runner.IsRunningNow(e))).ToArray(),
            runner.LastRun, state.Damaged, SupportAppLimits.MaxApps, root);
    }
}
