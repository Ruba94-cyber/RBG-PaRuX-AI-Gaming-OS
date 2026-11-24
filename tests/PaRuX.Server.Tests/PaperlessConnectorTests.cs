using System.Net;
using System.Text;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Paperless-ngx connector with a fake HTTP handler (synthetic documents, no network).</summary>
public sealed class PaperlessConnectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-paperless-" + Guid.NewGuid().ToString("N"));
    public PaperlessConnectorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = new();
        public List<string?> AuthHeaders { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Seen) { Seen.Add(request); AuthHeaders.Add(request.Headers.Authorization?.ToString()); }
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Description => "test";
        public string Protect(string purpose, string plaintext) => "enc:" + purpose + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string? Unprotect(string purpose, string blob) =>
            blob.StartsWith("enc:" + purpose + ":", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(blob[("enc:" + purpose + ":").Length..])) : null;
    }

    private WebKnowledgeSources Service(FakeHandler handler, ConnectorLimits? limits = null) =>
        new(Path.Combine(_root, "manifests"), Path.Combine(_root, "content"), _ => "src1", _ => { }, _ => true, () => false, () => false,
            connectors: new KnowledgeConnectors(() => new HttpClient(handler, disposeHandler: false), limits),
            secrets: new ConnectorSecretStore(Path.Combine(_root, "state"), new PlainProtector()), internetEnabled: () => true);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Page(bool hasNext, params (int Id, string Title, string Content)[] docs) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            count = docs.Length, next = hasNext ? "https://ignored.example/next" : null,
            results = docs.Select(d => new { id = d.Id, title = d.Title, content = d.Content }).ToArray()
        });

    [Theory]
    [InlineData("https://paperless.example.lan:8000", "https://paperless.example.lan:8000", "")]
    [InlineData("https://nas.example/paperless/", "https://nas.example", "/paperless")]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000", "")]
    [InlineData("http://localhost:8000/", "http://localhost:8000", "")]
    public void Targets_parse(string input, string origin, string basePath)
    {
        Assert.True(PaperlessTargetParser.TryParse(input, out PaperlessTarget t, out _));
        Assert.Equal(origin, t.Origin.GetLeftPart(UriPartial.Authority));
        Assert.Equal(basePath, t.BasePath);
    }

    [Theory]
    [InlineData("http://192.168.1.20:8000")]
    [InlineData("http://paperless.example.com")]
    [InlineData("ftp://paperless.example.lan")]
    [InlineData("https://user:pw@paperless.example.lan")]
    [InlineData("https://paperless.example.lan/?token=abc")]
    [InlineData("https://paperless.example.lan/a%20b/c")]
    [InlineData("")]
    public void Bad_targets_are_refused_with_a_reason(string input)
    {
        Assert.False(PaperlessTargetParser.TryParse(input, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("172.32.0.9", true)] // outside 172.16/12 it is an ordinary public address
    [InlineData("192.168.50.2", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", false)]
    public void Lan_address_rule_allows_private_but_never_link_local_or_cgnat(string ip, bool allowed) =>
        Assert.Equal(allowed, WebAddressRule.PublicOrPrivateLan("h", System.Net.IPAddress.Parse(ip)));

    [Fact]
    public async Task Import_sends_the_token_header_only_to_the_chosen_host_and_cites_the_paperless_page()
    {
        const string token = "pl_SYNTHETIC_TOKEN_0123456789";
        var h = new FakeHandler();
        h.Respond = r =>
        {
            string u = r.RequestUri!.ToString();
            if (u.Contains("page=1")) return Json(Page(true, (7, "Rental contract", "Synthetic rent clause text"), (8, "Empty scan", "  ")));
            if (u.Contains("page=2")) return Json(Page(false, (9, "Invoice 2026", "Synthetic invoice total 100")));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        WebKnowledgeSources svc = Service(h);
        WebSourceInfo info = svc.BeginConnector("paperless", "https://paperless.example.lan:8000", null, null, null, null, token, false);
        await svc.WaitAsync(info.Id);

        WebSourceInfo done = svc.Get(info.Id)!;
        Assert.Equal("ready", done.State);
        Assert.Equal(2, done.Pages);
        string all = string.Concat(Directory.GetFiles(done.Folder).Select(File.ReadAllText));
        Assert.Contains("https://paperless.example.lan:8000/documents/7/details", all);
        Assert.Contains("Synthetic invoice total 100", all);
        Assert.DoesNotContain(token, all);
        Assert.All(h.AuthHeaders, a => Assert.Equal("Token " + token, a));
        Assert.All(h.Seen, r => Assert.Equal("paperless.example.lan", r.RequestUri!.Host));
        Assert.All(h.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));
        foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.Contains("knowledge-connector-secrets")))
            Assert.DoesNotContain(token, File.ReadAllText(file));
    }

    [Fact]
    public async Task Document_cap_stops_the_import_and_says_so()
    {
        var h = new FakeHandler();
        h.Respond = _ => Json(Page(true, (1, "A", "text a"), (2, "B", "text b"), (3, "C", "text c")));
        WebKnowledgeSources svc = Service(h, new ConnectorLimits { MaxDocs = 2 });
        WebSourceInfo info = svc.BeginConnector("paperless", "https://paperless.example.lan", null, null, null, null, "tok", false);
        await svc.WaitAsync(info.Id);
        Assert.Equal(2, svc.Get(info.Id)!.Pages);
        Assert.Contains("document_cap", svc.Get(info.Id)!.Notice ?? string.Empty);
    }

    [Fact]
    public async Task Missing_token_and_refused_token_are_honest_failures()
    {
        var h = new FakeHandler();
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        WebKnowledgeSources svc = Service(h);
        WebSourceInfo none = svc.BeginConnector("paperless", "https://paperless.example.lan", null, null, null, null, null, false);
        await svc.WaitAsync(none.Id);
        Assert.Equal("auth_required", svc.Get(none.Id)!.Error);
        Assert.Empty(h.Seen);

        WebSourceInfo bad = svc.BeginConnector("paperless", "https://paperless.example.lan", null, null, null, null, "wrong", false);
        await svc.WaitAsync(bad.Id);
        Assert.Equal("auth_required", svc.Get(bad.Id)!.Error);
    }

    [Fact]
    public async Task A_server_that_is_not_paperless_fails_instead_of_importing_nothing_quietly()
    {
        var h = new FakeHandler();
        h.Respond = _ => Json("{\"hello\":\"world\"}");
        WebKnowledgeSources svc = Service(h);
        WebSourceInfo info = svc.BeginConnector("paperless", "https://paperless.example.lan", null, null, null, null, "tok", false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("unreadable_response", svc.Get(info.Id)!.Error);
    }

    [Fact]
    public async Task Loopback_http_is_allowed_but_a_redirect_to_another_host_is_not_followed()
    {
        var h = new FakeHandler();
        h.Respond = r => r.RequestUri!.Host == "127.0.0.1"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/api/documents/") } }
            : Json(Page(false));
        WebKnowledgeSources svc = Service(h);
        WebSourceInfo info = svc.BeginConnector("paperless", "http://127.0.0.1:8000", null, null, null, null, "tok", false);
        await svc.WaitAsync(info.Id);
        Assert.Equal("blocked_host", svc.Get(info.Id)!.Error);
        Assert.DoesNotContain(h.Seen, r => r.RequestUri!.Host == "evil.example");
    }

    [Fact]
    public void Plain_http_to_a_lan_host_is_refused_before_any_request()
    {
        var h = new FakeHandler();
        WebKnowledgeSources svc = Service(h);
        Assert.Throws<ArgumentException>(() => svc.BeginConnector("paperless", "http://192.168.1.20:8000", null, null, null, null, "tok", false));
        Assert.Empty(h.Seen);
    }
}
