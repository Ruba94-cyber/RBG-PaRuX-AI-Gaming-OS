using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Read-only desktop client for the same Core-owned bounded Project Knowledge used by
    /// Android PC Studio/Chat. The desktop never reconstructs Project task/artifact context from
    /// local UI state; Core remains the authority and can therefore fail closed on a stale id.</summary>
    public sealed class ProjectKnowledgeClient
    {
        public static ProjectKnowledgeClient Current { get; } = new ProjectKnowledgeClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private ProjectKnowledgeClient() { }

        private static string UiLang() =>
            UiLanguage.Normalize(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

        /// <summary>Per-workspace system prompt template + pinned documents, with the server's own preview (variables expanded, pin budget plan).</summary>
        public Task<ProjectChatSettingsResponse> GetChatSettingsAsync(string projectId, CancellationToken ct) =>
            SendAsync<ProjectChatSettingsResponse>(HttpMethod.Get, ChatSettingsPath(projectId) + "?lang=" + UiLang(), null, ct);

        public Task<ProjectChatSettingsResponse> SaveChatSettingsAsync(string projectId, ProjectChatSettingsDto settings, CancellationToken ct) =>
            SendAsync<ProjectChatSettingsResponse>(HttpMethod.Put, ChatSettingsPath(projectId) + "?lang=" + UiLang(), settings, ct);

        public Task<List<PinnableDocumentDto>> SearchPinnableDocumentsAsync(string projectId, string query, CancellationToken ct) =>
            SendAsync<List<PinnableDocumentDto>>(HttpMethod.Get, ChatSettingsPath(projectId) + "/documents?q=" + Uri.EscapeDataString(query ?? string.Empty), null, ct);

        private static string ChatSettingsPath(string projectId) =>
            "/api/v1/projects/" + Uri.EscapeDataString(projectId) + "/chat-settings";

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Workspace chat settings request failed (HTTP " + (int)response.StatusCode + "). " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty response.");
        }

        public async Task<ProjectKnowledgePackDto> GetAsync(string projectId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new LocalAiException("An active Project id is required before loading Project Knowledge.");

            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                bootstrap.Endpoint.TrimEnd('/') + "/api/v1/projects/" + Uri.EscapeDataString(projectId) + "/knowledge?lang=" + UiLang());
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException(
                    "Project context could not be loaded, so no model request was sent (HTTP "
                    + (int)response.StatusCode + "). " + detail);
            }

            ProjectKnowledgePackDto? pack = await response.Content
                .ReadFromJsonAsync<ProjectKnowledgePackDto>(cancellationToken: ct)
                .ConfigureAwait(false);
            return pack ?? throw new LocalAiException(
                "Project context could not be loaded, so no model request was sent: PaRuX Core returned an empty response.");
        }
    }
}
