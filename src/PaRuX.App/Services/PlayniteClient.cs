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

        public Task<GamingCallResult<PlayniteSettingsDto>> SaveSettingsAsync(PlayniteSettingsDto settings, CancellationToken ct = default) =>
            SendAsync<PlayniteSettingsDto>(HttpMethod.Put, Root + "/settings", settings, ct);

        public Task<GamingCallResult<PlayniteComponentDto>> LocateAsync(string path, CancellationToken ct = default) =>
            SendAsync<PlayniteComponentDto>(HttpMethod.Post, Root + "/locate", new PlayniteLocateRequest { Path = path }, ct);

        public Task<GamingCallResult<PlayniteComponentDto>> SkipAsync(bool skip, CancellationToken ct = default) =>
            SendAsync<PlayniteComponentDto>(HttpMethod.Post, Root + (skip ? "/skip" : "/unskip"), null, ct);

        public Task<GamingCallResult<PlayniteLaunchResultDto>> LaunchAsync(string? mode, string? displayId, CancellationToken ct = default) =>
            SendAsync<PlayniteLaunchResultDto>(HttpMethod.Post, Root + "/launch", new PlayniteLaunchRequest { Mode = mode, DisplayId = displayId }, ct);

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

        /// <summary>Streaming mode status (existing owner-only endpoint), shown on the Console Mode page.</summary>
        public Task<GamingCallResult<JsonElement>> GetStreamingStatusAsync(CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Get, "/api/v1/gaming/streaming", null, ct);

        public Task<GamingCallResult<JsonElement>> StartStreamingAsync(bool allowStartSunshine, CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/streaming/start",
                new { allowStartSunshine, allowDisplayChange = false }, ct);

        public Task<GamingCallResult<JsonElement>> StopStreamingAsync(CancellationToken ct = default) =>
            SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/gaming/streaming/stop", null, ct);

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
