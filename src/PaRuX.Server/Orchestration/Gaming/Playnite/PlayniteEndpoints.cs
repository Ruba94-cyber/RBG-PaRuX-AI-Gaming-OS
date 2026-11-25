using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>Pure admission policy: no frontend focus stealing during a tracked game.</summary>
internal static class PlayniteForegroundAdmission
{
    internal static PlayniteLaunchResultDto? Check(bool gameRunning, bool gamingHardOff = false) =>
        (gameRunning || gamingHardOff)
            ? new PlayniteLaunchResultDto
            {
                Ok = false,
                Outcome = "refused",
                Message = "A game is running. Finish playing before opening Playnite again."
            }
            : null;
}

public static class PlayniteEndpoints
{
    /// <summary>Registers the Playnite component services. The watcher itself is added by the caller outside of test hosts.</summary>
    public static IServiceCollection AddPlayniteIntegration(this IServiceCollection services)
    {
        services.AddSingleton<PlayniteSettingsStore>();
        services.AddSingleton<IPlayniteEnvironment, WindowsPlayniteEnvironment>();
        services.AddSingleton<PlayniteLocator>();
        services.AddSingleton<IProcessSnapshotSource, WindowsProcessSnapshotSource>();
        services.AddSingleton<IPlayniteProcessHost, WindowsPlayniteProcessHost>();
        services.AddSingleton(sp => new PlayniteLauncher(
            sp.GetRequiredService<IPlayniteProcessHost>(), sp.GetRequiredService<IPlayniteEnvironment>(),
            id => sp.GetRequiredService<OwnedGameWindowPlacementService>().HasConnectedTarget(id)));
        services.AddSingleton<IPlayniteYieldTarget>(sp => new OrchestratorYieldTarget(sp.GetRequiredService<WorkloadOrchestrator>()));
        services.AddSingleton(sp => new PlayniteInstaller(new HttpClient { Timeout = TimeSpan.FromMinutes(20) }));
        services.AddSingleton<PlayniteSessionService>();
        services.AddSingleton<PlayniteThemeStore>();
        services.AddSingleton(sp => new PlayniteThemeService(new HttpClient { Timeout = TimeSpan.FromMinutes(30) },
            sp.GetRequiredService<IPlayniteEnvironment>(), sp.GetRequiredService<PlayniteThemeStore>(),
            () => sp.GetRequiredService<PlayniteSettingsStore>().LocatedFolder));
        return services;
    }

    public static IServiceCollection AddPlayniteSessionWatcher(this IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<PlayniteSessionService>());

