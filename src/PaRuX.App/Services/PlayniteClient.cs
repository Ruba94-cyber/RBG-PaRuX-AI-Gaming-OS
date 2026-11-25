using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Typed client for the optional Playnite component (owner-only Core endpoints under /api/v1/gaming/playnite): detect, locate, skip,
    /// install with consent, launch Desktop or Fullscreen, switch mode, settings and the game-session status. Same authenticated
    /// control-plane path as <see cref="GamingLibraryClient"/>. Playnite itself is never modified.
    /// </summary>
    public sealed class PlayniteClient
    {
        public static PlayniteClient Current { get; } = new PlayniteClient();
        private const string Root = "/api/v1/gaming/playnite";
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(40));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private PlayniteClient() { }

        public Task<GamingCallResult<PlayniteStatusDto>> GetStatusAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteStatusDto>(HttpMethod.Get, Root + "/status", null, ct);

        /// <summary>Status with a fresh process check (not the cached detection): used by the Playnite return watch.</summary>
        public Task<GamingCallResult<PlayniteStatusDto>> GetLiveStatusAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteStatusDto>(HttpMethod.Get, Root + "/status?live=true", null, ct);

        public Task<GamingCallResult<PlayniteSettingsDto>> SaveSettingsAsync(PlayniteSettingsDto settings, CancellationToken ct = default) =>
            SendAsync<PlayniteSettingsDto>(HttpMethod.Put, Root + "/settings", settings, ct);

        public Task<GamingCallResult<PlayniteComponentDto>> LocateAsync(string path, CancellationToken ct = default) =>
            SendAsync<PlayniteComponentDto>(HttpMethod.Post, Root + "/locate", new PlayniteLocateRequest { Path = path }, ct);

        public Task<GamingCallResult<PlayniteComponentDto>> SkipAsync(bool skip, CancellationToken ct = default) =>
            SendAsync<PlayniteComponentDto>(HttpMethod.Post, Root + (skip ? "/skip" : "/unskip"), null, ct);

        public Task<GamingCallResult<PlayniteLaunchResultDto>> LaunchAsync(string? mode, string? displayId, CancellationToken ct = default, bool explicitRequest = false) =>
            SendAsync<PlayniteLaunchResultDto>(HttpMethod.Post, Root + "/launch", new PlayniteLaunchRequest { Mode = mode, DisplayId = displayId, Explicit = explicitRequest }, ct);

        public Task<GamingCallResult<PlayniteLaunchResultDto>> SwitchAsync(string? displayId, CancellationToken ct = default) =>
            SendAsync<PlayniteLaunchResultDto>(HttpMethod.Post, Root + "/switch", new PlayniteLaunchRequest { DisplayId = displayId }, ct);

        public Task<GamingCallResult<PlayniteInstallPlanDto>> GetInstallPlanAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteInstallPlanDto>(HttpMethod.Get, Root + "/install/plan", null, ct);

        public Task<GamingCallResult<PlayniteInstallStatusDto>> InstallAsync(PlayniteInstallRequest request, CancellationToken ct = default) =>
            SendAsync<PlayniteInstallStatusDto>(HttpMethod.Post, Root + "/install", request, ct);

        public Task<GamingCallResult<PlayniteInstallStatusDto>> GetInstallStatusAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteInstallStatusDto>(HttpMethod.Get, Root + "/install/status", null, ct);

        public Task<GamingCallResult<PlayniteInstallStatusDto>> CancelInstallAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteInstallStatusDto>(HttpMethod.Post, Root + "/install/cancel", null, ct);

        // ---- Optional Aniki ReMake Fullscreen theme (Detect -> Locate / Install with PaRuX / Skip) ----

        public Task<GamingCallResult<PlayniteThemeStatusDto>> GetThemeStatusAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteThemeStatusDto>(HttpMethod.Get, Root + "/theme/status", null, ct);

        public Task<GamingCallResult<PlayniteThemePlanDto>> GetThemePlanAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteThemePlanDto>(HttpMethod.Get, Root + "/theme/plan", null, ct);

        public Task<GamingCallResult<PlayniteThemeInstallStatusDto>> InstallThemeAsync(PlayniteThemeInstallRequest request, CancellationToken ct = default) =>
            SendAsync<PlayniteThemeInstallStatusDto>(HttpMethod.Post, Root + "/theme/install", request, ct);

        public Task<GamingCallResult<PlayniteThemeInstallStatusDto>> GetThemeInstallStatusAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteThemeInstallStatusDto>(HttpMethod.Get, Root + "/theme/install/status", null, ct);

        public Task<GamingCallResult<PlayniteThemeInstallStatusDto>> CancelThemeInstallAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteThemeInstallStatusDto>(HttpMethod.Post, Root + "/theme/install/cancel", null, ct);

        public Task<GamingCallResult<PlayniteThemeComponentDto>> LocateThemeAsync(string path, CancellationToken ct = default) =>
            SendAsync<PlayniteThemeComponentDto>(HttpMethod.Post, Root + "/theme/locate", new PlayniteThemeLocateRequest { Path = path }, ct);

        public Task<GamingCallResult<PlayniteThemeComponentDto>> SkipThemeAsync(bool skip, CancellationToken ct = default) =>
            SendAsync<PlayniteThemeComponentDto>(HttpMethod.Post, Root + (skip ? "/theme/skip" : "/theme/unskip"), null, ct);

        /// <summary>Selects Aniki ReMake in Playnite's Fullscreen configuration. The caller must have shown the consent text first.</summary>
        public Task<GamingCallResult<PlayniteThemeActionResultDto>> SelectThemeAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteThemeActionResultDto>(HttpMethod.Post, Root + "/theme/select", new PlayniteThemeConsentRequest { Consent = true }, ct);

        public Task<GamingCallResult<PlayniteThemeActionResultDto>> RestoreThemeSelectionAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteThemeActionResultDto>(HttpMethod.Post, Root + "/theme/restore-selection", new PlayniteThemeConsentRequest { Consent = true }, ct);

        public Task<GamingCallResult<PlayniteThemeActionResultDto>> RollbackThemeAsync(CancellationToken ct = default) =>
            SendAsync<PlayniteThemeActionResultDto>(HttpMethod.Post, Root + "/theme/rollback", new PlayniteThemeConsentRequest { Consent = true }, ct);

        /// <summary>Streaming mode status (existing owner-only endpoint), shown on the Console Mode page.</summary>
        public Task<GamingCallResult<JsonElement>> GetStreamingStatusAsync(CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Get, "/api/v1/gaming/streaming", null, ct);

        public Task<GamingCallResult<JsonElement>> StartStreamingAsync(bool allowStartSunshine, CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/streaming/start",
                new { allowStartSunshine, allowDisplayChange = false }, ct);

        public Task<GamingCallResult<JsonElement>> StopStreamingAsync(CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/streaming/stop", null, ct);

        // ---- Opt-in Console power plan (owner-only Core endpoints; the Core keeps the crash-safe restore point) ----

        public Task<GamingCallResult<JsonElement>> GetPowerProfileAsync(CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Get, "/api/v1/gaming/power-profile", null, ct);

        public Task<GamingCallResult<JsonElement>> ApplyPowerProfileAsync(string planId, CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/power-profile/apply", new { planId, allowPowerPlanChange = true }, ct);

        public Task<GamingCallResult<JsonElement>> RestorePowerProfileAsync(CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/power-profile/restore", null, ct);

        // ---- Xbox Game Bar block (owner-only Core endpoints; HKCU only, the Core keeps the restore point) ----

        public Task<GamingCallResult<JsonElement>> GetGameBarAsync(CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Get, "/api/v1/gaming/game-bar", null, ct);

        public Task<GamingCallResult<JsonElement>> ApplyGameBarBlockAsync(bool permanent, CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/game-bar/apply", new { allowGameBarChange = true, permanent }, ct);

        public Task<GamingCallResult<JsonElement>> RestoreGameBarAsync(bool includePermanent = false, CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/game-bar/restore", new { includePermanent }, ct);

        private static async Task<GamingCallResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            try
            {
                ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
                request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
                using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                return await GamingResponseReader.ReadAsync<T>(response, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return GamingCallResult<T>.Fail(ex.Message); }
        }
    }
}
