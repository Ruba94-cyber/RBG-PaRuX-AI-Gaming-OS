using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>One keyless search backend. Implementations throw on transport/format failure (so the chain can
/// record an honest failure and try the next one) and return an empty list only for a genuine "no results".</summary>
internal interface IWebSearchProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<List<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct);
}

/// <summary>
/// Wikipedia's public MediaWiki search API (https only, no key, one fixed host). Only the query text leaves the
/// PC. The response is bounded by the shared reader and every result URL is built by this class from the page
/// title, never copied from the response.
/// </summary>
internal sealed class WikipediaSearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://en.wikipedia.org/w/api.php";
    private const int MaxQueryCharacters = 300;
    private readonly HttpClient _http;

    public WikipediaSearchProvider(HttpClient http) => _http = http;

    public string Id => WebSearchSettingsService.WikipediaId;
    public string DisplayName => "Wikipedia";

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        string text = (query ?? string.Empty).Trim();
        if (text.Length == 0 || maxResults <= 0) return new List<WebSearchResult>();
        if (text.Length > MaxQueryCharacters) text = text[..MaxQueryCharacters];
        int limit = Math.Clamp(maxResults, 1, 10);

        string url = Endpoint + "?action=query&list=search&format=json&utf8=1&srlimit=" + limit
            + "&srprop=snippet&srsearch=" + Uri.EscapeDataString(text);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuXLocalAI/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await WebSearchService.ReadBoundedTextAsync(response.Content, ct).ConfigureAwait(false);
        return ParseResults(json, limit);
    }

    internal static List<WebSearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<WebSearchResult>();
        using JsonDocument document = JsonDocument.Parse(json);   // malformed JSON is a provider failure
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("query", out JsonElement query) || query.ValueKind != JsonValueKind.Object
            || !query.TryGetProperty("search", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Wikipedia returned an unexpected response shape.");

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (results.Count >= maxResults) break;
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("title", out JsonElement titleElement)
                || titleElement.ValueKind != JsonValueKind.String) continue;
            string title = (titleElement.GetString() ?? string.Empty).Trim();
            if (title.Length == 0 || title.Length > 180) continue;
            string snippet = entry.TryGetProperty("snippet", out JsonElement s) && s.ValueKind == JsonValueKind.String
                ? WebSearchService.CleanHtml(s.GetString() ?? string.Empty) : string.Empty;
            if (snippet.Length > 500) snippet = snippet[..500];
            results.Add(new WebSearchResult
            {
                Title = title,
                Url = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%3A", ":").Replace("%28", "(").Replace("%29", ")"),
                Snippet = snippet
            });
        }
        return results;
    }
}

/// <summary>
/// Marginalia Search public JSON API (https, one fixed host, the published shared "public" key, no account). Small-web
/// index that complements DuckDuckGo and Wikipedia when those fail or return nothing. Only the query text leaves the
/// PC. Result URLs come from the response but must be http(s); they are neutralised later like every web result.
/// </summary>
internal sealed class MarginaliaSearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://api.marginalia.nu/public/search/";
    private const int MaxQueryCharacters = 300;
    private readonly HttpClient _http;

    public MarginaliaSearchProvider(HttpClient http) => _http = http;

    public string Id => WebSearchSettingsService.MarginaliaId;
    public string DisplayName => "Marginalia";

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        string text = (query ?? string.Empty).Trim();
        if (text.Length == 0 || maxResults <= 0) return new List<WebSearchResult>();
        if (text.Length > MaxQueryCharacters) text = text[..MaxQueryCharacters];
        int limit = Math.Clamp(maxResults, 1, 10);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + Uri.EscapeDataString(text) + "?count=" + limit);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("PaRuXLocalAI/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == (HttpStatusCode)429)
            throw new HttpRequestException("Marginalia rate limit reached (HTTP 429).");
        response.EnsureSuccessStatusCode();
        string json = await WebSearchService.ReadBoundedTextAsync(response.Content, ct).ConfigureAwait(false);
        return ParseResults(json, limit);
    }

    internal static List<WebSearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<WebSearchResult>();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Marginalia returned an unexpected response shape.");
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (results.Count >= maxResults) break;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string url = entry.TryGetProperty("url", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? string.Empty : string.Empty;
            if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) continue;
            string title = entry.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? WebSearchService.CleanHtml(t.GetString() ?? string.Empty) : string.Empty;
            string snippet = entry.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String ? WebSearchService.CleanHtml(d.GetString() ?? string.Empty) : string.Empty;
            if (title.Length == 0) title = uri.Host;
            results.Add(new WebSearchResult { Title = title.Length > 180 ? title[..180] : title, Url = uri.AbsoluteUri, Snippet = snippet.Length > 500 ? snippet[..500] : snippet });
        }
        return results;
    }
}