    public static void MapPlayniteEndpoints(this WebApplication app)
    {
        const string root = "/api/v1/gaming/playnite";

        // ?live=true: the return watch needs a fresh "is Playnite still running" (the cached detection can be ~20 s old).
        app.MapGet(root + "/status", (PlayniteSessionService session, PlayniteSettingsStore settings, PlayniteInstaller installer, bool live = false) =>
        {
            bool refresh = live || installer.Status.Stage == "completed";
            return Results.Ok(new PlayniteStatusDto
            {
                Component = session.Component(refresh),
                Settings = settings.Get(),
                Session = session.GetSession()
            });
        }).RequireDesktopOwner();

        app.MapGet(root + "/settings", (PlayniteSettingsStore settings) => Results.Ok(settings.Get())).RequireDesktopOwner();

        app.MapPut(root + "/settings", (PlayniteSettingsDto request, PlayniteSettingsStore settings) =>
        {
            // "Skipped" has its own endpoints so a settings save can never silently skip or un-skip the component.
            PlayniteSettingsDto current = settings.Get();
            if (request is null) return Results.BadRequest(new { error = "No settings were sent." });
            request.Skipped = current.Skipped;
            (PlayniteSettingsDto? saved, string? error) = settings.Save(request);
            return saved is null ? Results.BadRequest(new { error }) : Results.Ok(saved);
        }).RequireDesktopOwner();

        app.MapPost(root + "/locate", (PlayniteLocateRequest request, PlayniteLocator locator, PlayniteSettingsStore settings, PlayniteSessionService session) =>
        {
            string? input = request?.Path;
            if (string.IsNullOrWhiteSpace(input)) return Results.BadRequest(new { error = "Choose the Playnite folder." });
            // Accept the folder or one of the two official executables inside it.
            string candidate = input.Trim().Trim('"');
            try
            {
                string fileName = Path.GetFileName(candidate);
                if (fileName.Equals(PlayniteLocator.DesktopExeName, StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(PlayniteLocator.FullscreenExeName, StringComparison.OrdinalIgnoreCase))
                    candidate = Path.GetDirectoryName(Path.GetFullPath(candidate)) ?? candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { return Results.BadRequest(new { error = "That path is not valid." }); }

            PlayniteComponentDto? found = locator.FromFolder(candidate, "located", "user-located");
            if (found is null)
                return Results.BadRequest(new { error = "No Playnite.DesktopApp.exe or Playnite.FullscreenApp.exe was found in that folder." });
            if (!settings.SetLocatedFolder(found.Folder)) return Results.Problem("The location could not be saved.");
            settings.SetSkipped(false);
            return Results.Ok(session.Component(refresh: true));
        }).RequireDesktopOwner();

        app.MapPost(root + "/skip", (PlayniteSettingsStore settings, PlayniteSessionService session) =>
            settings.SetSkipped(true) ? Results.Ok(session.Component(true)) : Results.Problem("The choice could not be saved."))
            .RequireDesktopOwner();

        app.MapPost(root + "/unskip", (PlayniteSettingsStore settings, PlayniteSessionService session) =>
            settings.SetSkipped(false) ? Results.Ok(session.Component(true)) : Results.Problem("The choice could not be saved."))
            .RequireDesktopOwner();

        app.MapPost(root + "/launch", (PlayniteLaunchRequest? request, PlayniteSessionService session,
            PlayniteLauncher launcher, PlayniteSettingsStore settings, PaRuX.Server.LocalRuntimeService runtime) =>
        {
            // One frontend per game session: launching/focusing Playnite during
            // a tracked game would steal focus and could disrupt controller input.
            // Apply this at the owner-only Core boundary as well as in the UI,
            // since shortcuts and controller hotkeys also reach this endpoint.
            PlayniteLaunchResultDto? blocked = PlayniteForegroundAdmission.Check(session.GameRunning, runtime.GamingMode);
            if (blocked is not null) return Results.Conflict(blocked);
            PlayniteLaunchResultDto result = launcher.Launch(session.Component(true), settings.Get(), request?.Mode, request?.DisplayId, request?.Explicit == true);
            return result.Outcome == "refused" ? Results.Conflict(result) : Results.Ok(result);
        }).RequireDesktopOwner();

        app.MapPost(root + "/switch", (PlayniteLaunchRequest? request, PlayniteSessionService session, PlayniteLauncher launcher, PlayniteSettingsStore settings) =>
        {
            PlayniteLaunchResultDto result = launcher.Switch(session.Component(true), settings.Get(), session.GameRunning, request?.DisplayId);
            return result.Outcome == "refused" ? Results.Conflict(result) : Results.Ok(result);
        }).RequireDesktopOwner();

        app.MapGet(root + "/install/plan", async (PlayniteInstaller installer, CancellationToken ct) =>
            Results.Ok(await installer.ResolveAsync(ct))).RequireDesktopOwner();

        app.MapPost(root + "/install", (PlayniteInstallRequest request, PlayniteInstaller installer) =>
        {
            string? refusal = installer.Begin(request);
            return refusal is null ? Results.Accepted(root + "/install/status", installer.Status) : Results.BadRequest(new { error = refusal });
        }).RequireDesktopOwner();

        app.MapGet(root + "/install/status", (PlayniteInstaller installer) => Results.Ok(installer.Status)).RequireDesktopOwner();

        app.MapPost(root + "/install/cancel", (PlayniteInstaller installer) =>
        {
            installer.Cancel();
            return Results.Ok(installer.Status);
        }).RequireDesktopOwner();

        // Optional Aniki ReMake Fullscreen theme (Detect -> Locate / Install with PaRuX / Skip). Same desktop-owner gate as Playnite itself.
        const string theme = root + "/theme";
        app.MapGet(theme + "/status", (PlayniteThemeService svc) =>
            Results.Ok(new PlayniteThemeStatusDto { Component = svc.Detect(), Install = svc.InstallStatus })).RequireDesktopOwner();

        app.MapGet(theme + "/plan", async (PlayniteThemeService svc, CancellationToken ct) =>
            Results.Ok(await svc.ResolveAsync(ct))).RequireDesktopOwner();

        app.MapPost(theme + "/install", (PlayniteThemeInstallRequest request, PlayniteThemeService svc) =>
        {
            string? refusal = svc.Begin(request);
            return refusal is null ? Results.Accepted(theme + "/install/status", svc.InstallStatus) : Results.BadRequest(new { error = refusal });
        }).RequireDesktopOwner();

        app.MapGet(theme + "/install/status", (PlayniteThemeService svc) => Results.Ok(svc.InstallStatus)).RequireDesktopOwner();

        app.MapPost(theme + "/install/cancel", (PlayniteThemeService svc) =>
        {
            svc.Cancel();
            return Results.Ok(svc.InstallStatus);
        }).RequireDesktopOwner();

        app.MapPost(theme + "/locate", (PlayniteThemeLocateRequest request, PlayniteThemeService svc) =>
        {
            (PlayniteThemeComponentDto? component, string? error) = svc.Locate(request?.Path);
            return component is null ? Results.BadRequest(new { error }) : Results.Ok(component);
        }).RequireDesktopOwner();

        app.MapPost(theme + "/skip", (PlayniteThemeService svc) =>
            svc.SetSkipped(true) ? Results.Ok(svc.Detect()) : Results.Problem("The choice could not be saved.")).RequireDesktopOwner();

        app.MapPost(theme + "/unskip", (PlayniteThemeService svc) =>
            svc.SetSkipped(false) ? Results.Ok(svc.Detect()) : Results.Problem("The choice could not be saved.")).RequireDesktopOwner();

        app.MapPost(theme + "/select", (PlayniteThemeConsentRequest? request, PlayniteThemeService svc) =>
        {
            PlayniteThemeActionResultDto result = svc.Select(request?.Consent == true);
            return result.Ok ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        app.MapPost(theme + "/restore-selection", (PlayniteThemeConsentRequest? request, PlayniteThemeService svc) =>
        {
            PlayniteThemeActionResultDto result = svc.RestoreSelection(request?.Consent == true);
            return result.Ok ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();

        app.MapPost(theme + "/rollback", (PlayniteThemeConsentRequest? request, PlayniteThemeService svc) =>
        {
            PlayniteThemeActionResultDto result = svc.Rollback(request?.Consent == true);
            return result.Ok ? Results.Ok(result) : Results.Conflict(result);
        }).RequireDesktopOwner();
    }
}
