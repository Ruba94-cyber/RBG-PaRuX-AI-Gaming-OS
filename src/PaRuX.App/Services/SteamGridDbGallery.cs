using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public enum SteamGridDbArtKind { Grid, Hero, Logo, Icon }

    public enum SteamGridDbStatus { Ok, NoKey, KeyRejected, RateLimited, GameNotFound, NoArtwork, Network, BadResponse, Blocked, TooLarge }

    /// <summary>One selectable SteamGridDB image. Both URLs are verified https URLs on the SteamGridDB CDN hosts.</summary>
    public sealed record SteamGridDbCandidate(long Id, SteamGridDbArtKind Kind, string Url, string ThumbUrl, int Width, int Height, long Score);

    public sealed record SteamGridDbResult<T>(SteamGridDbStatus Status, T? Value)
    {
        public bool Ok => Status == SteamGridDbStatus.Ok;
    }

    /// <summary>
    /// Lists the artwork candidates (grid, hero, logo, icon) SteamGridDB has for a game, so the owner can pick one. Talks only to
    /// https://www.steamgriddb.com/api/v2 with the owner's own key (never logged, never put in a URL) and fetches images only
    /// from the SteamGridDB CDN hosts. The HttpClient must not follow redirects. Parsing is pure and unit tested.
    /// </summary>
    public sealed class SteamGridDbGallery
    {
        public const int MaxCandidates = 24;
        public const long MaxListBytes = 2_000_000;
        public const long MaxThumbBytes = 1L * 1024 * 1024;
        public const string ApiBase = "https://www.steamgriddb.com/api/v2";

        private readonly HttpClient _http;
        private readonly Func<string?> _keyProvider;

        public SteamGridDbGallery(HttpClient http, Func<string?> keyProvider)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        }

        public static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX/2.0 (local game library; https://github.com/Ruba94-cyber)");
            return client;
        }

        public static string KindPath(SteamGridDbArtKind kind) => kind switch
        {
            SteamGridDbArtKind.Hero => "heroes",
            SteamGridDbArtKind.Logo => "logos",
            SteamGridDbArtKind.Icon => "icons",
            _ => "grids",
        };

        /// <summary>The key under which the chosen artwork is kept in the existing cover store. Grid is the cover itself.</summary>
        public static string StoreKey(string titleKey, SteamGridDbArtKind kind) =>
            kind == SteamGridDbArtKind.Grid ? titleKey : titleKey + "\u001f" + KindPath(kind);

        /// <summary>Finds the SteamGridDB game id for a title name (best close match of the autocomplete results).</summary>
        public async Task<SteamGridDbResult<long?>> FindGameAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name)) return new(SteamGridDbStatus.GameNotFound, null);
            (SteamGridDbStatus status, string? json) = await GetAsync("/search/autocomplete/" + Uri.EscapeDataString(name.Trim()), ct).ConfigureAwait(false);
            if (status != SteamGridDbStatus.Ok) return new(status, null);
            long? id = GameMetadataParsing.PickSteamGridDbGame(name.Trim(), GameMetadataParsing.ParseSteamGridDbSearch(json));
            return id is null ? new(SteamGridDbStatus.GameNotFound, null) : new(SteamGridDbStatus.Ok, id);
        }

        /// <summary>Lists up to <see cref="MaxCandidates"/> safe-for-work candidates of one kind for a SteamGridDB game id.</summary>
        public async Task<SteamGridDbResult<IReadOnlyList<SteamGridDbCandidate>>> ListAsync(long gameId, SteamGridDbArtKind kind, CancellationToken ct)
        {
            if (gameId <= 0) return new(SteamGridDbStatus.GameNotFound, null);
            string path = "/" + KindPath(kind) + "/game/" + gameId + "?nsfw=false&humor=false";
            (SteamGridDbStatus status, string? json) = await GetAsync(path, ct).ConfigureAwait(false);
            if (status != SteamGridDbStatus.Ok) return new(status, null);
            IReadOnlyList<SteamGridDbCandidate>? parsed = ParseCandidates(json, kind, MaxCandidates);
            if (parsed is null) return new(SteamGridDbStatus.BadResponse, null);
            return parsed.Count == 0 ? new(SteamGridDbStatus.NoArtwork, parsed) : new(SteamGridDbStatus.Ok, parsed);
        }

        /// <summary>
        /// Pure parser of <c>{"success":true,"data":[{id,url,thumb,width,height,score,nsfw,humor}]}</c>. Null when the document is not a
        /// successful reply; entries with a URL outside the SteamGridDB CDN, flagged nsfw/humor, or beyond <paramref name="max"/> are dropped.
        /// Highest score first.
        /// </summary>
        public static IReadOnlyList<SteamGridDbCandidate>? ParseCandidates(string? json, SteamGridDbArtKind kind, int max)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxListBytes) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (root.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False) return null;
                if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return null;
                var list = new List<SteamGridDbCandidate>();
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (Flag(item, "nsfw") || Flag(item, "humor")) continue;
                    string url = Str(item, "url");
                    if (!GameMetadataParsing.IsSteamGridDbImageUrl(url)) continue;
                    string thumb = Str(item, "thumb");
                    if (!GameMetadataParsing.IsSteamGridDbImageUrl(thumb)) thumb = url;
                    long id = item.TryGetProperty("id", out JsonElement i) && i.TryGetInt64(out long iv) ? iv : 0;
                    int width = item.TryGetProperty("width", out JsonElement w) && w.TryGetInt32(out int wv) ? wv : 0;
                    int height = item.TryGetProperty("height", out JsonElement h) && h.TryGetInt32(out int hv) ? hv : 0;
                    long score = item.TryGetProperty("score", out JsonElement s) && s.TryGetInt64(out long sv) ? sv : 0;
                    list.Add(new SteamGridDbCandidate(id, kind, url, thumb, width, height, score));
                }
                list.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (list.Count > max) list.RemoveRange(max, list.Count - max);
                return list;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>Downloads one image (thumbnail or full) from the SteamGridDB CDN with a hard size cap and no credentials.</summary>
        public async Task<SteamGridDbResult<byte[]>> FetchImageAsync(string url, long maxBytes, CancellationToken ct)
        {
            if (!GameMetadataParsing.IsSteamGridDbImageUrl(url)) return new(SteamGridDbStatus.Blocked, null);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new(SteamGridDbStatus.Network, null);
                if (response.Content.Headers.ContentLength is long len && len > maxBytes) return new(SteamGridDbStatus.TooLarge, null);
                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes) return new(SteamGridDbStatus.TooLarge, null);
                    buffer.Write(chunk, 0, read);
                }
                return buffer.Length == 0 ? new(SteamGridDbStatus.BadResponse, null) : new(SteamGridDbStatus.Ok, buffer.ToArray());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return new(SteamGridDbStatus.Network, null); }
        }

        private async Task<(SteamGridDbStatus Status, string? Body)> GetAsync(string path, CancellationToken ct)
        {
            string? key = _keyProvider();
            if (string.IsNullOrEmpty(key)) return (SteamGridDbStatus.NoKey, null);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (SteamGridDbStatus.KeyRejected, null);
                if (response.StatusCode == HttpStatusCode.TooManyRequests) return (SteamGridDbStatus.RateLimited, null);
                if (response.StatusCode == HttpStatusCode.NotFound) return (SteamGridDbStatus.GameNotFound, null);
                if (!response.IsSuccessStatusCode) return (SteamGridDbStatus.Network, null);
                if (response.Content.Headers.ContentLength is long len && len > MaxListBytes) return (SteamGridDbStatus.TooLarge, null);
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return body.Length > MaxListBytes ? (SteamGridDbStatus.TooLarge, null) : (SteamGridDbStatus.Ok, body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return (SteamGridDbStatus.Network, null); }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
    }
}
