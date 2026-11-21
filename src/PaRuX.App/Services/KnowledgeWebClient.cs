using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record WebSourceView(string Id, string Url, string Mode, int MaxPages, int MaxDepth, string State, int Pages, string? Error,
        string? StoppedBecause, int Written, int Unchanged, int Skipped, int Failed, int KeptEdited, int Removed);

    public sealed record SearchChoiceView(string Id, string Label, string Description, bool Selected);

    public sealed record RetrievalView(string Method, string Mode, string Reason, bool EmbedderInstalled, string? EmbedderModel,
        bool CanInstallEmbedder, bool OfflineBlocked, IReadOnlyList<SearchChoiceView> Choices, string Note);

    /// <summary>The Core refused a website or watch request; <see cref="Code"/> is a stable reason such as offline_blocked.</summary>
    public sealed class KnowledgeWebException : Exception
    {
        public string? Code { get; }
        public KnowledgeWebException(string message, string? code) : base(message) { Code = code; }
    }

    /// <summary>Client for websites and watched folders as Local Knowledge sources, and for the search method. Loopback only.</summary>
    public sealed class KnowledgeWebClient
    {
        public static KnowledgeWebClient Current { get; } = new KnowledgeWebClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(1));
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        private KnowledgeWebClient() { }

        public async Task<List<WebSourceView>> ListWebSourcesAsync(CancellationToken ct) =>
            await SendAsync<List<WebSourceView>>(HttpMethod.Get, "/api/v1/knowledge/web-sources", null, ct).ConfigureAwait(false) ?? new List<WebSourceView>();

        public Task<WebSourceView?> AddWebSourceAsync(string url, bool crawl, CancellationToken ct) =>
            SendAsync<WebSourceView>(HttpMethod.Post, "/api/v1/knowledge/web-sources", new { url, mode = crawl ? "crawl" : "page" }, ct);

        public Task<WebSourceView?> ResyncAsync(string id, CancellationToken ct) =>
            SendAsync<WebSourceView>(HttpMethod.Post, "/api/v1/knowledge/web-sources/" + Uri.EscapeDataString(id) + "/resync", null, ct);

        public Task RemoveWebSourceAsync(string id, CancellationToken ct) =>
            SendAsync<object>(HttpMethod.Delete, "/api/v1/knowledge/web-sources/" + Uri.EscapeDataString(id), null, ct);

        public async Task<HashSet<string>> WatchedAsync(CancellationToken ct) =>
            (await SendAsync<List<string>>(HttpMethod.Get, "/api/v1/knowledge/watched", null, ct).ConfigureAwait(false) ?? new List<string>()).ToHashSet(StringComparer.Ordinal);

        public Task SetWatchedAsync(string id, bool enabled, CancellationToken ct, bool consentBroad = false) =>
            SendAsync<object>(HttpMethod.Post, "/api/v1/knowledge/sources/" + Uri.EscapeDataString(id) + "/watch", new { enabled, consentBroad }, ct);

        public Task<RetrievalView?> GetRetrievalAsync(CancellationToken ct) =>
            SendAsync<RetrievalView>(HttpMethod.Get, "/api/v1/knowledge/retrieval", null, ct);

        public Task SetSearchMethodAsync(string method, CancellationToken ct) =>
            SendAsync<object>(HttpMethod.Put, "/api/v1/knowledge/settings", new { searchMethod = method }, ct);

        private static async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) where T : class
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body, options: Web);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string message = "The request failed (HTTP " + (int)response.StatusCode + ").";
                string? code = null;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String) message = e.GetString() ?? message;
                    if (doc.RootElement.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                }
                catch (JsonException) { }
                throw new KnowledgeWebException(message, code);
            }
            if (string.IsNullOrWhiteSpace(text) || typeof(T) == typeof(object)) return null;
            return JsonSerializer.Deserialize<T>(text, Web);
        }
    }
}
