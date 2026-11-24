using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>The Core refused or failed a request. <see cref="Code"/> is the stable code the Core sent (for example "gaming_active"), if any.</summary>
    public sealed class CoreRequestException : Exception
    {
        public int Status { get; }
        public string? Code { get; }
        public CoreRequestException(int status, string? code, string message) : base(message) { Status = status; Code = code; }
    }

    /// <summary>One authenticated loopback call to the Core, returning the JSON body (or an empty element for 204). Never logs a body.</summary>
    internal static class CoreJson
    {
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(10));

        public static async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonElement parsed = default;
            if (text.Length > 0)
            {
                try { parsed = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
            }
            if (!response.IsSuccessStatusCode)
            {
                string message = parsed.ValueKind == JsonValueKind.Object && parsed.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "request failed" : "request failed (" + (int)response.StatusCode + ")";
                string? code = parsed.ValueKind == JsonValueKind.Object && parsed.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                throw new CoreRequestException((int)response.StatusCode, code, message);
            }
            return parsed;
        }

        public static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        public static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
        public static int Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }
}
