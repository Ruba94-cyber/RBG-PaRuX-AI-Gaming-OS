using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record ChatCodeRuntimeDto(
        string State, string Ownership, string? InterpreterPath, string PythonVersion, string Stage, string? Error,
        bool Enabled, bool Skipped);

    /// <summary>Talks to /api/v1/chat/code/*. Every call can fail; nothing here pretends a decision or an install happened.</summary>
    public sealed class ChatCodeClient
    {
        public static ChatCodeClient Current { get; set; } = new ChatCodeClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public async Task<IReadOnlyList<ChatCodeApprovalDto>> GetApprovalsAsync(CancellationToken ct)
        {
            using JsonDocument doc = await SendAsync(HttpMethod.Get, "/api/v1/chat/code/approvals", ct).ConfigureAwait(false);
            var list = new List<ChatCodeApprovalDto>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string id = Str(item, "id"), action = Str(item, "action"), state = Str(item, "state");
                DateTime created = item.TryGetProperty("createdUtc", out JsonElement c) && c.TryGetDateTime(out DateTime d) ? d : DateTime.UtcNow;
                list.Add(new ChatCodeApprovalDto(id, action, Str(item, "summary"), created, state));
            }
            return list;
        }

        public async Task DecideAsync(string id, bool approve, CancellationToken ct)
        {
            using JsonDocument _ = await SendAsync(HttpMethod.Post,
                "/api/v1/chat/code/approvals/" + Uri.EscapeDataString(id) + (approve ? "/approve" : "/deny"), ct).ConfigureAwait(false);
        }

        public async Task<ChatCodeRuntimeDto> GetStatusAsync(CancellationToken ct)
        {
            using JsonDocument doc = await SendAsync(HttpMethod.Get, "/api/v1/chat/code/status", ct).ConfigureAwait(false);
            return ReadRuntime(doc.RootElement.GetProperty("runtime"));
        }

        public async Task<ChatCodeRuntimeDto> PostRuntimeAsync(string action, CancellationToken ct)
        {
            string path = action switch
            {
                "install" => "/api/v1/chat/code/runtime/install",
                "cancel" => "/api/v1/chat/code/runtime/install/cancel",
                "skip" => "/api/v1/chat/code/runtime/skip",
                "unskip" => "/api/v1/chat/code/runtime/unskip",
                _ => throw new ArgumentException("Unknown runtime action.", nameof(action))
            };
            using JsonDocument doc = await SendAsync(HttpMethod.Post, path, ct).ConfigureAwait(false);
            // 202 install returns the runtime status directly; the others return it too.
            return ReadRuntime(doc.RootElement);
        }

        public async Task<ChatCodeRuntimeDto> SetEnabledAsync(bool enabled, CancellationToken ct)
        {
            using JsonDocument doc = await SendAsync(HttpMethod.Put, "/api/v1/chat/code/settings", ct, new { enabled }).ConfigureAwait(false);
            return ReadRuntime(doc.RootElement);
        }

        private static ChatCodeRuntimeDto ReadRuntime(JsonElement e)
        {
            JsonElement settings = e.TryGetProperty("settings", out JsonElement s) ? s : default;
            return new ChatCodeRuntimeDto(
                Str(e, "state"), Str(e, "ownership"),
                e.TryGetProperty("interpreterPath", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                Str(e, "pythonVersion"), Str(e, "stage"),
                e.TryGetProperty("error", out JsonElement er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null,
                settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("enabled", out JsonElement en) && en.ValueKind == JsonValueKind.True,
                settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("skipped", out JsonElement sk) && sk.ValueKind == JsonValueKind.True);
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty : string.Empty;

        private static async Task<JsonDocument> SendAsync(HttpMethod method, string path, CancellationToken ct, object? body = null)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = text.Length > 300 ? text[..300] : text;
                throw new LocalAiException("Code execution request failed (" + (int)response.StatusCode + "): " + detail);
            }
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
    }
}
