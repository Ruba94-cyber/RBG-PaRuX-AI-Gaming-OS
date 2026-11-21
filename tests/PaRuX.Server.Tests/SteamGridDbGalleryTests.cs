using System.Net;
using System.Text;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SteamGridDbGalleryTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public List<HttpRequestMessage> Requests { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_reply(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static SteamGridDbGallery Make(FakeHandler handler, string? key = "abcdef123456") =>
        new(new HttpClient(handler), () => key);

    private const string Good = """
    {"success":true,"data":[
      {"id":1,"url":"https://cdn2.steamgriddb.com/grid/a.png","thumb":"https://cdn2.steamgriddb.com/thumb/a.jpg","width":600,"height":900,"score":3},
      {"id":2,"url":"https://cdn2.steamgriddb.com/grid/b.png","thumb":"https://evil.example/t.jpg","width":600,"height":900,"score":9},
      {"id":3,"url":"https://evil.example/grid/c.png","thumb":"https://cdn2.steamgriddb.com/thumb/c.jpg","score":99},
      {"id":4,"url":"http://cdn2.steamgriddb.com/grid/d.png","score":50},
      {"id":5,"url":"https://cdn2.steamgriddb.com/grid/e.png","nsfw":true,"score":70},
      {"id":6,"url":"https://cdn2.steamgriddb.com/grid/f.png","humor":true,"score":70}
    ]}
    """;

    [Fact]
    public void Parse_keeps_only_safe_cdn_candidates_sorted_by_score()
    {
        var list = SteamGridDbGallery.ParseCandidates(Good, SteamGridDbArtKind.Grid, 24)!;
        Assert.Equal(new long[] { 2, 1 }, list.Select(c => c.Id).ToArray());
        Assert.Equal(list[0].Url, list[0].ThumbUrl); // foreign thumb host falls back to the verified full URL
        Assert.All(list, c => Assert.Equal(SteamGridDbArtKind.Grid, c.Kind));
    }

    [Fact]
    public void Parse_bounds_the_result_count()
    {
        var items = string.Join(",", Enumerable.Range(1, 100).Select(i => $"{{\"id\":{i},\"url\":\"https://cdn2.steamgriddb.com/g/{i}.png\",\"score\":{i}}}"));
        var list = SteamGridDbGallery.ParseCandidates("{\"success\":true,\"data\":[" + items + "]}", SteamGridDbArtKind.Hero, 24)!;
        Assert.Equal(24, list.Count);
        Assert.Equal(100, list[0].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"success\":false,\"errors\":[\"x\"]}")]
    [InlineData("{\"success\":true}")]
    public void Parse_rejects_malformed_documents(string? json) =>
        Assert.Null(SteamGridDbGallery.ParseCandidates(json, SteamGridDbArtKind.Logo, 24));

    [Fact]
    public void Parse_rejects_oversized_documents() =>
        Assert.Null(SteamGridDbGallery.ParseCandidates(new string(' ', (int)SteamGridDbGallery.MaxListBytes + 1), SteamGridDbArtKind.Grid, 24));

    [Fact]
    public void Store_key_keeps_grid_as_the_cover_and_suffixes_other_kinds()
    {
        Assert.Equal("t", SteamGridDbGallery.StoreKey("t", SteamGridDbArtKind.Grid));
        Assert.Equal("t\u001fheroes", SteamGridDbGallery.StoreKey("t", SteamGridDbArtKind.Hero));
        Assert.NotEqual(SteamGridDbGallery.StoreKey("t", SteamGridDbArtKind.Logo), SteamGridDbGallery.StoreKey("t", SteamGridDbArtKind.Icon));
    }

    [Fact]
    public async Task List_without_key_refuses_and_sends_nothing()
    {
        var handler = new FakeHandler(_ => Json(Good));
        var result = await Make(handler, key: null).ListAsync(5, SteamGridDbArtKind.Grid, CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.NoKey, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task List_calls_only_the_api_host_with_bearer_key_never_in_the_url()
    {
        var handler = new FakeHandler(_ => Json(Good));
        var result = await Make(handler).ListAsync(42, SteamGridDbArtKind.Hero, CancellationToken.None);
        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("www.steamgriddb.com", request.RequestUri!.Host);
        Assert.StartsWith("/api/v2/heroes/game/42", request.RequestUri.AbsolutePath);
        Assert.Contains("nsfw=false", request.RequestUri.Query);
        Assert.DoesNotContain("abcdef123456", request.RequestUri.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("abcdef123456", request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SteamGridDbStatus.KeyRejected)]
    [InlineData(HttpStatusCode.Forbidden, SteamGridDbStatus.KeyRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, SteamGridDbStatus.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, SteamGridDbStatus.GameNotFound)]
    [InlineData(HttpStatusCode.InternalServerError, SteamGridDbStatus.Network)]
    [InlineData(HttpStatusCode.Found, SteamGridDbStatus.Network)]
    public async Task List_maps_http_errors_honestly(HttpStatusCode code, SteamGridDbStatus expected)
    {
        var result = await Make(new FakeHandler(_ => Json("{}", code))).ListAsync(1, SteamGridDbArtKind.Icon, CancellationToken.None);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task List_reports_bad_response_and_empty_gallery()
    {
        var bad = await Make(new FakeHandler(_ => Json("garbage"))).ListAsync(1, SteamGridDbArtKind.Grid, CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.BadResponse, bad.Status);
        var empty = await Make(new FakeHandler(_ => Json("{\"success\":true,\"data\":[]}"))).ListAsync(1, SteamGridDbArtKind.Grid, CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.NoArtwork, empty.Status);
    }

    [Fact]
    public async Task List_network_failure_is_an_error_status_and_cancellation_propagates()
    {
        var failing = new FakeHandler(_ => throw new HttpRequestException("down"));
        Assert.Equal(SteamGridDbStatus.Network, (await Make(failing).ListAsync(1, SteamGridDbArtKind.Grid, CancellationToken.None)).Status);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(new FakeHandler(_ => Json(Good))).ListAsync(1, SteamGridDbArtKind.Grid, cts.Token));
    }

    [Fact]
    public async Task FindGame_picks_the_close_match_and_rejects_unrelated_results()
    {
        var hit = new FakeHandler(_ => Json("{\"success\":true,\"data\":[{\"id\":7,\"name\":\"Hades\"}]}"));
        var found = await Make(hit).FindGameAsync("Hades", CancellationToken.None);
        Assert.Equal(7, found.Value);
        Assert.StartsWith("/api/v2/search/autocomplete/Hades", hit.Requests[0].RequestUri!.AbsolutePath);
        var miss = await Make(new FakeHandler(_ => Json("{\"success\":true,\"data\":[{\"id\":9,\"name\":\"Completely Different\"}]}"))).FindGameAsync("Hades", CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.GameNotFound, miss.Status);
    }

    [Theory]
    [InlineData("http://cdn2.steamgriddb.com/a.png")]
    [InlineData("https://evil.example/a.png")]
    [InlineData("https://user@cdn2.steamgriddb.com/a.png")]
    [InlineData("https://www.steamgriddb.com/api/v2/grids/game/1")]
    public async Task FetchImage_blocks_hosts_outside_the_allow_list_without_a_request(string url)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) });
        var result = await Make(handler).FetchImageAsync(url, 1000, CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.Blocked, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchImage_enforces_the_size_cap_and_sends_no_credentials()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) });
        var gallery = Make(handler);
        Assert.Equal(SteamGridDbStatus.TooLarge, (await gallery.FetchImageAsync("https://cdn2.steamgriddb.com/a.png", 1000, CancellationToken.None)).Status);
        var ok = await gallery.FetchImageAsync("https://cdn2.steamgriddb.com/a.png", 5000, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal(5000, ok.Value!.Length);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task FetchImage_treats_empty_body_and_http_errors_as_failures()
    {
        string url = "https://cdn.steamgriddb.com/a.png";
        Assert.Equal(SteamGridDbStatus.BadResponse, (await Make(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) })).FetchImageAsync(url, 100, CancellationToken.None)).Status);
        Assert.Equal(SteamGridDbStatus.Network, (await Make(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found))).FetchImageAsync(url, 100, CancellationToken.None)).Status);
    }
}
