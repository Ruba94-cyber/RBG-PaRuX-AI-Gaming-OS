using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Desktop client for the optional local OpenAI-compatible server provider (USER_MANAGED).</summary>
    public sealed class OpenAiCompatibleProviderClient
    {
        public static OpenAiCompatibleProviderClient Current { get; } = new OpenAiCompatibleProviderClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(20));
        private const string Path = "/api/v1/ai/openai-compatible";
        private OpenAiCompatibleProviderClient() { }

        public Task<OpenAiCompatibleProviderStatus> GetStatusAsync(CancellationToken ct) =>
            SendAsync<OpenAiCompatibleProviderStatus>(HttpMethod.Get, Path, null, ct);

        public Task<OpenAiCompatibleProviderStatus> SaveAsync(OpenAiCompatibleProviderRequest request, CancellationToken ct) =>
            SendAsync<OpenAiCompatibleProviderStatus>(HttpMethod.Put, Path, request, ct);

        public Task<OpenAiCompatibleTestResult> TestAsync(OpenAiCompatibleProviderRequest? candidate, CancellationToken ct) =>
            SendAsync<OpenAiCompatibleTestResult>(HttpMethod.Post, Path + "/test", candidate ?? new OpenAiCompatibleProviderRequest(), ct, treatEmptyBodyAsStored: candidate == null);

        public async Task ClearAsync(CancellationToken ct)
        {
            using HttpResponseMessage response = await SendRawAsync(HttpMethod.Delete, Path, null, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, bool treatEmptyBodyAsStored = false)
        {
            // An empty BaseUrl in the body means "test what is stored", which the Core reads as no candidate.
            using HttpResponseMessage response = await SendRawAsync(method, path, treatEmptyBodyAsStored ? null : body, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty response.");
        }

        private static async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            return await Http.SendAsync(request, ct).ConfigureAwait(false);
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.IsSuccessStatusCode) return;
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using JsonDocument document = JsonDocument.Parse(detail);
                if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                    detail = error.GetString() ?? detail;
            }
            catch (JsonException) { }
            throw new LocalAiException(detail.Length > 0 ? detail : "The request failed (HTTP " + (int)response.StatusCode + ").");
        }
    }
}
