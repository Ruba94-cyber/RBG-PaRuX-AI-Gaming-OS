using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Desktop-owner calls for the after-sign-in app list. Core owns the list, the opt-in, the sign-in decision and the run;
    /// the shell only reports facts (sign-in session, its own start, Console/TV state) and shows Core's answers.
    /// </summary>
    public sealed class SignInAppListClient
    {
        public static SignInAppListClient Current { get; } = new SignInAppListClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private const string Base = "/api/v1/gaming/sign-in-apps";
        private SignInAppListClient() { }

        public Task<SignInAppListStatus> GetAsync(CancellationToken ct) => Send<SignInAppListStatus>(HttpMethod.Get, Base, null, ct);
        public Task<SignInAppListStatus> SaveAsync(SignInAppListSaveRequest request, CancellationToken ct) =>
            Send<SignInAppListStatus>(HttpMethod.Put, Base, request, ct);
        public Task<SignInTriggerResponse> TriggerAsync(SignInTriggerRequest request, CancellationToken ct) =>
            Send<SignInTriggerResponse>(HttpMethod.Post, Base + "/trigger", request, ct);
        public Task<SignInTriggerResponse> RunNowAsync(CancellationToken ct) => Send<SignInTriggerResponse>(HttpMethod.Post, Base + "/run", null, ct);
        public Task<SignInRunSnapshot> CancelAsync(CancellationToken ct) => Send<SignInRunSnapshot>(HttpMethod.Post, Base + "/cancel", null, ct);
        public Task ReportShellStateAsync(bool consoleActive, CancellationToken ct) =>
            SendNoContent(HttpMethod.Post, Base + "/shell-state", new SignInShellStateRequest(consoleActive), ct);
        public Task<SignInAppImportPreview> ImportPreviewAsync(string path, CancellationToken ct) =>
            Send<SignInAppImportPreview>(HttpMethod.Post, Base + "/import-preview", new SignInAppImportRequest(path), ct);
        public Task<SignInAppListStatus> ImportConfirmAsync(string path, bool append, CancellationToken ct) =>
            Send<SignInAppListStatus>(HttpMethod.Post, Base + "/import", new SignInAppImportRequest(path, Confirm: true, Append: append), ct);

        private static async Task<HttpRequestMessage> Build(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            return request;
        }

        private static async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            using HttpRequestMessage request = await Build(method, path, body, ct).ConfigureAwait(false);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalAiException(await ReadError(response, ct).ConfigureAwait(false));
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty answer.");
        }

        private static async Task SendNoContent(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            using HttpRequestMessage request = await Build(method, path, body, ct).ConfigureAwait(false);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalAiException(await ReadError(response, ct).ConfigureAwait(false));
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
