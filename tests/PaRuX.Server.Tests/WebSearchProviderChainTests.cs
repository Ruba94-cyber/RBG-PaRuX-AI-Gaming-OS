using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Provider chain for keyless web search. Every transport is a fake handler; no network is used.</summary>
public sealed class WebSearchProviderChainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-wsp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private InternetSettingsService Internet(bool enabled)
    {
        var s = new InternetSettingsService(Path.Combine(_root, "i" + Guid.NewGuid().ToString("N")));
        s.SetEnabled(enabled);
        return s;
    }

    private WebSearchSettingsService Settings() => new(Path.Combine(_root, "s" + Guid.NewGuid().ToString("N")));

    private const string DdgHtml = """
        <div class="result"><a class="result__a" href="/l/?uddg=https%3A%2F%2Fexample.org%2Fa&amp;rut=1">Example A</a>
        <a class="result__snippet" href="#">Snippet A</a></div>
        """;

    private const string WikiJson = """
        {"query":{"search":[{"title":"Rome (city)","snippet":"The <span class=\"searchmatch\">history</span> of Rome &amp; more"}]}}
        """;

    private const string SearxJson = """
        {"results":[{"title":"Mine","url":"https://example.net/x","content":"from searx"}]}
        """;

    private static HttpResponseMessage Ok(string body, string type) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    private sealed class Fake : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<Uri> Requests { get; } = new();
        public Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(_respond(request));
        }
    }

    private static Fake Web(bool ddgWorks, bool wikiWorks) => new(r =>
    {
        string host = r.RequestUri!.Host;
        if (host.EndsWith("duckduckgo.com"))
            return ddgWorks ? Ok(DdgHtml, "text/html") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (host == "en.wikipedia.org")
            return wikiWorks ? Ok(WikiJson, "application/json") : new HttpResponseMessage(HttpStatusCode.BadGateway);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task First_provider_answers_and_later_providers_are_not_contacted()
    {
        Fake web = Web(ddgWorks: true, wikiWorks: true);
        var svc = new WebSearchService(Internet(true), web, Settings());

        var results = await svc.SearchAsync("rome", 5, CancellationToken.None);

        Assert.Equal("duckduckgo", Assert.Single(results).Provider);
        Assert.DoesNotContain(web.Requests, u => u.Host == "en.wikipedia.org");
        Assert.Equal("duckduckgo", svc.GetProvidersStatus().LastProvider);
    }

    [Fact]
    public async Task A_failing_provider_falls_back_to_wikipedia_and_the_failure_is_reported()
    {
        Fake web = Web(ddgWorks: false, wikiWorks: true);
        var svc = new WebSearchService(Internet(true), web, Settings());

        var results = await svc.SearchAsync("history of rome", 5, CancellationToken.None);

        WebSearchResult hit = Assert.Single(results);
        Assert.Equal("wikipedia", hit.Provider);
        Assert.Equal("https://en.wikipedia.org/wiki/Rome_(city)", hit.Url);
        Assert.Equal("The history of Rome & more", hit.Snippet);
        Assert.All(web.Requests, u => Assert.Equal("https", u.Scheme));

        var status = svc.GetProvidersStatus();
        Assert.Equal("wikipedia", status.LastProvider);
        Assert.Equal("failed", status.Providers.Single(p => p.Id == "duckduckgo").State);
        Assert.Equal("ok", status.Providers.Single(p => p.Id == "wikipedia").State);
        Assert.Equal("not_configured", status.Providers.Single(p => p.Id == "searxng").State);
    }

    [Fact]
    public async Task Only_the_query_text_reaches_wikipedia()
    {
        Fake web = Web(ddgWorks: false, wikiWorks: true);
        var svc = new WebSearchService(Internet(true), web, Settings());
        await svc.SearchAsync("a b&c", 5, CancellationToken.None);

        Uri wiki = web.Requests.Single(u => u.Host == "en.wikipedia.org");
        Assert.Contains("srsearch=a%20b%26c", wiki.Query);
        Assert.DoesNotContain("token", wiki.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task When_every_provider_fails_the_error_names_each_one()
    {
        var svc = new WebSearchService(Internet(true), Web(false, false), Settings());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => svc.SearchAsync("x", 5, CancellationToken.None));

        Assert.Contains("DuckDuckGo", ex.Message);
        Assert.Contains("Wikipedia", ex.Message);
    }

    private const string MarginaliaJson = """
        {"license":"x","results":[{"url":"https://small.example/page","title":"Small <b>web</b> page","description":"An &amp; indie page"},{"url":"javascript:alert(1)","title":"bad"}]}
        """;

    [Fact]
    public async Task Marginalia_answers_when_duckduckgo_fails_and_is_tried_before_wikipedia()
    {
        var web = new Fake(r => r.RequestUri!.Host switch
        {
            "api.marginalia.nu" => Ok(MarginaliaJson, "application/json"),
            "en.wikipedia.org" => Ok(WikiJson, "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        var svc = new WebSearchService(Internet(true), web, Settings());

        WebSearchResult hit = Assert.Single(await svc.SearchAsync("a b&c", 5, CancellationToken.None));

        Assert.Equal("marginalia", hit.Provider);
        Assert.Equal("https://small.example/page", hit.Url);
        Assert.Equal("Small web page", hit.Title);
        Assert.Equal("An & indie page", hit.Snippet);
        Uri request = web.Requests.Single(u => u.Host == "api.marginalia.nu");
        Assert.Equal("https", request.Scheme);
        Assert.Contains("a%20b%26c", request.AbsoluteUri);
        Assert.DoesNotContain(web.Requests, u => u.Host == "en.wikipedia.org");
        Assert.Equal("ok", svc.GetProvidersStatus().Providers.Single(p => p.Id == "marginalia").State);
    }

    [Fact]
    public async Task Marginalia_can_be_turned_off_and_a_malformed_answer_is_a_failure_that_falls_through()
    {
        WebSearchSettingsService settings = Settings();
        settings.Set(null, new[] { "marginalia" });
        Fake web = Web(ddgWorks: false, wikiWorks: true);
        var svc = new WebSearchService(Internet(true), web, settings);
        Assert.Equal("wikipedia", (await svc.SearchAsync("x", 5, CancellationToken.None)).Single().Provider);
        Assert.DoesNotContain(web.Requests, u => u.Host == "api.marginalia.nu");
        Assert.Equal("disabled", svc.GetProvidersStatus().Providers.Single(p => p.Id == "marginalia").State);

        settings.Set(null, Array.Empty<string>());
        var bad = new Fake(r => r.RequestUri!.Host switch
        {
            "api.marginalia.nu" => Ok("[1,2]", "application/json"),
            "en.wikipedia.org" => Ok(WikiJson, "application/json"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        var svc2 = new WebSearchService(Internet(true), bad, settings);
        Assert.Equal("wikipedia", (await svc2.SearchAsync("x", 5, CancellationToken.None)).Single().Provider);
        Assert.Equal("failed", svc2.GetProvidersStatus().Providers.Single(p => p.Id == "marginalia").State);
    }

    [Fact]
    public async Task A_provider_that_answers_with_no_results_is_an_honest_empty_list_not_an_error()
    {
        var web = new Fake(r => r.RequestUri!.Host == "en.wikipedia.org"
            ? Ok("{\"query\":{\"search\":[]}}", "application/json")
            : Ok("<html>nothing</html>", "text/html"));
        var svc = new WebSearchService(Internet(true), web, Settings());

        Assert.Empty(await svc.SearchAsync("zzzz", 5, CancellationToken.None));
        Assert.Equal("no_results", svc.GetProvidersStatus().Providers.Single(p => p.Id == "wikipedia").State);
    }

    [Fact]
    public async Task Disabled_providers_are_skipped_and_all_disabled_is_reported()
    {
        WebSearchSettingsService settings = Settings();
        settings.Set(null, new[] { "duckduckgo" });
        Fake web = Web(true, true);
        var svc = new WebSearchService(Internet(true), web, settings);

        Assert.Equal("wikipedia", (await svc.SearchAsync("x", 5, CancellationToken.None)).Single().Provider);
        Assert.DoesNotContain(web.Requests, u => u.Host.EndsWith("duckduckgo.com"));
        Assert.Equal("disabled", svc.GetProvidersStatus().Providers.Single(p => p.Id == "duckduckgo").State);

        settings.Set(null, new[] { "duckduckgo", "wikipedia", "marginalia" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SearchAsync("x", 5, CancellationToken.None));
    }

    [Fact]
    public async Task Configured_searxng_answers_first_on_its_own_transport()
    {
        WebSearchSettingsService settings = Settings();
        settings.Set("http://192.168.1.20:8080/", null);
        Fake web = Web(true, true);
        var searx = new Fake(r => Ok(SearxJson, "application/json"));
        var svc = new WebSearchService(Internet(true), web, settings, searx);

        var results = await svc.SearchAsync("q", 5, CancellationToken.None);

        Assert.Equal("searxng", Assert.Single(results).Provider);
        Assert.Empty(web.Requests);
        Assert.Equal("192.168.1.20", searx.Requests.Single().Host);
        Assert.Equal("/search", searx.Requests.Single().AbsolutePath);
    }

    [Fact]
    public async Task A_broken_searxng_falls_back_without_disabling_search()
    {
        WebSearchSettingsService settings = Settings();
        settings.Set("http://127.0.0.1:8080/", null);
        var searx = new Fake(r => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var svc = new WebSearchService(Internet(true), Web(true, true), settings, searx);

        Assert.Equal("duckduckgo", (await svc.SearchAsync("q", 5, CancellationToken.None)).Single().Provider);
        var p = svc.GetProvidersStatus().Providers.Single(x => x.Id == "searxng");
        Assert.Equal("failed", p.State);
        Assert.Contains("403", p.Detail);
    }

    [Fact]
    public async Task A_searxng_redirect_or_non_json_body_is_a_failure_not_followed()
    {
        WebSearchSettingsService settings = Settings();
        settings.Set("http://10.0.0.5:8080/", null);
        var searx = new Fake(r => Ok("<html>login</html>", "text/html"));
        var svc = new WebSearchService(Internet(true), Web(true, true), settings, searx);

        await svc.SearchAsync("q", 5, CancellationToken.None);

        Assert.Equal("failed", svc.GetProvidersStatus().Providers.Single(x => x.Id == "searxng").State);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8888/", true)]
    [InlineData("http://192.168.0.2:8888", true)]
    [InlineData("https://10.1.2.3/", true)]
    [InlineData("http://172.16.0.9:80/", true)]
    [InlineData("http://172.32.0.9/", false)]
    [InlineData("http://8.8.8.8/", false)]
    [InlineData("http://169.254.1.1/", false)]
    [InlineData("http://example.com/", false)]
    [InlineData("http://localhost:8888/", false)]
    [InlineData("http://192.168.0.2/search", false)]
    [InlineData("http://192.168.0.2/?q=1", false)]
    [InlineData("http://user:pw@192.168.0.2/", false)]
    [InlineData("ftp://192.168.0.2/", false)]
    [InlineData("http://[::1]:8888/", false)]
    public void Searxng_endpoint_is_limited_to_loopback_and_private_lan(string url, bool allowed)
    {
        Assert.Equal(allowed, LocalSearxngSearchProvider.IsAllowedConfiguredEndpoint(new Uri(url)));
        var settings = Settings();
        if (allowed) Assert.NotEmpty(settings.Set(url, null).SearxngUrl);
        else Assert.Throws<ArgumentException>(() => settings.Set(url, null));
    }

    [Fact]
    public void Settings_persist_and_ignore_unknown_provider_ids()
    {
        string dir = Path.Combine(_root, "persist");
        new WebSearchSettingsService(dir).Set("http://127.0.0.1:9000/", new[] { "wikipedia", "bogus", "searxng" });

        var again = new WebSearchSettingsService(dir).Get();

        Assert.Equal("http://127.0.0.1:9000/", again.SearxngUrl);
        Assert.Equal(new[] { "wikipedia" }, again.DisabledProviders.ToArray());
    }

    [Fact]
    public async Task Internet_off_contacts_no_provider_including_searxng()
    {
        WebSearchSettingsService settings = Settings();
        settings.Set("http://127.0.0.1:8080/", null);
        Fake web = Web(true, true);
        var searx = new Fake(r => Ok(SearxJson, "application/json"));
        var svc = new WebSearchService(Internet(false), web, settings, searx);

        await Assert.ThrowsAsync<InternetAccessDisabledException>(() => svc.SearchAsync("q", 5, CancellationToken.None));

        Assert.Empty(web.Requests);
        Assert.Empty(searx.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_stops_the_chain_instead_of_trying_the_next_provider()
    {
        Fake web = Web(true, true);
        var svc = new WebSearchService(Internet(true), web, Settings());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.SearchAsync("q", 5, cts.Token));

        Assert.Empty(web.Requests);
    }

    [Fact]
    public void Wikipedia_parser_rejects_unexpected_shapes_and_bounds_results()
    {
        Assert.Throws<InvalidDataException>(() => WikipediaSearchProvider.ParseResults("{\"x\":1}", 5));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WikipediaSearchProvider.ParseResults("<html>", 5));
        string many = "{\"query\":{\"search\":[" + string.Join(",", Enumerable.Range(0, 20)
            .Select(i => "{\"title\":\"T" + i + "\",\"snippet\":\"s\"}")) + "]}}";
        Assert.Equal(3, WikipediaSearchProvider.ParseResults(many, 3).Count);
    }
}
