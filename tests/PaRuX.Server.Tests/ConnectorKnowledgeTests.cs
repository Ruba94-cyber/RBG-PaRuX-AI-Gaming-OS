using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>GitHub / YouTube / Confluence connectors with a fake HTTP handler (no real network).</summary>
public sealed class ConnectorKnowledgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-conn-" + Guid.NewGuid().ToString("N"));
    public ConnectorKnowledgeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = new();
        public List<string?> AuthHeaders { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Seen) { Seen.Add(request); AuthHeaders.Add(request.Headers.Authorization?.ToString()); }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private sealed class PlainProtector : ISecretProtector
    {
        public string Description => "test";
        public string Protect(string purpose, string plaintext) => "enc:" + purpose + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string? Unprotect(string purpose, string blob) =>
            blob.StartsWith("enc:" + purpose + ":", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(blob[("enc:" + purpose + ":").Length..])) : null;
    }

    private (WebKnowledgeSources Svc, ConnectorSecretStore Secrets, List<string> Indexed) Service(FakeHandler handler,
        Func<bool>? internet = null, Func<bool>? gaming = null, Func<bool>? offline = null, ConnectorLimits? limits = null, Func<CancellationToken>? offToken = null)
    {
        var indexed = new List<string>();
        var secrets = new ConnectorSecretStore(Path.Combine(_root, "state"), new PlainProtector());
        int n = 0;
        var svc = new WebKnowledgeSources(Path.Combine(_root, "manifests"), Path.Combine(_root, "content"),
            _ => "src" + (++n), _ => { }, id => { indexed.Add(id); return true; }, offline ?? (() => false), gaming ?? (() => false),
            connectors: new KnowledgeConnectors(() => new HttpClient(handler, disposeHandler: false), limits),
            secrets: secrets, internetEnabled: internet ?? (() => true), internetOffToken: offToken);
        return (svc, secrets, indexed);
    }

    // ----------------------------------------------------------------------------- parsing

    [Theory]
    [InlineData("https://github.com/octo/hello", "octo", "hello", null, null)]
    [InlineData("https://github.com/octo/hello.git", "octo", "hello", null, null)]
    [InlineData("https://github.com/octo/hello/tree/main/docs/guide", "octo", "hello", "main", "docs/guide")]
    [InlineData("octo/hello", "octo", "hello", null, null)]
    public void GitHub_targets_parse(string input, string owner, string repo, string? refName, string? path)
    {
        Assert.True(GitHubTargetParser.TryParse(input, null, null, out GitHubTarget t, out _));
        Assert.Equal((owner, repo, refName, path), (t.Owner, t.Repo, t.Ref, t.Path));
    }

    [Theory]
    [InlineData("http://github.com/octo/hello")]
    [InlineData("https://evil.example.com/octo/hello")]
    [InlineData("https://user:pw@github.com/octo/hello")]
    [InlineData("https://github.com/octo")]
    [InlineData("https://github.com/../hello")]
    [InlineData("file:///c:/secret")]
    [InlineData("")]
    public void GitHub_rejects_bad_targets(string input) =>
        Assert.False(GitHubTargetParser.TryParse(input, null, null, out _, out string error) || error.Length == 0);

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("/abs/path")]
    [InlineData("a/../b")]
    [InlineData("C:/x")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    public void Repository_paths_with_traversal_are_unsafe(string path) => Assert.False(GitHubTargetParser.IsSafeRelativePath(path));

    [Fact]
    public void Path_prefix_and_ref_with_traversal_are_refused()
    {
        Assert.False(GitHubTargetParser.TryParse("octo/hello", null, "../../x", out _, out _));
        Assert.False(GitHubTargetParser.TryParse("octo/hello", "a/../b", null, out _, out _));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=4s", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void YouTube_ids_parse(string input, string id)
    {
        Assert.True(YouTubeTargetParser.TryParseVideoId(input, out string parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ%00x0")]
    public void YouTube_rejects_bad_targets(string input) => Assert.False(YouTubeTargetParser.TryParseVideoId(input, out _));

    [Theory]
    [InlineData("https://acme.atlassian.net/wiki/spaces/KEY/pages/12345/Title", "/wiki", "12345")]
    [InlineData("https://wiki.acme.com/pages/viewpage.action?pageId=777", "", "777")]
    [InlineData("https://wiki.acme.com/confluence/pages/viewpage.action?pageId=9", "/confluence", "9")]
    public void Confluence_targets_parse(string input, string context, string pageId)
    {
        Assert.True(ConfluenceTargetParser.TryParse(input, out ConfluenceTarget t, out _));
        Assert.Equal((context, pageId), (t.ContextPath, t.PageId));
    }

    [Theory]
    [InlineData("http://acme.atlassian.net/wiki/spaces/K/pages/1")]
    [InlineData("https://acme.atlassian.net/wiki/spaces/K")]
    [InlineData("https://u:p@acme.atlassian.net/wiki/spaces/K/pages/1")]
    [InlineData("https://acme.atlassian.net/wi%20ki/spaces/K/pages/1")]
    public void Confluence_rejects_bad_targets(string input) => Assert.False(ConfluenceTargetParser.TryParse(input, out _, out _));

    [Fact]
    public void Confluence_private_hosts_are_refused_before_any_request()
    {
        Assert.False(KnowledgeConnectors.TryValidate(ConnectorKind.Confluence, "https://127.0.0.1/pages/viewpage.action?pageId=1", null, null, out string error));
        Assert.Contains("private", error, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------------------- GitHub

    private static string TreeJson(params (string Path, int Size)[] files) =>
        JsonSerializer.Serialize(new { truncated = false, tree = files.Select(f => new { path = f.Path, type = "blob", size = f.Size }).ToArray() });

    private static void GitHubRoutes(FakeHandler h, string tree, Func<string, HttpResponseMessage>? file = null)
    {
        h.Respond = r =>
        {
            string u = r.RequestUri!.ToString();
            if (u == "https://api.github.com/repos/octo/hello") return Json("{\"default_branch\":\"main\"}");
            if (u.StartsWith("https://api.github.com/repos/octo/hello/git/trees/main")) return Json(tree);
            if (u.StartsWith("https://raw.githubusercontent.com/octo/hello/main/"))
                return file?.Invoke(u["https://raw.githubusercontent.com/octo/hello/main/".Length..]) ?? Text("content of " + u);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    [Fact]
    public async Task GitHub_import_writes_text_files_with_citation_urls_and_skips_binary_and_vendor()
    {
        var h = new FakeHandler();
        GitHubRoutes(h, TreeJson(("README.md", 40), ("src/app.py", 30), ("node_modules/x/index.js", 10), ("logo.png", 10), ("bin/blob.txt", 5), ("docs/guide.md", 50)),
            path => path == "docs/guide.md" ? Text("Guide text") : Text("# Readme\nHello\n"));
        var (svc, _, indexed) = Service(h);

        WebSourceInfo info = svc.BeginConnector("github", "https://github.com/octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal("ready", done.State);
        Assert.Equal("github", done.Connector);
        Assert.Equal(3, done.Pages);   // README.md, docs/guide.md, src/app.py
        Assert.Contains(info.Id, indexed);
        string[] files = Directory.GetFiles(done.Folder);
        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.Equal(done.Folder, Path.GetDirectoryName(f)));
        Assert.Equal("https://github.com/octo/hello/blob/main/docs/guide.md", svc.TryGetUrl(files.First(f => File.ReadAllText(f).Contains("Guide text"))));
        string code = File.ReadAllText(files.First(f => File.ReadAllText(f).Contains("def") || f.Contains("app.py")));
        Assert.Contains("untrusted imported text", code);
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.AbsolutePath.Contains("node_modules"));
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.AbsolutePath.Contains("logo.png"));
    }

    [Fact]
    public async Task GitHub_hostile_paths_in_the_tree_never_leave_the_folder_or_get_fetched()
    {
        var h = new FakeHandler();
        GitHubRoutes(h, TreeJson(("../../evil.md", 10), ("ok/..%2f..%2fx.md", 10), ("/abs.md", 10), ("a\\b.md", 10), ("fine.md", 10)));
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal(2, done.Pages);   // fine.md and the percent-encoded name (a plain, legal file name)
        foreach (string f in Directory.GetFiles(done.Folder)) Assert.Equal(done.Folder, Path.GetDirectoryName(f));
        Assert.False(File.Exists(Path.Combine(_root, "evil.md")));
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.ToString().Contains("evil"));
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.ToString().Contains("abs.md"));
    }

    [Fact]
    public async Task GitHub_file_and_total_size_caps_are_enforced()
    {
        var h = new FakeHandler();
        GitHubRoutes(h, TreeJson(("big.md", 5_000_000), ("a.md", 50), ("b.md", 50), ("c.md", 50)), _ => Text(new string('x', 40)));
        var (svc, _, _) = Service(h, limits: new ConnectorLimits { MaxBytesPerDoc = 1000, MaxDocs = 2 });
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal(2, done.Pages);               // doc cap
        Assert.Equal("limit", GetStopped(svc, info.Id));
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.AbsolutePath.EndsWith("big.md"));
    }

    private static string? GetStopped(WebKnowledgeSources svc, string id) => svc.Get(id)!.StoppedBecause;

    [Fact]
    public async Task GitHub_oversized_download_body_is_skipped_not_stored()
    {
        var h = new FakeHandler();
        GitHubRoutes(h, TreeJson(("a.md", 10), ("b.md", 10)), p => p == "a.md" ? Text(new string('y', 5000)) : Text("small"));
        var (svc, _, _) = Service(h, limits: new ConnectorLimits { MaxBytesPerDoc = 1000 });
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);
        Assert.Equal(1, svc.Get(info.Id)!.Pages);
    }

    [Fact]
    public async Task GitHub_rate_limit_is_an_honest_failure_and_indexes_nothing()
    {
        var h = new FakeHandler();
        h.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            r.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            return r;
        };
        var (svc, _, indexed) = Service(h);
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal("failed", done.State);
        Assert.Equal("rate_limited", done.Error);
        Assert.Contains("token", done.Notice!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(indexed);
    }

    [Fact]
    public async Task GitHub_token_goes_only_in_the_authorization_header_to_github_hosts_and_never_into_urls_manifests_or_notices()
    {
        const string token = "ghp_SECRETTOKENVALUE1234567890";
        var h = new FakeHandler();
        GitHubRoutes(h, TreeJson(("README.md", 10)));
        var (svc, secrets, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, token, false);
        await svc.WaitAsync(info.Id);

        Assert.All(h.Seen, r => Assert.DoesNotContain(token, r.RequestUri!.ToString()));
        Assert.All(h.Seen, r => Assert.Contains(r.RequestUri!.Host, new[] { "api.github.com", "raw.githubusercontent.com" }));
        Assert.All(h.AuthHeaders, a => Assert.Equal("Bearer " + token, a));
        // The secret file holds ciphertext only, and the manifest + info never contain the token.
        string secretFile = File.ReadAllText(Path.Combine(_root, "state", "knowledge-connector-secrets.json"));
        Assert.DoesNotContain(token, secretFile);
        Assert.Equal(token, secrets.Get(info.Id));
        foreach (string m in Directory.GetFiles(Path.Combine(_root, "manifests"))) Assert.DoesNotContain(token, File.ReadAllText(m));
        Assert.DoesNotContain(token, JsonSerializer.Serialize(svc.Get(info.Id)));
        foreach (string f in Directory.GetFiles(svc.Get(info.Id)!.Folder)) Assert.DoesNotContain(token, File.ReadAllText(f));

        svc.Remove(info.Id);
        Assert.False(secrets.Has(info.Id));
    }

    [Fact]
    public async Task Redirect_to_another_host_is_not_followed_and_does_not_carry_the_token()
    {
        const string token = "tok_redirect_1234567890";
        var h = new FakeHandler();
        h.Respond = r => r.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.com/steal") } }
            : Text("should never be reached");
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, token, false);
        await svc.WaitAsync(info.Id);

        Assert.Equal("failed", svc.Get(info.Id)!.State);
        Assert.Equal("blocked_host", svc.Get(info.Id)!.Error);
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.Host == "evil.example.com");
    }

    [Fact]
    public async Task Plain_http_redirect_is_refused()
    {
        var h = new FakeHandler();
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("http://api.github.com/repos/octo/hello") } };
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("insecure_address", svc.Get(info.Id)!.Error);
    }

    [Fact]
    public async Task Resync_does_not_overwrite_an_edited_copy_and_removes_only_unmodified_gone_files()
    {
        var h = new FakeHandler();
        var tree = TreeJson(("a.md", 10), ("b.md", 10));
        string bodyA = "version one";
        GitHubRoutes(h, tree, _ => Text(bodyA));
        h.Respond = r =>
        {
            string u = r.RequestUri!.ToString();
            if (u == "https://api.github.com/repos/octo/hello") return Json("{\"default_branch\":\"main\"}");
            if (u.Contains("/git/trees/")) return Json(tree);
            return Text(u.EndsWith("a.md") ? bodyA : "bee");
        };
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);
        string folder = svc.Get(info.Id)!.Folder;
        string fileA = Directory.GetFiles(folder).First(f => File.ReadAllText(f).Contains("version one"));
        File.AppendAllText(fileA, "\nMY EDIT");

        bodyA = "version two";
        tree = TreeJson(("a.md", 10));   // b.md is gone upstream
        svc.Resync(info.Id);
        await svc.WaitAsync(info.Id);

        Assert.Contains("MY EDIT", File.ReadAllText(fileA));
        Assert.DoesNotContain("version two", File.ReadAllText(fileA));
        Assert.Single(Directory.GetFiles(folder));      // b.md removed (unmodified), edited a.md kept
        Assert.Equal(1, svc.Get(info.Id)!.KeptEdited);
    }

    // ----------------------------------------------------------------------------- gates

    [Fact]
    public void Internet_switch_off_blocks_connectors_before_any_request()
    {
        var h = new FakeHandler();
        var (svc, _, _) = Service(h, internet: () => false);
        Assert.Throws<InternetDisabledException>(() => svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false));
        Assert.Empty(h.Seen);
        Assert.False(Directory.Exists(Path.Combine(_root, "content")) && Directory.GetDirectories(Path.Combine(_root, "content")).Length > 0);
    }

    [Fact]
    public void Strict_offline_and_gaming_block_connectors_before_any_request()
    {
        var h = new FakeHandler();
        var (offline, _, _) = Service(h, offline: () => true);
        Assert.Throws<OfflineBlockedException>(() => offline.BeginConnector("youtube", "dQw4w9WgXcQ", null, null, null, null, null, false));
        var (gaming, _, _) = Service(h, gaming: () => true);
        Assert.Throws<UnavailableDuringGamingException>(() => gaming.BeginConnector("youtube", "dQw4w9WgXcQ", null, null, null, null, null, false));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Internet_switched_off_mid_import_stops_it()
    {
        var h = new FakeHandler();
        using var off = new CancellationTokenSource();
        h.Respond = r => { off.Cancel(); return Json("{\"default_branch\":\"main\"}"); };
        var (svc, _, indexed) = Service(h, offToken: () => off.Token);
        WebSourceInfo info = svc.BeginConnector("github", "octo/hello", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("stopped", svc.Get(info.Id)!.State);
        Assert.Equal("internet_off", svc.Get(info.Id)!.Error);
        Assert.Empty(indexed);
    }

    [Fact]
    public void Invalid_token_shapes_are_refused_without_storing_anything()
    {
        var h = new FakeHandler();
        var (svc, secrets, _) = Service(h);
        Assert.Throws<ArgumentException>(() => svc.BeginConnector("github", "octo/hello", null, null, null, null, "has space", false));
        Assert.Throws<ArgumentException>(() => svc.BeginConnector("github", "octo/hello", null, null, null, null, "line\nbreak", false));
        Assert.Throws<ArgumentException>(() => svc.BeginConnector("github", "octo/hello", null, null, null, "a:b@x.com", "ok", false));
        Assert.Throws<ArgumentException>(() => svc.BeginConnector("nope", "x", null, null, null, null, null, false));
        Assert.Empty(h.Seen);
        Assert.False(secrets.Has("src1"));
    }

    // ----------------------------------------------------------------------------- YouTube

    private static string WatchPage(string captionBase, string title = "My Talk") =>
        "<html><script>var ytInitialPlayerResponse = " + JsonSerializer.Serialize(new
        {
            videoDetails = new { title, author = "Some Channel" },
            captions = new
            {
                playerCaptionsTracklistRenderer = new
                {
                    captionTracks = new object[]
                    {
                        new { baseUrl = captionBase + "&lang=de", languageCode = "de", kind = "" },
                        new { baseUrl = captionBase + "&lang=en", languageCode = "en", kind = "asr" }
                    }
                }
            }
        }) + ";var meta = {};</script></html>";

    [Fact]
    public async Task YouTube_transcript_is_imported_with_timestamps_and_video_url_citation()
    {
        var h = new FakeHandler();
        string events = JsonSerializer.Serialize(new
        {
            events = new object[]
            {
                new { tStartMs = 0, segs = new[] { new { utf8 = "Hello" }, new { utf8 = " world" } } },
                new { tStartMs = 5000, segs = new[] { new { utf8 = "second line" } } },
                new { tStartMs = 65000, segs = new[] { new { utf8 = "a minute later" } } },
                new { tStartMs = 66000 }
            }
        });
        h.Respond = r =>
        {
            string u = r.RequestUri!.ToString();
            if (u.StartsWith("https://www.youtube.com/watch")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WatchPage("https://www.youtube.com/api/timedtext?v=x"), Encoding.UTF8, "text/html") };
            if (u.StartsWith("https://www.youtube.com/api/timedtext")) return Json(u.Contains("lang=en") ? events : "{}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("youtube", "https://youtu.be/dQw4w9WgXcQ", null, null, "en", null, null, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal("ready", done.State);
        string file = Directory.GetFiles(done.Folder).Single();
        string text = File.ReadAllText(file);
        Assert.Contains("[00:00] Hello world second line", text);
        Assert.Contains("[01:05] a minute later", text);
        Assert.Contains("Some Channel", text);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", svc.TryGetUrl(file));
    }

    [Fact]
    public async Task YouTube_without_captions_fails_honestly()
    {
        var h = new FakeHandler();
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ytInitialPlayerResponse = {\"videoDetails\":{\"title\":\"x\"}};</html>", Encoding.UTF8, "text/html") };
        var (svc, _, indexed) = Service(h);
        WebSourceInfo info = svc.BeginConnector("youtube", "dQw4w9WgXcQ", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("failed", svc.Get(info.Id)!.State);
        Assert.Equal("no_captions", svc.Get(info.Id)!.Error);
        Assert.Empty(indexed);
    }

    [Fact]
    public async Task YouTube_caption_url_pointing_outside_youtube_is_not_fetched()
    {
        var h = new FakeHandler();
        h.Respond = r => r.RequestUri!.Host == "www.youtube.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WatchPage("https://169.254.169.254/latest/meta?x=1"), Encoding.UTF8, "text/html") }
            : Text("secret");
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("youtube", "dQw4w9WgXcQ", null, null, "de", null, null, false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("blocked_host", svc.Get(info.Id)!.Error);
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.Host == "169.254.169.254");
    }

    [Fact]
    public void YouTube_player_response_extraction_handles_braces_in_strings()
    {
        string html = "x ytInitialPlayerResponse = {\"a\":\"}{ not end\",\"b\":{\"c\":1}}; more";
        Assert.Equal("{\"a\":\"}{ not end\",\"b\":{\"c\":1}}", YouTubeConnectorProbe.Extract(html));
    }

    [Fact]
    public async Task YouTube_xml_captions_fallback_and_bare_host_work_with_timestamps()
    {
        var h = new FakeHandler();
        const string xml = "<transcript><text start=\"0.0\" dur=\"2.0\">Intro &amp; welcome</text><text start=\"42.5\" dur=\"3.0\">Second section</text></transcript>";
        h.Respond = r =>
        {
            string u = r.RequestUri!.ToString();
            if (u.StartsWith("https://www.youtube.com/watch")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WatchPage("https://youtube.com/api/timedtext?v=x"), Encoding.UTF8, "text/html") };
            if (u.StartsWith("https://youtube.com/api/timedtext")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("youtube", "https://youtube.com/watch?v=dQw4w9WgXcQ", null, null, "en", null, null, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal("ready", done.State);
        string file = Directory.GetFiles(done.Folder).Single();
        string text = File.ReadAllText(file);
        Assert.Contains("[00:00] Intro & welcome", text);
        Assert.Contains("[00:42] Second section", text);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", svc.TryGetUrl(file));
    }

    [Fact]
    public async Task YouTube_rate_limit_429_is_an_honest_failure_and_indexes_nothing()
    {
        var h = new FakeHandler();
        h.Respond = _ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.TryAddWithoutValidation("Retry-After", "60");
            return r;
        };
        var (svc, _, indexed) = Service(h);
        WebSourceInfo info = svc.BeginConnector("youtube", "dQw4w9WgXcQ", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal("failed", done.State);
        Assert.Equal("rate_limited", done.Error);
        Assert.Contains("60", done.Notice!);
        Assert.Empty(indexed);
    }

    // ----------------------------------------------------------------------------- Confluence

    [Fact]
    public async Task Confluence_page_and_children_are_imported_with_basic_auth_only_to_that_host()
    {
        const string token = "atl_TOKEN_abcdefghij";
        var h = new FakeHandler();
        h.Respond = r =>
        {
            string u = r.RequestUri!.ToString();
            if (u.StartsWith("https://acme.atlassian.net/wiki/rest/api/content/100/child/page"))
                return Json("{\"results\":[{\"id\":\"101\"},{\"id\":\"../102\"}]}");
            if (u.StartsWith("https://acme.atlassian.net/wiki/rest/api/content/100?"))
                return Json("{\"title\":\"Parent\",\"space\":{\"key\":\"ENG\"},\"body\":{\"storage\":{\"value\":\"<h1>Parent</h1><p>Deploy <b>steps</b></p><script>alert(1)</script>\"}}}");
            if (u.StartsWith("https://acme.atlassian.net/wiki/rest/api/content/101?"))
                return Json("{\"title\":\"Child\",\"space\":{\"key\":\"ENG\"},\"body\":{\"storage\":{\"value\":\"<p>Child page text</p>\"}}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("confluence", "https://acme.atlassian.net/wiki/spaces/ENG/pages/100/Parent", null, null, null, "me@acme.com", token, true);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal("ready", done.State);
        Assert.Equal(2, done.Pages);
        string[] files = Directory.GetFiles(done.Folder);
        string parent = File.ReadAllText(files.First(f => File.ReadAllText(f).Contains("Deploy")));
        Assert.DoesNotContain("alert(1)", parent);
        Assert.Contains("https://acme.atlassian.net/wiki/spaces/ENG/pages/100", parent);
        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me@acme.com:" + token));
        Assert.All(h.AuthHeaders, a => Assert.Equal(expected, a));
        Assert.All(h.Seen, r => Assert.Equal("acme.atlassian.net", r.RequestUri!.Host));
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.ToString().Contains(".."));
    }

    [Fact]
    public async Task Confluence_without_a_token_on_a_private_page_asks_for_one()
    {
        var h = new FakeHandler();
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("confluence", "https://acme.atlassian.net/wiki/spaces/ENG/pages/5/T", null, null, null, null, null, false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("auth_required", svc.Get(info.Id)!.Error);
        Assert.Contains("token", svc.Get(info.Id)!.Notice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confluence_429_is_a_rate_limit_failure_with_retry_after()
    {
        var h = new FakeHandler();
        h.Respond = _ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.TryAddWithoutValidation("Retry-After", "30");
            return r;
        };
        var (svc, _, _) = Service(h);
        WebSourceInfo info = svc.BeginConnector("confluence", "https://acme.atlassian.net/wiki/spaces/ENG/pages/5/T", null, null, null, null, "tok", false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("rate_limited", svc.Get(info.Id)!.Error);
        Assert.Contains("30", svc.Get(info.Id)!.Notice!);
    }

    // ----------------------------------------------------------------------------- text + secrets units

    [Fact]
    public void Imported_text_is_stripped_of_control_characters_and_bounded()
    {
        string cleaned = ConnectorText.Sanitize("ok\0text\u0007\u001b[31m end\r\nline", 12);
        Assert.DoesNotContain('\0', cleaned);
        Assert.DoesNotContain('\u0007', cleaned);
        Assert.DoesNotContain('\u001b', cleaned);
        Assert.True(cleaned.Length <= 12);
    }

    [Fact]
    public void Secret_store_is_purpose_bound_per_source()
    {
        var store = new ConnectorSecretStore(Path.Combine(_root, "s"), new PlainProtector());
        store.Set("a", "alpha");
        store.Set("b", "beta");
        Assert.Equal("alpha", store.Get("a"));
        Assert.Equal("beta", store.Get("b"));
        string file = File.ReadAllText(Path.Combine(_root, "s", "knowledge-connector-secrets.json"));
        Dictionary<string, string> raw = JsonSerializer.Deserialize<Dictionary<string, string>>(file)!;
        raw["a"] = raw["b"];   // a blob moved to another source id must not decrypt
        File.WriteAllText(Path.Combine(_root, "s", "knowledge-connector-secrets.json"), JsonSerializer.Serialize(raw));
        Assert.Null(store.Get("a"));
        store.Forget("b");
        Assert.False(store.Has("b"));
    }
}

internal static class YouTubeConnectorProbe
{
    public static string? Extract(string html) => typeof(KnowledgeConnectors).Assembly
        .GetType("PaRuX.Server.Knowledge.YouTubeConnector")!
        .GetMethod("ExtractPlayerResponse", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(null, new object[] { html }) as string;
}
