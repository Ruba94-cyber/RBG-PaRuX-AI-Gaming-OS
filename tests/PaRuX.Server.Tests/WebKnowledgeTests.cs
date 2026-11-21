using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Websites as Local Knowledge sources. A loopback test site stands in for the internet; the crawler's address rule is
/// switched to "loopback only" for those tests, while dedicated tests prove the production rule (public addresses only).
/// </summary>
public sealed class WebKnowledgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-web-" + Guid.NewGuid().ToString("N"));
    private readonly List<TestSite> _sites = new();

    public WebKnowledgeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (TestSite s in _sites) s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record Reply(int Status, string ContentType, string Body, Dictionary<string, string>? Headers = null, int DelayMs = 0);

    private sealed class TestSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        public ConcurrentDictionary<string, Func<HttpListenerRequest, Reply>> Routes { get; } = new();
        public ConcurrentDictionary<string, int> Hits { get; } = new();
        public string Origin { get; }
        public int TotalHits => Hits.Values.Sum();

        public TestSite()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            Origin = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch { return; }
                    _ = Task.Run(() => Serve(c));
                }
            });
        }

        public void Html(string path, string body, Dictionary<string, string>? headers = null) =>
            Routes[path] = _ => new Reply(200, "text/html; charset=utf-8", body, headers);

        private async Task Serve(HttpListenerContext c)
        {
            string path = c.Request.Url!.AbsolutePath;
            Hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            try
            {
                Reply reply = Routes.TryGetValue(path, out var handler) ? handler(c.Request) : new Reply(404, "text/plain", "missing");
                if (reply.DelayMs > 0) await Task.Delay(reply.DelayMs);
                c.Response.StatusCode = reply.Status;
                c.Response.ContentType = reply.ContentType;
                if (reply.Headers != null) foreach (var h in reply.Headers) c.Response.Headers[h.Key] = h.Value;
                byte[] bytes = Encoding.UTF8.GetBytes(reply.Body);
                c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
            catch { try { c.Response.Abort(); } catch { } }
        }

        public void Dispose() { try { _listener.Abort(); } catch { } }
    }

    private TestSite Site()
    {
        var s = new TestSite();
        _sites.Add(s);
        return s;
    }

    private static string Page(string title, string body, params string[] links) =>
        "<html><head><title>" + title + "</title><script>var secret='SCRIPTTEXT';</script><style>.x{}</style></head><body>" +
        "<nav>NAVTEXT menu</nav><h1>" + title + "</h1><p>" + body + "</p>" +
        string.Concat(links.Select(l => "<a href=\"" + l + "\">link</a>")) + "<footer>FOOTERTEXT</footer></body></html>";

    private static bool Loopback(string host, IPAddress a) => IPAddress.IsLoopback(a);

    private WebKnowledgeCrawler Crawler(WebCrawlOptions options, Func<bool>? gaming = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<string, IPAddress, bool>? rule = null) =>
        new(options with { PoliteDelay = TimeSpan.Zero }, rule ?? Loopback, null, gaming ?? (() => false), delay ?? ((_, _) => Task.CompletedTask));

    private (string Folder, WebSourceManifest Manifest) NewFolder()
    {
        string folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        return (folder, new WebSourceManifest { Id = "t", Folder = folder });
    }

    // ------------------------------------------------------------------ single page and extraction

    [Fact]
    public async Task A_single_page_is_saved_as_text_with_its_address_and_without_scripts_or_menus()
    {
        TestSite site = Site();
        site.Html("/guide.html", Page("Pump guide", "Replace the seal &amp; check the <b>valve</b> yearly.", "/other.html"));
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport report = await Crawler(WebCrawlOptions.SinglePage).CrawlAsync(new Uri(site.Origin + "/guide.html"), folder, manifest, default);

        Assert.Equal(1, report.Written);
        Assert.Equal("done", report.StoppedBecause);
        string file = Assert.Single(Directory.GetFiles(folder));
        string text = File.ReadAllText(file);
        Assert.StartsWith("# Pump guide", text);
        Assert.Contains("Source: " + site.Origin + "/guide.html", text);
        Assert.Contains("Replace the seal & check the valve yearly.", text);
        Assert.DoesNotContain("SCRIPTTEXT", text);
        Assert.DoesNotContain("NAVTEXT", text);
        Assert.DoesNotContain("FOOTERTEXT", text);
        Assert.Equal(0, site.Hits.GetValueOrDefault("/other.html")); // single page never follows links
    }

    [Fact]
    public async Task Plain_text_and_noindex_and_non_text_content_are_handled_honestly()
    {
        TestSite site = Site();
        site.Routes["/note.txt"] = _ => new Reply(200, "text/plain", "Plain text note about calibration of the sensor.");
        site.Html("/hidden.html", "<html><head><meta name=\"robots\" content=\"noindex\"></head><body><p>Do not index this page at all please.</p></body></html>");
        site.Routes["/image.png"] = _ => new Reply(200, "image/png", "notpng");
        (string folder, WebSourceManifest manifest) = NewFolder();

        var crawler = Crawler(WebCrawlOptions.SinglePage);
        Assert.Equal(1, (await crawler.CrawlAsync(new Uri(site.Origin + "/note.txt"), folder, manifest, default)).Written);
        WebCrawlReport hidden = await crawler.CrawlAsync(new Uri(site.Origin + "/hidden.html"), folder, manifest, default);
        WebCrawlReport image = await crawler.CrawlAsync(new Uri(site.Origin + "/image.png"), folder, manifest, default);

        Assert.Equal("noindex", Assert.Single(hidden.Pages).Reason);
        Assert.Equal("unsupported_content_type", Assert.Single(image.Pages).Reason);
        Assert.Single(Directory.GetFiles(folder));
    }

    // ------------------------------------------------------------------ crawl scope and limits

    [Fact]
    public async Task A_crawl_stays_on_the_host_and_below_the_start_folder_and_stops_at_the_page_limit()
    {
        TestSite site = Site();
        site.Html("/docs/index.html", Page("Index", "Start page with enough text to keep.", "/docs/a.html", "/docs/b.html", "/other/z.html", "http://example.org/docs/ext.html", "#top"));
        site.Html("/docs/a.html", Page("Page A", "Alpha content with enough words to be kept.", "/docs/c.html"));
        site.Html("/docs/b.html", Page("Page B", "Beta content with enough words to be kept."));
        site.Html("/docs/c.html", Page("Page C", "Gamma content with enough words to be kept."));
        site.Html("/other/z.html", Page("Z", "Outside the start folder entirely, long enough."));
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport report = await Crawler(WebCrawlOptions.Crawl(10, 2)).CrawlAsync(new Uri(site.Origin + "/docs/index.html"), folder, manifest, default);

        Assert.Equal(4, report.Written);
        Assert.Equal(0, site.Hits.GetValueOrDefault("/other/z.html"));
        Assert.Equal("done", report.StoppedBecause);

        (string folder2, WebSourceManifest manifest2) = NewFolder();
        WebCrawlReport limited = await Crawler(WebCrawlOptions.Crawl(2, 2)).CrawlAsync(new Uri(site.Origin + "/docs/index.html"), folder2, manifest2, default);
        Assert.Equal(2, limited.Written);
        Assert.Equal("page_limit", limited.StoppedBecause);

        (string folder3, WebSourceManifest manifest3) = NewFolder();
        WebCrawlReport shallow = await Crawler(WebCrawlOptions.Crawl(10, 1)).CrawlAsync(new Uri(site.Origin + "/docs/index.html"), folder3, manifest3, default);
        Assert.Equal(3, shallow.Written); // depth 1: index + a + b, not c
    }

    [Fact]
    public void Hard_ceilings_cap_what_a_person_can_ask_for()
    {
        WebCrawlOptions big = WebCrawlOptions.Crawl(100000, 99);
        Assert.Equal(WebCrawlOptions.HardMaxPages, big.MaxPages);
        Assert.Equal(WebCrawlOptions.HardMaxDepth, big.MaxDepth);
        Assert.Equal(1, WebCrawlOptions.Crawl(-5, -1).MaxPages);
    }

    [Fact]
    public async Task Big_slow_and_endless_responses_are_cut_off()
    {
        TestSite site = Site();
        site.Html("/big.html", "<html><body>" + new string('x', 2000) + "</body></html>");
        site.Routes["/slow.html"] = _ => new Reply(200, "text/html", Page("Slow", "Slow page text that is long enough to keep."), null, DelayMs: 3000);
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport big = await Crawler(WebCrawlOptions.SinglePage with { MaxBytesPerPage = 500 })
            .CrawlAsync(new Uri(site.Origin + "/big.html"), folder, manifest, default);
        Assert.Equal("too_large", Assert.Single(big.Pages).Reason);

        WebCrawlReport slow = await Crawler(WebCrawlOptions.SinglePage with { RequestTimeout = TimeSpan.FromMilliseconds(300) })
            .CrawlAsync(new Uri(site.Origin + "/slow.html"), folder, manifest, default);
        Assert.Equal("timeout", Assert.Single(slow.Pages).Reason);

        WebCrawlReport total = await Crawler(WebCrawlOptions.Crawl(5, 1) with { TotalTimeout = TimeSpan.FromMilliseconds(300) })
            .CrawlAsync(new Uri(site.Origin + "/slow.html"), folder, manifest, default);
        Assert.Equal("timeout", total.StoppedBecause);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task The_total_byte_budget_stops_the_crawl()
    {
        TestSite site = Site();
        string filler = string.Join(" ", Enumerable.Repeat("word", 400));
        site.Html("/p/index.html", Page("I", filler, "/p/a.html", "/p/b.html", "/p/c.html"));
        foreach (string n in new[] { "a", "b", "c" }) site.Html("/p/" + n + ".html", Page(n, filler));
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport report = await Crawler(WebCrawlOptions.Crawl(10, 1) with { MaxTotalBytes = 3000 })
            .CrawlAsync(new Uri(site.Origin + "/p/index.html"), folder, manifest, default);

        Assert.Equal("byte_limit", report.StoppedBecause);
        Assert.True(report.Written < 4);
    }

    // ------------------------------------------------------------------ robots.txt

    [Fact]
    public async Task Robots_rules_are_honoured_and_blocked_pages_are_never_requested()
    {
        TestSite site = Site();
        site.Routes["/robots.txt"] = _ => new Reply(200, "text/plain",
            "User-agent: *\nDisallow: /docs/private/\nAllow: /docs/private/open.html\n\nUser-agent: SomeOtherBot\nDisallow: /\n");
        site.Html("/docs/index.html", Page("Index", "Index text that is long enough to be kept here.", "/docs/private/secret.html", "/docs/private/open.html", "/docs/ok.html"));
        site.Html("/docs/private/secret.html", Page("Secret", "Secret text that must never be fetched at all."));
        site.Html("/docs/private/open.html", Page("Open", "Explicitly allowed text inside a disallowed folder."));
        site.Html("/docs/ok.html", Page("Ok", "Normal page text that is long enough to be kept."));
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport report = await Crawler(WebCrawlOptions.Crawl(10, 1)).CrawlAsync(new Uri(site.Origin + "/docs/index.html"), folder, manifest, default);

        Assert.Equal(0, site.Hits.GetValueOrDefault("/docs/private/secret.html"));
        Assert.Contains(report.Pages, p => p.Reason == "blocked_by_robots" && p.Url.EndsWith("secret.html"));
        Assert.Equal(3, report.Written);
    }

    [Fact]
    public async Task A_robots_file_for_this_bot_can_block_the_whole_site()
    {
        TestSite site = Site();
        site.Routes["/robots.txt"] = _ => new Reply(200, "text/plain", "User-agent: PaRuXKnowledgeBot\nDisallow: /\n");
        site.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport report = await Crawler(WebCrawlOptions.SinglePage).CrawlAsync(new Uri(site.Origin + "/a.html"), folder, manifest, default);

        Assert.Equal(0, site.Hits.GetValueOrDefault("/a.html"));
        Assert.Equal("blocked_by_robots", Assert.Single(report.Pages).Reason);
    }

    [Fact]
    public async Task A_missing_robots_file_allows_everything_but_a_server_error_blocks_everything()
    {
        TestSite open = Site();
        open.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        (string folderA, WebSourceManifest manifestA) = NewFolder();
        Assert.Equal(1, (await Crawler(WebCrawlOptions.SinglePage).CrawlAsync(new Uri(open.Origin + "/a.html"), folderA, manifestA, default)).Written);

        TestSite broken = Site();
        broken.Routes["/robots.txt"] = _ => new Reply(503, "text/plain", "down");
        broken.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        (string folderB, WebSourceManifest manifestB) = NewFolder();
        WebCrawlReport report = await Crawler(WebCrawlOptions.SinglePage).CrawlAsync(new Uri(broken.Origin + "/a.html"), folderB, manifestB, default);

        Assert.Equal("robots_unavailable", Assert.Single(report.Pages).Reason);
        Assert.Equal(0, broken.Hits.GetValueOrDefault("/a.html"));
    }

    [Fact]
    public async Task A_crawl_delay_is_respected_up_to_a_ceiling()
    {
        TestSite site = Site();
        site.Routes["/robots.txt"] = _ => new Reply(200, "text/plain", "User-agent: *\nCrawl-delay: 3\n");
        site.Html("/d/index.html", Page("I", "Index text long enough to be kept here.", "/d/a.html"));
        site.Html("/d/a.html", Page("A", "Page A text long enough to be kept here."));
        var waits = new List<TimeSpan>();
        (string folder, WebSourceManifest manifest) = NewFolder();

        await Crawler(WebCrawlOptions.Crawl(5, 1), delay: (span, _) => { waits.Add(span); return Task.CompletedTask; })
            .CrawlAsync(new Uri(site.Origin + "/d/index.html"), folder, manifest, default);

        Assert.Contains(TimeSpan.FromSeconds(3), waits);
        Assert.DoesNotContain(waits, w => w > TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("User-agent: *\nDisallow: /a\nAllow: /a/b", "/a/b/c", true)]       // the longer rule wins
    [InlineData("User-agent: *\nDisallow: /a\nAllow: /a/b", "/a/x", false)]
    [InlineData("User-agent: *\nDisallow: /*.pdf$", "/files/x.pdf", false)]
    [InlineData("User-agent: *\nDisallow: /*.pdf$", "/files/x.pdf?download=1", true)]
    [InlineData("User-agent: *\nDisallow:", "/anything", true)]                      // an empty Disallow allows all
    [InlineData("User-agent: *\nDisallow: /x # comment", "/x/y", false)]
    [InlineData("", "/x", true)]
    public void Robots_matching_follows_the_standard(string file, string path, bool expected)
    {
        Assert.Equal(expected, RobotsRules.Parse(file, "paruxknowledgebot").IsAllowed(path));
    }

    // ------------------------------------------------------------------ SSRF and redirects

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:8080/x")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.16.0.9/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("")]
    [InlineData("not a url")]
    public void Unsafe_addresses_are_refused_before_any_request(string url)
    {
        Assert.False(WebUrl.TryValidate(url, WebAddressRule.PublicOnly, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/docs/page.html?x=1")]
    [InlineData("https://93.184.216.34/")]
    public void Public_addresses_pass_validation(string url) =>
        Assert.True(WebUrl.TryValidate(url, WebAddressRule.PublicOnly, out Uri uri, out _) && uri.IsAbsoluteUri);

    [Fact]
    public async Task The_production_rule_blocks_loopback_even_for_a_plain_host_name()
    {
        TestSite site = Site();
        site.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        // No custom rule: production default (public addresses only). "localhost" resolves to loopback and is refused at connect time.
        var crawler = new WebKnowledgeCrawler(WebCrawlOptions.SinglePage, gamingActive: () => false, delay: (_, _) => Task.CompletedTask);

        WebCrawlReport report = await crawler.CrawlAsync(new Uri(site.Origin.Replace("127.0.0.1", "localhost") + "/a.html"), folder, manifest, default);

        Assert.Equal(0, site.TotalHits);
        Assert.Equal(0, report.Written);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task A_public_name_that_resolves_to_a_private_address_is_blocked_without_a_request()
    {
        TestSite site = Site();
        site.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        ValidatedRemoteConnector.HostResolver rebinding = (host, ct) => Task.FromResult(new[] { IPAddress.Loopback });
        var crawler = new WebKnowledgeCrawler(WebCrawlOptions.SinglePage, WebAddressRule.PublicOnly, rebinding, () => false, (_, _) => Task.CompletedTask);
        string port = new Uri(site.Origin).Port.ToString();

        WebCrawlReport report = await crawler.CrawlAsync(new Uri("http://docs.example.test:" + port + "/a.html"), folder, manifest, default);

        Assert.Equal(0, site.TotalHits);
        Assert.Equal(0, report.Written);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_not_followed_but_one_on_the_same_host_is()
    {
        TestSite target = Site();
        target.Html("/landing.html", Page("Landing", "Landing text that is long enough to be kept here."));
        TestSite site = Site();
        site.Routes["/away.html"] = _ => new Reply(302, "text/plain", "", new() { ["Location"] = target.Origin + "/landing.html" });
        site.Routes["/old.html"] = _ => new Reply(301, "text/plain", "", new() { ["Location"] = "/new.html" });
        site.Html("/new.html", Page("New", "New location text that is long enough to be kept."));
        site.Routes["/loop.html"] = _ => new Reply(302, "text/plain", "", new() { ["Location"] = "/loop.html" });
        (string folder, WebSourceManifest manifest) = NewFolder();
        var crawler = Crawler(WebCrawlOptions.SinglePage);

        WebCrawlReport away = await crawler.CrawlAsync(new Uri(site.Origin + "/away.html"), folder, manifest, default);
        WebCrawlReport moved = await crawler.CrawlAsync(new Uri(site.Origin + "/old.html"), folder, manifest, default);
        WebCrawlReport loop = await crawler.CrawlAsync(new Uri(site.Origin + "/loop.html"), folder, manifest, default);

        Assert.Equal("redirected_to_another_site", Assert.Single(away.Pages).Reason);
        Assert.Equal(0, target.TotalHits);
        Assert.Equal(1, moved.Written);
        Assert.Equal("too_many_redirects", Assert.Single(loop.Pages).Reason);
    }

    // ------------------------------------------------------------------ gaming

    [Fact]
    public async Task Gaming_mode_stops_a_crawl_before_the_first_request()
    {
        TestSite site = Site();
        site.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport report = await Crawler(WebCrawlOptions.SinglePage, gaming: () => true).CrawlAsync(new Uri(site.Origin + "/a.html"), folder, manifest, default);

        Assert.Equal("gaming", report.StoppedBecause);
        Assert.Equal(0, site.TotalHits);
    }

    // ------------------------------------------------------------------ re-sync

    [Fact]
    public async Task Resync_skips_unchanged_pages_rewrites_changed_ones_and_keeps_edited_copies()
    {
        TestSite site = Site();
        int version = 1;
        site.Routes["/r/index.html"] = req =>
        {
            if (req.Headers["If-None-Match"] == "\"v1\"" && version == 1) return new Reply(304, "text/html", "");
            return new Reply(200, "text/html", Page("Index", "Index text long enough to be kept here.", "/r/a.html", "/r/b.html"), new() { ["ETag"] = "\"v" + version + "\"" });
        };
        site.Html("/r/a.html", Page("A", "Alpha version one text that is long enough."));
        site.Html("/r/b.html", Page("B", "Beta version one text that is long enough."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        var crawler = Crawler(WebCrawlOptions.Crawl(10, 1));
        Uri start = new(site.Origin + "/r/index.html");

        WebCrawlReport first = await crawler.CrawlAsync(start, folder, manifest, default);
        Assert.Equal(3, first.Written);

        WebCrawlReport second = await crawler.CrawlAsync(start, folder, manifest, default);
        Assert.Equal(3, second.Unchanged);
        Assert.Equal(0, second.Written);
        Assert.Contains(second.Pages, p => p.Reason == "not_modified");   // index answered 304 and its links were still followed

        // a changed, and an edited-by-the-person, page
        site.Html("/r/a.html", Page("A", "Alpha version TWO text that is long enough."));
        site.Html("/r/b.html", Page("B", "Beta version TWO text that is long enough."));
        string bFile = Path.Combine(folder, manifest.Pages.Values.Single(p => p.Url.EndsWith("/b.html")).FileName);
        File.AppendAllText(bFile, "\nMy own note about this page.\n");

        WebCrawlReport third = await crawler.CrawlAsync(start, folder, manifest, default);

        Assert.Equal(1, third.Written);
        Assert.Equal(1, third.KeptEdited);
        Assert.Contains("version TWO", File.ReadAllText(Path.Combine(folder, manifest.Pages.Values.Single(p => p.Url.EndsWith("/a.html")).FileName)));
        string kept = File.ReadAllText(bFile);
        Assert.Contains("My own note", kept);
        Assert.Contains("version one", kept);   // not overwritten
    }

    [Fact]
    public async Task A_page_that_disappeared_is_removed_only_if_the_person_did_not_edit_it()
    {
        TestSite site = Site();
        site.Html("/g/index.html", Page("Index", "Index text long enough to be kept here.", "/g/a.html", "/g/b.html"));
        site.Html("/g/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        site.Html("/g/b.html", Page("B", "Beta text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        var crawler = Crawler(WebCrawlOptions.Crawl(10, 1));
        Uri start = new(site.Origin + "/g/index.html");
        await crawler.CrawlAsync(start, folder, manifest, default);
        string aFile = Path.Combine(folder, manifest.Pages.Values.Single(p => p.Url.EndsWith("/a.html")).FileName);
        string bFile = Path.Combine(folder, manifest.Pages.Values.Single(p => p.Url.EndsWith("/b.html")).FileName);
        File.AppendAllText(bFile, "\nedited\n");

        site.Html("/g/index.html", Page("Index", "Index text long enough to be kept here."));   // both links gone
        site.Routes.TryRemove("/g/a.html", out _);                                              // and the site now answers 404
        site.Routes.TryRemove("/g/b.html", out _);
        WebCrawlReport report = await crawler.CrawlAsync(start, folder, manifest, default);

        Assert.Equal(1, report.Removed);
        Assert.False(File.Exists(aFile));
        Assert.True(File.Exists(bFile));
    }

    [Fact]
    public async Task A_stopped_crawl_never_forgets_pages_it_simply_did_not_reach()
    {
        TestSite site = Site();
        site.Html("/h/index.html", Page("Index", "Index text long enough to be kept here.", "/h/a.html"));
        site.Html("/h/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        Uri start = new(site.Origin + "/h/index.html");
        await Crawler(WebCrawlOptions.Crawl(10, 1)).CrawlAsync(start, folder, manifest, default);

        WebCrawlReport limited = await Crawler(WebCrawlOptions.Crawl(1, 1)).CrawlAsync(start, folder, manifest, default);

        Assert.Equal("page_limit", limited.StoppedBecause);
        Assert.Equal(0, limited.Removed);
        Assert.Equal(2, manifest.Pages.Count);
    }

    [Fact]
    public void File_names_are_safe_unique_and_stable()
    {
        string a = WebKnowledgeCrawler.FileNameFor("https://example.com/docs/a b?x=1");
        string b = WebKnowledgeCrawler.FileNameFor("https://example.com/docs/a b?x=2");
        Assert.NotEqual(a, b);
        Assert.Equal(a, WebKnowledgeCrawler.FileNameFor("https://example.com/docs/a b?x=1"));
        Assert.EndsWith(".md", a);
        Assert.DoesNotContain(a, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.True(WebKnowledgeCrawler.FileNameFor("https://example.com/" + new string('z', 500)).Length < 120);
    }


    // ------------------------------------------------------------------ review fixes

    [Fact]
    public async Task A_robots_outage_on_resync_keeps_every_saved_page()
    {
        TestSite site = Site();
        bool robotsDown = false;
        site.Routes["/robots.txt"] = _ => robotsDown ? new Reply(503, "text/plain", "down") : new Reply(404, "text/plain", "");
        site.Html("/k/index.html", Page("Index", "Index text long enough to be kept here.", "/k/a.html"));
        site.Html("/k/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        var crawler = Crawler(WebCrawlOptions.Crawl(10, 1));
        Uri start = new(site.Origin + "/k/index.html");
        await crawler.CrawlAsync(start, folder, manifest, default);
        string[] files = Directory.GetFiles(folder);
        Assert.Equal(2, files.Length);

        robotsDown = true;
        WebCrawlReport report = await crawler.CrawlAsync(start, folder, manifest, default);

        Assert.Equal(0, report.Removed);
        Assert.Equal(files.OrderBy(f => f), Directory.GetFiles(folder).OrderBy(f => f));
        Assert.Equal(2, manifest.Pages.Count);
    }

    [Fact]
    public async Task A_robots_redirect_or_timeout_also_counts_as_unreadable_and_prunes_nothing()
    {
        TestSite site = Site();
        string mode = "ok";
        site.Routes["/robots.txt"] = _ => mode == "redirect"
            ? new Reply(302, "text/plain", "", new() { ["Location"] = "/elsewhere.txt" })
            : new Reply(404, "text/plain", "");
        site.Html("/q/index.html", Page("Index", "Index text long enough to be kept here.", "/q/a.html"));
        site.Html("/q/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        var crawler = Crawler(WebCrawlOptions.Crawl(10, 1));
        Uri start = new(site.Origin + "/q/index.html");
        await crawler.CrawlAsync(start, folder, manifest, default);
        mode = "redirect";

        WebCrawlReport report = await crawler.CrawlAsync(start, folder, manifest, default);

        Assert.Equal(0, report.Removed);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task A_page_that_is_merely_no_longer_linked_but_still_exists_is_kept()
    {
        TestSite site = Site();
        site.Html("/u/index.html", Page("Index", "Index text long enough to be kept here.", "/u/a.html"));
        site.Html("/u/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        var crawler = Crawler(WebCrawlOptions.Crawl(10, 1));
        Uri start = new(site.Origin + "/u/index.html");
        await crawler.CrawlAsync(start, folder, manifest, default);

        site.Html("/u/index.html", Page("Index", "Index text long enough to be kept here."));   // link removed, page still served
        WebCrawlReport report = await crawler.CrawlAsync(start, folder, manifest, default);

        Assert.Equal(0, report.Removed);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Theory]
    [InlineData("bot")]
    [InlineData("p")]
    [InlineData("paruxknowledgebotx")]
    [InlineData("parux")]
    public void A_robots_group_for_a_different_or_partial_crawler_name_does_not_apply(string agent)
    {
        RobotsRules rules = RobotsRules.Parse("User-agent: " + agent + "\nDisallow: /\n\nUser-agent: *\nDisallow:\n", "paruxknowledgebot");
        Assert.True(rules.IsAllowed("/anything"));
    }

    [Theory]
    [InlineData("PaRuXKnowledgeBot")]
    [InlineData("paruxknowledgebot/1.0")]
    public void A_robots_group_naming_this_crawler_applies(string agent)
    {
        RobotsRules rules = RobotsRules.Parse("User-agent: " + agent + "\nDisallow: /\n\nUser-agent: *\nDisallow:\n", "paruxknowledgebot");
        Assert.False(rules.IsAllowed("/anything"));
    }

    [Fact]
    public async Task A_same_host_redirect_into_a_robots_disallowed_path_or_out_of_scope_is_not_followed()
    {
        TestSite site = Site();
        site.Routes["/robots.txt"] = _ => new Reply(200, "text/plain", "User-agent: *\nDisallow: /docs/private/\n");
        site.Html("/docs/index.html", Page("Index", "Index text long enough to be kept here.", "/docs/jump.html", "/docs/away.html"));
        site.Routes["/docs/jump.html"] = _ => new Reply(302, "text/plain", "", new() { ["Location"] = "/docs/private/secret.html" });
        site.Routes["/docs/away.html"] = _ => new Reply(302, "text/plain", "", new() { ["Location"] = "/admin/panel.html" });
        site.Html("/docs/private/secret.html", Page("Secret", "Secret text that must never be fetched at all."));
        site.Html("/admin/panel.html", Page("Admin", "Admin text outside the crawl scope entirely, long."));
        (string folder, WebSourceManifest manifest) = NewFolder();

        WebCrawlReport report = await Crawler(WebCrawlOptions.Crawl(10, 1)).CrawlAsync(new Uri(site.Origin + "/docs/index.html"), folder, manifest, default);

        Assert.Equal(0, site.Hits.GetValueOrDefault("/docs/private/secret.html"));
        Assert.Equal(0, site.Hits.GetValueOrDefault("/admin/panel.html"));
        Assert.Contains(report.Pages, p => p.Reason == "redirect_blocked_by_robots");
        Assert.Contains(report.Pages, p => p.Reason == "redirected_outside_scope");
    }

    [Fact]
    public async Task Turning_strict_offline_on_mid_crawl_stops_before_the_next_request()
    {
        TestSite site = Site();
        bool offline = false;
        site.Html("/o/index.html", Page("Index", "Index text long enough to be kept here.", "/o/a.html", "/o/b.html"));
        site.Routes["/o/a.html"] = _ => { offline = true; return new Reply(200, "text/html", Page("A", "Alpha text that is long enough to be kept here.")); };
        site.Html("/o/b.html", Page("B", "Beta text that is long enough to be kept here."));
        (string folder, WebSourceManifest manifest) = NewFolder();
        var crawler = new WebKnowledgeCrawler(WebCrawlOptions.Crawl(10, 1) with { PoliteDelay = TimeSpan.Zero }, Loopback, null, () => false,
            (_, _) => Task.CompletedTask, () => offline);

        WebCrawlReport report = await crawler.CrawlAsync(new Uri(site.Origin + "/o/index.html"), folder, manifest, default);

        Assert.Equal("offline", report.StoppedBecause);
        Assert.Equal(0, site.Hits.GetValueOrDefault("/o/b.html"));
    }

    [Theory]
    [InlineData("..\\evil.txt")]
    [InlineData("sub\\x.md")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("..")]
    [InlineData("")]
    public void Manifest_file_names_must_be_plain_names_inside_the_folder(string fileName)
    {
        Assert.Null(WebKnowledgeCrawler.SafePath(Path.Combine(_root, "folder"), fileName));
    }

    [Fact]
    public void A_plain_file_name_resolves_inside_the_folder()
    {
        string folder = Path.Combine(_root, "folder");
        Assert.Equal(Path.Combine(folder, "page-1.md"), WebKnowledgeCrawler.SafePath(folder, "page-1.md"));
    }

    [Fact]
    public async Task A_tampered_manifest_cannot_make_removal_delete_files_elsewhere()
    {
        TestSite site = Site();
        site.Html("/t/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        var (service, folders, _, _) = Service();
        WebSourceInfo info = service.Begin(site.Origin + "/t/a.html", "page", null, null);
        await service.WaitAsync(info.Id);

        string victim = Path.Combine(_root, "victim.txt");
        File.WriteAllText(victim, "precious");
        string manifestPath = Path.Combine(_root, "manifests", info.Id + ".json");
        string text = File.ReadAllText(manifestPath);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<WebSourceManifest>(text)!;
        WebPageRecord record = manifest.Pages.Values.Single();
        record.FileName = "..\\..\\victim.txt";
        record.WrittenSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("precious"))).ToLowerInvariant();
        File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));

        service.Remove(info.Id);

        Assert.True(File.Exists(victim));
        _ = folders;
    }

    [Fact]
    public async Task A_manifest_pointing_outside_the_web_pages_root_is_ignored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "manifests"));
        string outside = Path.Combine(_root, "not-web-root");
        Directory.CreateDirectory(outside);
        var manifest = new WebSourceManifest { Id = "evil", Url = "https://example.com/", Folder = outside };
        File.WriteAllText(Path.Combine(_root, "manifests", "evil.json"), System.Text.Json.JsonSerializer.Serialize(manifest));
        var (service, _, _, unregistered) = Service();

        Assert.Null(service.Get("evil"));
        Assert.False(service.Remove("evil"));
        Assert.Empty(unregistered());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_stopped_crawl_does_not_start_indexing()
    {
        TestSite site = Site();
        site.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        var (service, _, indexed, _) = Service(gaming: () => false);
        bool gaming = false;
        var folders = new List<string>();
        var gated = new WebKnowledgeSources(
            Path.Combine(_root, "m3"), Path.Combine(_root, "c3"),
            f => { folders.Add(f); return "g1"; }, _ => { }, id => { indexed().Add(id); return true; }, () => false, () => gaming,
            options => new WebKnowledgeCrawler(options with { PoliteDelay = TimeSpan.Zero }, Loopback, null, () => true, (_, _) => Task.CompletedTask));

        WebSourceInfo info = gated.Begin(site.Origin + "/a.html", "page", null, null);
        await gated.WaitAsync(info.Id);

        Assert.Equal("stopped", gated.Get(info.Id)!.State);
        Assert.Equal("gaming_active", gated.Get(info.Id)!.Error);
        Assert.Empty(indexed());
        _ = service;
    }

    [Fact]
    public async Task Two_quick_resyncs_start_one_crawl()
    {
        TestSite site = Site();
        site.Routes["/s2/a.html"] = _ => new Reply(200, "text/html", Page("A", "Alpha text that is long enough to be kept here."), null, DelayMs: 700);
        var (service, _, _, _) = Service();
        WebSourceInfo info = service.Begin(site.Origin + "/s2/a.html", "page", null, null);
        await service.WaitAsync(info.Id);
        int before = site.Hits.GetValueOrDefault("/s2/a.html");

        Task[] attempts = Enumerable.Range(0, 6).Select(_ => Task.Run(() => service.Resync(info.Id))).ToArray();
        await Task.WhenAll(attempts);
        await service.WaitAsync(info.Id);

        Assert.Equal(before + 1, site.Hits.GetValueOrDefault("/s2/a.html"));
    }

    // ------------------------------------------------------------------ the source service (policy, index, citation, removal)

    private (WebKnowledgeSources Service, Func<List<string>> Folders, Func<List<string>> Indexed, Func<List<string>> Unregistered) Service(
        Func<bool>? offline = null, Func<bool>? gaming = null, Func<string, bool>? index = null)
    {
        var folders = new List<string>();
        var indexed = new List<string>();
        var unregistered = new List<string>();
        var service = new WebKnowledgeSources(
            Path.Combine(_root, "manifests"), Path.Combine(_root, "content"),
            folder => { folders.Add(folder); return "src" + folders.Count; },
            id => unregistered.Add(id),
            id => { indexed.Add(id); return index?.Invoke(id) ?? true; },
            offline ?? (() => false), gaming ?? (() => false),
            options => new WebKnowledgeCrawler(options with { PoliteDelay = TimeSpan.Zero }, Loopback, null, gaming ?? (() => false), (_, _) => Task.CompletedTask));
        return (service, () => folders, () => indexed, () => unregistered);
    }

    [Fact]
    public async Task Strict_offline_mode_refuses_with_an_honest_message_and_does_nothing()
    {
        TestSite site = Site();
        site.Html("/a.html", Page("A", "Some text that is long enough to be kept here."));
        var (service, folders, indexed, _) = Service(offline: () => true);

        var ex = Assert.Throws<OfflineBlockedException>(() => service.Begin(site.Origin + "/a.html", "page", null, null));

        Assert.Contains("offline", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was downloaded", ex.Message);
        Assert.Empty(folders());
        Assert.Equal(0, site.TotalHits);
        Assert.Throws<OfflineBlockedException>(() => service.Resync("anything"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Gaming_mode_and_bad_requests_are_refused_before_a_folder_or_request_exists()
    {
        TestSite site = Site();
        var (gamingService, gamingFolders, _, _) = Service(gaming: () => true);
        Assert.Throws<UnavailableDuringGamingException>(() => gamingService.Begin(site.Origin + "/a.html", "page", null, null));
        Assert.Empty(gamingFolders());

        var (service, folders, _, _) = Service();
        Assert.Throws<ArgumentException>(() => service.Begin("http://10.0.0.1/", "page", null, null));
        Assert.Throws<ArgumentException>(() => service.Begin("file:///C:/x", "page", null, null));
        Assert.Throws<ArgumentException>(() => service.Begin("https://example.com/", "mirror", null, null));
        Assert.Empty(folders());
        Assert.Equal(0, site.TotalHits);
        Assert.False(Directory.Exists(Path.Combine(_root, "content")));
    }

    [Fact]
    public async Task An_added_page_is_indexed_and_a_question_finds_it_with_the_page_address()
    {
        TestSite site = Site();
        site.Html("/kb/pump.html", Page("Pump maintenance", "The impeller of the hydraulic pump must be replaced after four thousand operating hours."));
        using var index = new KnowledgeIndex(Path.Combine(_root, "knowledge.db"));
        string? folderForSource = null;
        var (service, folders, indexed, _) = Service(index: id => true);
        // Use the real index for the registered folder.
        var real = new WebKnowledgeSources(
            Path.Combine(_root, "manifests2"), Path.Combine(_root, "content2"),
            folder => { folderForSource = folder; return "web1"; },
            id => { },
            id => { index.IndexFolderAsync(id, folderForSource!, null, null, default).GetAwaiter().GetResult(); return true; },
            () => false, () => false,
            options => new WebKnowledgeCrawler(options with { PoliteDelay = TimeSpan.Zero }, Loopback, null, () => false, (_, _) => Task.CompletedTask));

        WebSourceInfo started = real.Begin(site.Origin + "/kb/pump.html", "page", null, null);
        await real.WaitAsync(started.Id);

        WebSourceInfo done = real.Get("web1")!;
        Assert.Equal("ready", done.State);
        Assert.Equal(1, done.Pages);
        IReadOnlyList<KnowledgeHit> hits = index.Search("impeller hydraulic pump replaced", KnowledgeSearchMode.Lexical, 5, new[] { "web1" });
        KnowledgeHit hit = hits.First(h => h.Text.Contains("operating hours"));
        Assert.All(hits, h => Assert.Equal(site.Origin + "/kb/pump.html", real.TryGetUrl(h.FilePath)));
        Assert.Equal(site.Origin + "/kb/pump.html", real.TryGetUrl(hit.FilePath));   // the citation maps back to the page address
        Assert.Null(real.TryGetUrl(Path.Combine(_root, "elsewhere.md")));
        _ = (service, folders, indexed);
    }

    [Fact]
    public async Task Removing_a_web_source_deletes_only_the_files_it_wrote()
    {
        TestSite site = Site();
        site.Html("/m/index.html", Page("Index", "Index text long enough to be kept here.", "/m/a.html"));
        site.Html("/m/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        var (service, folders, _, unregistered) = Service();

        WebSourceInfo info = service.Begin(site.Origin + "/m/index.html", "crawl", 5, 1);
        await service.WaitAsync(info.Id);
        string folder = folders().Single();
        string userFile = Path.Combine(folder, "my-own-file.txt");
        File.WriteAllText(userFile, "mine");
        string[] written = Directory.GetFiles(folder, "*.md");
        Assert.Equal(2, written.Length);
        File.AppendAllText(written[0], "\nedited by me\n");

        Assert.True(service.Remove(info.Id));

        Assert.Contains(info.Id, unregistered());
        Assert.True(File.Exists(userFile));
        Assert.True(File.Exists(written[0]));          // edited copy kept
        Assert.False(File.Exists(written[1]));         // untouched copy removed
        Assert.Null(service.Get(info.Id));
        Assert.False(service.Remove(info.Id));
    }

    [Fact]
    public async Task Resync_through_the_service_is_user_initiated_and_reuses_the_saved_settings()
    {
        TestSite site = Site();
        site.Html("/s/a.html", Page("A", "Alpha text that is long enough to be kept here."));
        var (service, folders, indexed, _) = Service();
        WebSourceInfo info = service.Begin(site.Origin + "/s/a.html", "page", null, null);
        await service.WaitAsync(info.Id);
        int hitsBefore = site.TotalHits;
        await Task.Delay(300);
        Assert.Equal(hitsBefore, site.TotalHits);   // nothing polls in the background

        WebSourceInfo? again = service.Resync(info.Id);
        await service.WaitAsync(info.Id);

        Assert.NotNull(again);
        Assert.True(site.TotalHits > hitsBefore);
        Assert.Equal(2, indexed().Count);
        Assert.Null(service.Resync("does-not-exist"));
        _ = folders;
    }
}
