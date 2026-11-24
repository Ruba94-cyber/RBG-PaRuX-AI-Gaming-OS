using System.Net;
using System.Text;
using PaRuX.Server;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Readability-style page reading, the opt-in Brave provider and numbered web sources (synthetic pages, scripted transports).</summary>
public sealed class WebSearchParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-wsp-" + Guid.NewGuid().ToString("N"));
    public WebSearchParityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Description => "test";
        public string Protect(string purpose, string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string? Unprotect(string purpose, string blob) => Encoding.UTF8.GetString(Convert.FromBase64String(blob["enc:".Length..]));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private const string ArticlePage = @"<html><head><title>Synthetic Tides Guide</title><script>var track = 'SCRIPT_NOISE';</script></head>
<body>
<nav><a href='/'>Home</a> <a href='/a'>Products</a> <a href='/b'>Pricing</a> NAV_NOISE</nav>
<div class='sidebar'><p>Subscribe to our newsletter and follow us on every social network today, SIDEBAR_NOISE.</p></div>
<div id='content' class='post-content'>
<h1>How tides work</h1>
<p>Tides are caused mostly by the gravitational pull of the moon, and to a lesser degree by the sun, on the oceans of the earth.</p>
<p>Because the earth rotates beneath the bulges, most coasts see two high tides and two low tides each day, roughly six hours apart.</p>
<p>Local geography, depth, and weather can change the height and timing of tides, which is why published tables are specific to a port.</p>
</div>
<footer>Copyright FOOTER_NOISE</footer>
</body></html>";

    [Fact]
    public void Readability_keeps_the_article_and_drops_navigation_sidebar_footer_and_scripts()
    {
        ReadableArticle a = WebReadability.Extract(ArticlePage);
        Assert.Equal("Synthetic Tides Guide", a.Title);
        Assert.True(a.UsedMainContentHeuristic);
        Assert.Contains("gravitational pull of the moon", a.Text);
        Assert.Contains("two high tides", a.Text);
        Assert.Contains("# How tides work", a.Text);
        foreach (string noise in new[] { "NAV_NOISE", "SIDEBAR_NOISE", "FOOTER_NOISE", "SCRIPT_NOISE" })
            Assert.DoesNotContain(noise, a.Text);
    }

    [Fact]
    public void Readability_prefers_text_over_link_lists_and_is_bounded_on_hostile_markup()
    {
        string links = string.Concat(Enumerable.Range(0, 80).Select(i => "<li><a href='/x" + i + "'>Link number " + i + " to somewhere else entirely</a></li>"));
        string html = "<body><div class='menu2'><ul>" + links + "</ul></div><div><p>Real paragraph with enough words, commas, and substance to count as the article body here.</p></div></body>";
        ReadableArticle a = WebReadability.Extract(html);
        Assert.Contains("Real paragraph", a.Text);

        string deep = string.Concat(Enumerable.Repeat("<div>", 5000)) + "<p>deep text that is long enough to be scored as paragraph content</p>";
        ReadableArticle d = WebReadability.Extract(deep); // must not throw or overflow the stack
        Assert.NotNull(d);
        Assert.Equal(string.Empty, WebReadability.Extract("   ").Text);
    }

    [Fact]
    public void Readability_does_not_execute_or_keep_script_and_drops_hidden_blocks()
    {
        ReadableArticle a = WebReadability.Extract("<body><div hidden><p>HIDDEN_TEXT should never be read as it is not visible at all.</p></div><div style='display: none'><p>NONE_TEXT also invisible content here.</p></div><article><p>Visible article text, long enough, with commas, to be kept as the main content.</p></article><script>document.write('X')</script></body>");
        Assert.Contains("Visible article text", a.Text);
        Assert.DoesNotContain("HIDDEN_TEXT", a.Text);
        Assert.DoesNotContain("NONE_TEXT", a.Text);
        Assert.DoesNotContain("document.write", a.Text);
    }

    [Fact]
    public async Task Fetching_a_page_uses_main_content_extraction()
    {
        var internet = new InternetSettingsService(Path.Combine(_root, "i"));
        internet.SetEnabled(true);
        var handler = new ScriptedHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ArticlePage, Encoding.UTF8, "text/html") } };
        var web = new WebSearchService(internet, handler);
        string text = await web.FetchPageTextAsync("https://example.org/tides", CancellationToken.None);
        Assert.Contains("gravitational pull", text);
        Assert.DoesNotContain("NAV_NOISE", text);
        Assert.DoesNotContain("FOOTER_NOISE", text);
    }

    [Fact]
    public void Brave_results_are_parsed_and_unsafe_or_malformed_entries_are_dropped()
    {
        const string json = @"{""web"":{""results"":[
          {""url"":""https://example.org/a"",""title"":""A <strong>title</strong>"",""description"":""First &amp; best""},
          {""url"":""javascript:alert(1)"",""title"":""bad"",""description"":""x""},
          {""url"":""ftp://example.org/f"",""title"":""bad2""},
          {""title"":""no url""},
          {""url"":""https://example.org/b"",""title"":"""",""description"":""no title""}]}}";
        List<WebSearchResult> r = BraveSearchProvider.ParseResults(json, 10);
        Assert.Equal(2, r.Count);
        Assert.Equal("https://example.org/a", r[0].Url);
        Assert.DoesNotContain("<", r[0].Title);
        Assert.Equal("example.org", r[1].Title);
        Assert.Empty(BraveSearchProvider.ParseResults("{\"query\":{}}", 5));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BraveSearchProvider.ParseResults("not json", 5));
    }

    [Fact]
    public async Task Brave_joins_the_chain_only_with_a_key_sends_it_only_in_the_header_and_failure_falls_back()
    {
        const string key = "BSA_SYNTHETIC_KEY_0123456789";
        var settings = new WebSearchSettingsService(_root, new ConnectorSecretStore(_root, new PlainProtector(), "websearch-secrets.json"));
        var internet = new InternetSettingsService(Path.Combine(_root, "i2"));
        internet.SetEnabled(true);
        var handler = new ScriptedHandler();
        handler.Respond = r => r.RequestUri!.Host switch
        {
            "api.search.brave.com" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            "en.wikipedia.org" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"query\":{\"search\":[{\"title\":\"Tide\",\"snippet\":\"Rise and fall\"}]}}", Encoding.UTF8, "application/json") },
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };
        var web = new WebSearchService(internet, handler, settings);

        // No key: Brave is not contacted and is reported as not configured.
        await web.SearchAsync("tide", 3, CancellationToken.None);
        Assert.DoesNotContain(handler.Seen, r => r.RequestUri!.Host == "api.search.brave.com");
        Assert.Equal("not_configured", web.GetProvidersStatus().Providers.First(p => p.Id == "brave").State);

        settings.SetBraveKey(key);
        handler.Seen.Clear();
        List<WebSearchResult> results = await web.SearchAsync("tide", 3, CancellationToken.None);
        HttpRequestMessage brave = handler.Seen.First(r => r.RequestUri!.Host == "api.search.brave.com");
        Assert.Equal(key, brave.Headers.GetValues("X-Subscription-Token").Single());
        Assert.DoesNotContain(key, brave.RequestUri!.ToString());
        Assert.All(handler.Seen.Where(r => r.RequestUri!.Host != "api.search.brave.com"), r => Assert.False(r.Headers.Contains("X-Subscription-Token")));
        Assert.Equal("wikipedia", results[0].Provider); // rate limit on Brave: the chain went on
        Assert.Equal("failed", web.GetProvidersStatus().Providers.First(p => p.Id == "brave").State);
        Assert.DoesNotContain(key, System.Text.Json.JsonSerializer.Serialize(web.GetProvidersStatus()));

        foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("websearch-secrets.json")))
            Assert.DoesNotContain(key, File.ReadAllText(file));
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(_root, "websearch-secrets.json")));

        settings.SetBraveKey("");
        Assert.False(settings.HasBraveKey);
        Assert.Throws<ArgumentException>(() => settings.SetBraveKey("has space"));
    }

    [Fact]
    public void Web_sources_are_numbered_and_page_text_is_labelled_read_or_snippet_only()
    {
        var results = new List<WebSearchResult>
        {
            new() { Title = "Tides", Url = "https://example.org/tides", Snippet = "snippet one", Provider = "wikipedia" },
            new() { Title = "Moon", Url = "https://example.org/moon", Snippet = "snippet two", Provider = "duckduckgo" },
            new() { Title = "Sun", Url = "https://example.org/sun", Snippet = "snippet three", Provider = "duckduckgo" }
        };
        var pages = new string?[] { new string('a', 2000), new string('b', 5000), null };
        List<ContextEngineService.ContextCandidate> c = ContextEngineService.BuildNumberedWebCandidates(results, pages);

        Assert.Equal(3, c.Count);
        Assert.Equal("[1] https://example.org/tides (wikipedia)", c[0].Source);
        Assert.Equal("[2] Moon", c[1].Title);
        Assert.Contains("cite it as [2]", c[1].Content);
        Assert.Contains("page text read", c[0].Content);
        Assert.Contains("search snippet only", c[2].Content);
        Assert.Contains("snippet three", c[2].Content);
        Assert.True(c[1].Content.Length < 1_600, "later pages are capped harder than the first");
        Assert.All(c, x => Assert.StartsWith(WebSearchService.UntrustedContentHeader, x.Content));

        // The chat footer shows exactly these numbered sources.
        var pack = ContextEngineService.Assemble(new List<List<ContextEngineService.ContextCandidate>> { c }, 12_000);
        Assert.Equal(new[] { c[0].Source, c[1].Source, c[2].Source }, ChatProxyService.DisplaySources(pack));
    }
}
