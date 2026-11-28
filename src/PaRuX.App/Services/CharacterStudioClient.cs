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
    /// <summary>A Character Studio failure with the server's stable code and the next step for the user.</summary>
    public sealed class CharacterStudioApiException : Exception
    {
        public string Code { get; }
        public string NextAction { get; }
        public CharacterStudioApiException(string code, string message, string nextAction) : base(message)
        {
            Code = code;
            NextAction = nextAction;
        }
    }

    /// <summary>Thin client for PaRuX Core's /api/v2/characters routes (desktop owner token).</summary>
    public sealed class CharacterStudioClient
    {
        public static CharacterStudioClient Current { get; } = new CharacterStudioClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(30));
        private CharacterStudioClient() { }

        public Task<CharacterListResponse> ListAsync(CancellationToken ct) => Send<CharacterListResponse>(HttpMethod.Get, "/api/v2/characters", null, ct);
        public Task<CharacterProfile> CreateAsync(CreateCharacterRequest r, CancellationToken ct) => Send<CharacterProfile>(HttpMethod.Post, "/api/v2/characters", r, ct);
        public Task<CharacterProfile> GetAsync(string id, CancellationToken ct) => Send<CharacterProfile>(HttpMethod.Get, "/api/v2/characters/" + E(id), null, ct);
        public Task<CharacterProfile> UpdateAsync(string id, UpdateCharacterRequest r, CancellationToken ct) => Send<CharacterProfile>(HttpMethod.Put, "/api/v2/characters/" + E(id), r, ct);
        public Task DeleteAsync(string id, CancellationToken ct) => Send<object>(HttpMethod.Delete, "/api/v2/characters/" + E(id), null, ct, allowEmpty: true);
        public Task<CharacterProfile> GenerateReferencesAsync(string id, int count, CancellationToken ct) =>
            Send<CharacterProfile>(HttpMethod.Post, "/api/v2/characters/" + E(id) + "/references/generate", new GenerateReferencesRequest { Count = count }, ct);
        public Task<CharacterProfile> AddImageAsync(string id, string fileName, byte[] data, CancellationToken ct) =>
            Send<CharacterProfile>(HttpMethod.Post, "/api/v2/characters/" + E(id) + "/images",
                new AddCharacterImageRequest { FileName = fileName, DataBase64 = Convert.ToBase64String(data) }, ct);
        public Task<CharacterProfile> RemoveImageAsync(string id, string imageId, CancellationToken ct) =>
            Send<CharacterProfile>(HttpMethod.Delete, "/api/v2/characters/" + E(id) + "/images/" + E(imageId), null, ct);
        public Task<CharacterProfile> CaptionAsync(string id, CancellationToken ct) => Send<CharacterProfile>(HttpMethod.Post, "/api/v2/characters/" + E(id) + "/captions", null, ct);
        public Task<CharacterTrainingPlan> PlanAsync(string id, string preset, CancellationToken ct) =>
            Send<CharacterTrainingPlan>(HttpMethod.Get, "/api/v2/characters/" + E(id) + "/plan?preset=" + E(preset), null, ct);
        public Task<CharacterProfile> StartTrainingAsync(string id, StartCharacterTrainingRequest r, CancellationToken ct) =>
            Send<CharacterProfile>(HttpMethod.Post, "/api/v2/characters/" + E(id) + "/training", r, ct);
        public Task<CharacterProfile> ResumeTrainingAsync(string id, CancellationToken ct) =>
            Send<CharacterProfile>(HttpMethod.Post, "/api/v2/characters/" + E(id) + "/training/resume", null, ct);
        public Task<CharacterProfile> CancelTrainingAsync(string id, CancellationToken ct) =>
            Send<CharacterProfile>(HttpMethod.Post, "/api/v2/characters/" + E(id) + "/training/cancel", null, ct);
        public Task<CharacterTrainerStatus> TrainerStatusAsync(CancellationToken ct) => Send<CharacterTrainerStatus>(HttpMethod.Get, "/api/v2/characters/trainer/status", null, ct);
        public Task<CharacterTrainerStatus> InstallTrainerAsync(CancellationToken ct) => Send<CharacterTrainerStatus>(HttpMethod.Post, "/api/v2/characters/trainer/install", null, ct);
        public Task<CharacterTrainerStatus> LocateTrainerAsync(string folder, string python, CancellationToken ct) =>
            Send<CharacterTrainerStatus>(HttpMethod.Post, "/api/v2/characters/trainer/locate", new LocateCharacterTrainerRequest { TrainerFolder = folder, PythonExe = python }, ct);
        public Task<CharacterTrainerStatus> SkipTrainerAsync(CancellationToken ct) => Send<CharacterTrainerStatus>(HttpMethod.Post, "/api/v2/characters/trainer/skip", null, ct);

        /// <summary>Raw bytes of a character picture or a training sample (kind is "images" or "samples"), or null.</summary>
        public async Task<byte[]?> GetFileAsync(string id, string kind, string file, CancellationToken ct) =>
            await GetBytes("/api/v2/characters/" + E(id) + "/" + kind + "/" + E(file), ct).ConfigureAwait(false);

        /// <summary>Portrait of the character attached to the active chat persona, or null when there is none.</summary>
        public async Task<byte[]?> GetActivePortraitAsync(CancellationToken ct) => await GetBytes("/api/v2/characters/active/portrait", ct).ConfigureAwait(false);

        private static string E(string s) => Uri.EscapeDataString(s);

        private static async Task<byte[]?> GetBytes(string path, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
        }

        private static async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct, bool allowEmpty = false)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string code = response.StatusCode == System.Net.HttpStatusCode.Forbidden ? "forbidden" : "failed";
                string message = detail, next = string.Empty;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(detail);
                    if (doc.RootElement.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String) code = c.GetString() ?? code;
                    if (doc.RootElement.TryGetProperty("error", out JsonElement m) && m.ValueKind == JsonValueKind.String) message = m.GetString() ?? message;
                    if (doc.RootElement.TryGetProperty("nextAction", out JsonElement n) && n.ValueKind == JsonValueKind.String) next = n.GetString() ?? string.Empty;
                }
                catch (JsonException) { }
                throw new CharacterStudioApiException(code, message, next);
            }
            if (allowEmpty || response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new CharacterStudioApiException("empty", "PaRuX Core returned an empty answer.", string.Empty);
        }
    }
}