/// <summary>
/// Brave Search API (https, one fixed host, user-supplied key sent only in the X-Subscription-Token header to that host).
/// Opt-in: it joins the chain only while a key is stored. Result URLs are taken from the response but must be http(s) and
/// are neutralised later like every web result; the key never appears in a URL, a message or a log.
/// </summary>
internal sealed class BraveSearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://api.search.brave.com/res/v1/web/search";
    private const int MaxQueryCharacters = 300;
    private readonly HttpClient _http;
    private readonly string _key;

    public BraveSearchProvider(HttpClient http, string key) { _http = http; _key = key; }

    public string Id => WebSearchSettingsService.BraveId;
    public string DisplayName => "Brave Search";

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        string text = (query ?? string.Empty).Trim();
        if (text.Length == 0 || maxResults <= 0) return new List<WebSearchResult>();
        if (text.Length > MaxQueryCharacters) text = text[..MaxQueryCharacters];
        int limit = Math.Clamp(maxResults, 1, 10);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "?count=" + limit + "&q=" + Uri.EscapeDataString(text));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Subscription-Token", _key);
        request.Headers.UserAgent.ParseAdd("PaRuXLocalAI/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("Brave Search refused the API key (HTTP " + (int)response.StatusCode + ").");
        if (response.StatusCode == (HttpStatusCode)429)
            throw new HttpRequestException("Brave Search rate limit or quota reached (HTTP 429).");
        response.EnsureSuccessStatusCode();
        string json = await WebSearchService.ReadBoundedTextAsync(response.Content, ct).ConfigureAwait(false);
        return ParseResults(json, limit);
    }

    internal static List<WebSearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<WebSearchResult>();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Brave Search returned an unexpected response shape.");
        if (!root.TryGetProperty("web", out JsonElement web) || web.ValueKind != JsonValueKind.Object
            || !web.TryGetProperty("results", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            return results; // a valid answer without a web section is a genuine "no results"
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (results.Count >= maxResults) break;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string url = entry.TryGetProperty("url", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? string.Empty : string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp || url.Length > 2048) continue;
            string title = entry.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? WebSearchService.CleanHtml(t.GetString() ?? string.Empty) : string.Empty;
            string snippet = entry.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String ? WebSearchService.CleanHtml(d.GetString() ?? string.Empty) : string.Empty;
            if (title.Length == 0) title = uri.Host;
            results.Add(new WebSearchResult { Title = title.Length > 180 ? title[..180] : title, Url = uri.AbsoluteUri, Snippet = snippet.Length > 500 ? snippet[..500] : snippet });
        }
        return results;
    }
}

/// <summary>
/// The user's own SearXNG, from Settings (or the legacy environment variable). The endpoint is accepted only on
/// loopback or a private LAN IPv4 address. The client never follows redirects, never uses a proxy or cookies, and
/// a non-200 answer or an oversized body is a reported failure rather than a silent empty result.
/// </summary>
internal sealed class SearxngSearchProvider : IWebSearchProvider
{
    private const int MaxJsonBytes = 131_072;
    private const int MaxQueryCharacters = 512;
    private readonly Uri _endpoint;
    private readonly HttpClient _http;

    public SearxngSearchProvider(Uri endpoint, HttpClient http)
    {
        if (!LocalSearxngSearchProvider.IsAllowedConfiguredEndpoint(endpoint))
            throw new ArgumentException("SearXNG endpoint must be loopback or a private LAN address.", nameof(endpoint));
        _endpoint = endpoint;
        _http = http;
    }

    public string Id => WebSearchSettingsService.SearxngId;
    public string DisplayName => "SearXNG (self-hosted)";

    internal static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        {
            // A stopped self-hosted instance must not delay the fallback providers.
            Timeout = TimeSpan.FromSeconds(4)
        };

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        string text = (query ?? string.Empty).Trim();
        if (text.Length == 0 || maxResults <= 0) return new List<WebSearchResult>();
        if (text.Length > MaxQueryCharacters) text = text[..MaxQueryCharacters];

        var builder = new UriBuilder(_endpoint)
        {
            Path = "/search",
            Query = "q=" + Uri.EscapeDataString(text) + "&format=json"
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("SearXNG answered HTTP " + (int)response.StatusCode
                + " (JSON output may be disabled in its settings).");
        if (response.Content.Headers.ContentLength > MaxJsonBytes)
            throw new InvalidDataException("SearXNG response is larger than the allowed limit.");

        await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] data = new byte[MaxJsonBytes + 1];
        int used = 0;
        while (used < data.Length)
        {
            int read = await input.ReadAsync(data.AsMemory(used, data.Length - used), ct).ConfigureAwait(false);
            if (read == 0) break;
            used += read;
        }
        if (used > MaxJsonBytes) throw new InvalidDataException("SearXNG response is larger than the allowed limit.");
        try { using JsonDocument probe = JsonDocument.Parse(data.AsMemory(0, used)); }
        catch (JsonException) { throw new InvalidDataException("SearXNG did not return JSON."); }
        return LocalSearxngSearchProvider.ParseResults(data.AsSpan(0, used), Math.Clamp(maxResults, 1, 10));
    }
}
