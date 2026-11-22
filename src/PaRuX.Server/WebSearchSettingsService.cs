using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Operator choices for web search providers: an optional self-hosted SearXNG endpoint and which of the
/// built-in keyless providers are switched off. Holds no secrets (no keys, no credentials). It does not
/// grant Internet access: <see cref="InternetSettingsService"/> stays the only consent switch.
/// </summary>
public sealed class WebSearchSettingsService
{
    public const string DuckDuckGoId = "duckduckgo";
    public const string WikipediaId = "wikipedia";
    public const string SearxngId = "searxng";

    private readonly object _gate = new();
    private readonly string _stateDirectory;
    private Snapshot? _cache;

    public WebSearchSettingsService() : this(ServerState.StateDirectory) { }

    public WebSearchSettingsService(string stateDirectory) => _stateDirectory = stateDirectory;

    private string SettingPath => Path.Combine(_stateDirectory, "websearch.json");

    public sealed record Snapshot(string SearxngUrl, IReadOnlySet<string> DisabledProviders);

    public Snapshot Get()
    {
        lock (_gate)
        {
            if (_cache != null) return _cache;
            string url = string.Empty;
            var disabled = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(SettingPath))
                {
                    var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(SettingPath));
                    if (doc != null)
                    {
                        if (Uri.TryCreate(doc.SearxngUrl, UriKind.Absolute, out Uri? uri)
                            && LocalSearxngSearchProvider.IsAllowedConfiguredEndpoint(uri))
                            url = uri.AbsoluteUri;
                        foreach (string id in doc.DisabledProviders ?? new List<string>())
                            if (IsToggleable(id)) disabled.Add(id);
                    }
                }
            }
            catch
            {
                // A corrupt file falls back to the defaults (built-in providers on, no SearXNG).
            }
            return _cache = new Snapshot(url, disabled);
        }
    }

    /// <summary>Validates and persists. An endpoint outside loopback/private-LAN raises <see cref="ArgumentException"/>
    /// and nothing is stored.</summary>
    public Snapshot Set(string? searxngUrl, IEnumerable<string>? disabledProviders)
    {
        string url = string.Empty;
        if (!string.IsNullOrWhiteSpace(searxngUrl))
        {
            if (!Uri.TryCreate(searxngUrl.Trim(), UriKind.Absolute, out Uri? uri)
                || !LocalSearxngSearchProvider.IsAllowedConfiguredEndpoint(uri))
                throw new ArgumentException(
                    "The SearXNG address must be http(s) on this PC (127.0.0.1) or a private network address "
                    + "(10.x, 172.16-31.x, 192.168.x), with no path, login or query.");
            url = uri.AbsoluteUri;
        }

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in disabledProviders ?? Array.Empty<string>())
            if (IsToggleable(id)) disabled.Add(id);

        lock (_gate)
        {
            _cache = new Snapshot(url, disabled);
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                string temp = SettingPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(
                    new Document { SearxngUrl = url, DisabledProviders = disabled.OrderBy(x => x, StringComparer.Ordinal).ToList() }));
                File.Move(temp, SettingPath, true);
            }
            catch
            {
                // Best-effort persistence; the in-memory value still applies for this session.
            }
            return _cache;
        }
    }

    private static bool IsToggleable(string id) => id is DuckDuckGoId or WikipediaId;

    private sealed class Document
    {
        public string? SearxngUrl { get; set; }
        public List<string>? DisabledProviders { get; set; }
    }
}
