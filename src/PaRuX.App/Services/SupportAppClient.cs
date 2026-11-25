using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Desktop-owner calls for the support apps. Core owns the list, the downloads and every start/stop decision; the shell shows its answers.</summary>
    public sealed class SupportAppClient
    {
        public static SupportAppClient Current { get; } = new SupportAppClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(12));
        private const string Base = "/api/v1/gaming/support-apps";
        private SupportAppClient() { }

        public Task<SupportAppStatus> GetAsync(CancellationToken ct) => Send<SupportAppStatus>(HttpMethod.Get, Base, null, ct);
        public Task<SupportAppStatus> SaveAsync(SupportAppSaveRequest request, CancellationToken ct) => Send<SupportAppStatus>(HttpMethod.Put, Base, request, ct);
        public Task<SupportAppStatus> StartAsync(CancellationToken ct) => Send<SupportAppStatus>(HttpMethod.Post, Base + "/start", null, ct);
        public Task<SupportAppStatus> StopAsync(CancellationToken ct) => Send<SupportAppStatus>(HttpMethod.Post, Base + "/stop", null, ct);
        public Task<List<SupportAppCatalogEntry>> CatalogAsync(CancellationToken ct) => Send<List<SupportAppCatalogEntry>>(HttpMethod.Get, Base + "/catalog", null, ct);
        public Task<List<RedistStatusRow>> RedistAsync(CancellationToken ct) => Send<List<RedistStatusRow>>(HttpMethod.Get, Base + "/redist", null, ct);
        public Task<SupportAppActionResult> InstallAsync(SupportAppInstallRequest request, CancellationToken ct) => Send<SupportAppActionResult>(HttpMethod.Post, Base + "/install", request, ct);
        public Task<SupportAppActionResult> RemoveAsync(string catalogId, CancellationToken ct) => Send<SupportAppActionResult>(HttpMethod.Post, Base + "/remove", new SupportAppRemoveRequest(catalogId), ct);

        private static async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalAiException(await ReadError(response, ct).ConfigureAwait(false));
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty answer.");
        }

        private static async Task<string> ReadError(HttpResponseMessage response, CancellationToken ct)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) return e.GetString()!;
            }
            catch (System.Text.Json.JsonException) { }
            return "PaRuX Core answered " + (int)response.StatusCode + ".";
        }
    }
}
