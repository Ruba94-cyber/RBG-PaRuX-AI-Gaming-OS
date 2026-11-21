using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RetroAchievementsTests : IDisposable
{
    private const string Key = "AbCdEf0123456789AbCdEf0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-ra-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRa _handler = new();
    private bool _internet = true;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Synthetic RetroAchievements server: a console game list and per-game progress.</summary>
    private sealed class FakeRa : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string GameList { get; set; } = """[{"Title":"Synthetic Quest","ID":777,"ConsoleID":12},{"Title":"Other Game | Alt Name","ID":778,"ConsoleID":12}]""";
        public string Progress { get; set; } = """
        {"ID":777,"Title":"Synthetic Quest","ConsoleName":"PlayStation","NumAchievements":3,"Achievements":{
          "1":{"ID":1,"Title":"Start","Description":"Begin","DateEarned":"2024-05-01 10:00:00"},
          "2":{"ID":2,"Title":"Middle","Description":"Halfway"},
          "3":{"ID":3,"Title":"Hard","Description":"Hard mode","DateEarned":"2024-05-02 11:30:00","DateEarnedHardcore":"2024-05-02 11:31:00"}}}
        """;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request.RequestUri!);
            string path = request.RequestUri!.AbsolutePath;
            string body = path.EndsWith("API_GetGameList.php", StringComparison.Ordinal) ? GameList : Progress;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private RetroAchievementsService NewService() => new(_dir, () => _internet, _handler);
    private static RetroAchievementsCredentialsRequest Good() => new() { UserName = "SyntheticUser", ApiKey = Key };

    [Theory]
    [InlineData("", Key)]
    [InlineData("a", Key)]
    [InlineData("bad name!", Key)]
    [InlineData("SyntheticUser", "short")]
    [InlineData("SyntheticUser", "has spaces in it 0123456789012")]
    public void Malformed_credentials_are_refused_and_nothing_is_written(string user, string key)
    {
        RetroAchievementsService service = NewService();
        Assert.NotNull(service.Save(new RetroAchievementsCredentialsRequest { UserName = user, ApiKey = key }));
        Assert.False(service.Status().Configured);
        Assert.False(File.Exists(Path.Combine(_dir, "retroachievements-credentials.json")));
    }

    [Fact]
    public void The_key_is_stored_encrypted_and_never_reported()
    {
        RetroAchievementsService service = NewService();
        Assert.Null(service.Save(Good()));
        Assert.Equal("SyntheticUser", service.Status().UserName);
        Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(_dir, "retroachievements-credentials.json")), StringComparison.OrdinalIgnoreCase);
        Assert.True(service.Clear());
        Assert.False(service.Status().Configured);
        Assert.False(service.Clear());
    }

    [Fact]
    public async Task Nothing_is_requested_without_credentials_or_with_the_internet_off()
    {
        RetroAchievementsService service = NewService();
        Assert.Contains("Save your RetroAchievements", (await service.GetAsync(777, null, null)).Problem);
        service.Save(Good());
        _internet = false;
        Assert.Contains("Internet", (await service.GetAsync(777, null, null)).Problem);
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public async Task An_explicit_game_id_reads_progress_over_https_and_merges_dates()
    {
        RetroAchievementsService service = NewService();
        service.Save(Good());
        RetroAchievementsResult r = await service.GetAsync(777, null, null);

        Assert.Equal(string.Empty, r.Problem);
        Assert.Equal("Synthetic Quest", r.GameName);
        Assert.Equal("PlayStation", r.ConsoleName);
        Assert.Equal(3, r.Total);
        Assert.Equal(2, r.Unlocked);
        Assert.Equal(new DateTime(2024, 5, 2, 11, 31, 0, DateTimeKind.Utc), r.Achievements.Single(a => a.Key == "3").UnlockedUtc);
        Assert.False(r.Achievements.Single(a => a.Key == "2").Achieved);
        Uri call = Assert.Single(_handler.Calls);
        Assert.Equal("https", call.Scheme);
        Assert.Equal(RetroAchievementsService.Host, call.Host);
        Assert.Contains("g=777", call.Query);
    }

    [Fact]
    public async Task A_title_is_matched_exactly_inside_the_console_list_and_the_list_is_cached()
    {
        RetroAchievementsService service = NewService();
        service.Save(Good());
        RetroAchievementsResult r = await service.GetAsync(0, "synthetic QUEST!", "PlayStation");
        Assert.Equal(string.Empty, r.Problem);
        Assert.Equal(777, r.GameId);
        Assert.Contains(_handler.Calls, c => c.AbsolutePath.EndsWith("API_GetGameList.php") && c.Query.Contains("i=12"));

        _handler.Progress = _handler.Progress.Replace("777", "778");
        RetroAchievementsResult alt = await service.GetAsync(0, "Alt Name", "PlayStation");
        Assert.Equal(778, alt.GameId);
        Assert.Single(_handler.Calls, c => c.AbsolutePath.EndsWith("API_GetGameList.php"));   // second lookup used the cache
    }

    [Fact]
    public async Task Unknown_platform_unknown_title_and_missing_title_give_honest_reasons()
    {
        RetroAchievementsService service = NewService();
        service.Save(Good());
        Assert.Contains("game id", (await service.GetAsync(0, "Synthetic Quest", "PlayStation 3")).Problem);   // RetroAchievements has no PS3
        Assert.Contains("exactly", (await service.GetAsync(0, "No Such Game", "PlayStation")).Problem);
        Assert.Contains("game id", (await service.GetAsync(0, "  ", "PlayStation")).Problem);
        Assert.Contains("valid", (await service.GetAsync(-3, "x", "PlayStation")).Problem);
    }

    [Fact]
    public async Task Refusals_and_garbage_become_plain_reasons_without_the_key()
    {
        RetroAchievementsService service = NewService();
        service.Save(Good());
        _handler.Status = HttpStatusCode.Unauthorized;
        RetroAchievementsResult denied = await service.GetAsync(777, null, null);
        Assert.Contains("refused", denied.Problem);
        Assert.DoesNotContain(Key, denied.Problem, StringComparison.OrdinalIgnoreCase);

        _handler.Status = HttpStatusCode.OK;
        _handler.Progress = "not json";
        Assert.NotEmpty((await service.GetAsync(777, null, null)).Problem);
        _handler.Progress = """{"Error":"Unknown game"}""";
        Assert.Equal("Unknown game", (await service.GetAsync(777, null, null)).Problem);
        _handler.Progress = """{"ID":777,"Title":"Empty","Achievements":{}}""";
        Assert.Contains("no achievements", (await service.GetAsync(777, null, null)).Problem);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        RetroAchievementsService service = NewService();
        service.Save(Good());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(777, null, null, cts.Token));
    }

    [Theory]
    [InlineData("PlayStation", new[] { 12 })]
    [InlineData("PlayStation 2", new[] { 21 })]
    [InlineData("PlayStation Portable", new[] { 41 })]
    [InlineData("Game Boy / Game Boy Color / Game Boy Advance", new[] { 4, 6, 5 })]
    [InlineData("Sega Genesis", new[] { 1 })]
    [InlineData("Nintendo Entertainment System", new[] { 7 })]
    [InlineData("PlayStation 4", new int[0])]
    [InlineData("Nintendo Switch", new int[0])]
    [InlineData("", new int[0])]
    public void Platform_labels_map_to_console_ids(string platform, int[] expected) =>
        Assert.Equal(expected, RetroAchievementsConsoles.ForPlatform(platform));

    [Fact]
    public void The_test_base_url_override_is_loopback_only_and_needs_an_isolated_profile()
    {
        string? oldBase = Environment.GetEnvironmentVariable("PARUX_RA_API_BASE_TEST");
        string? oldRoot = Environment.GetEnvironmentVariable("PARUX_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("PARUX_DATA_ROOT", null);
            Environment.SetEnvironmentVariable("PARUX_RA_API_BASE_TEST", "http://127.0.0.1:9");
            Assert.Equal("https://retroachievements.org", RetroAchievementsService.BaseUrl());
            Environment.SetEnvironmentVariable("PARUX_DATA_ROOT", _dir);
            Assert.Equal("http://127.0.0.1:9", RetroAchievementsService.BaseUrl());
            Environment.SetEnvironmentVariable("PARUX_RA_API_BASE_TEST", "http://evil.example");
            Assert.Equal("https://retroachievements.org", RetroAchievementsService.BaseUrl());
            Environment.SetEnvironmentVariable("PARUX_RA_API_BASE_TEST", "https://127.0.0.1");
            Assert.Equal("https://retroachievements.org", RetroAchievementsService.BaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PARUX_RA_API_BASE_TEST", oldBase);
            Environment.SetEnvironmentVariable("PARUX_DATA_ROOT", oldRoot);
        }
    }
}

public sealed class AchievementUnlockTests
{
    private static AchievementEntryDto E(string key, bool achieved, int? sec = null) =>
        new() { Key = key, Name = "N" + key, Achieved = achieved, UnlockedUtc = sec is null ? null : new DateTime(2024, 1, 1).AddSeconds(sec.Value) };

    [Fact]
    public void Diff_returns_only_new_unlocks_in_unlock_order()
    {
        HashSet<string> known = AchievementUnlockDiff.UnlockedKeys(new[] { E("a", true), E("b", false) });
        var now = new[] { E("a", true), E("b", true, 20), E("c", true, 10), E("d", false), E("", true) };
        Assert.Equal(new[] { "c", "b" }, AchievementUnlockDiff.NewlyUnlocked(known, now).Select(x => x.Key));
    }

    [Fact]
    public void Steam_entries_map_to_the_common_shape()
    {
        AchievementEntryDto e = AchievementUnlockDiff.FromSteam(new SteamAchievementDto { ApiName = "X", Name = "Nm", Achieved = true });
        Assert.Equal("X", e.Key);
        Assert.True(e.Achieved);
    }

    private static AchievementSnapshot Snap(params AchievementEntryDto[] e) => new("Game", e, string.Empty);
    private static AchievementSnapshot Bad(string why = "no") => new(string.Empty, Array.Empty<AchievementEntryDto>(), why);

    private static async Task<(AchievementSessionWatcher W, List<string> Announced)> Run(
        Queue<AchievementSnapshot> reads, Func<bool>? allowed = null, CancellationToken ct = default, Action? onDelay = null)
    {
        var announced = new List<string>();
        var w = new AchievementSessionWatcher(
            _ => reads.Count > 0 ? Task.FromResult(reads.Dequeue()) : throw new OperationCanceledException(),
            allowed ?? (() => true), (g, a) => announced.Add(g + ":" + a.Key),
            (_, _) => { onDelay?.Invoke(); return Task.CompletedTask; }, TimeSpan.Zero);
        await w.RunAsync(ct);
        return (w, announced);
    }

    [Fact]
    public async Task The_first_read_is_a_baseline_and_later_unlocks_are_announced_once()
    {
        var reads = new Queue<AchievementSnapshot>(new[]
        {
            Snap(E("a", true), E("b", false)),    // baseline: "a" already earned, never announced
            Snap(E("a", true), E("b", true)),     // b is new
            Snap(E("a", true), E("b", true)),     // nothing new
            Snap(E("a", true), E("b", true), E("c", true)),
        });
        var (w, announced) = await Run(reads);
        Assert.Equal(new[] { "Game:b", "Game:c" }, announced);
        Assert.True(w.BaselineEstablished);
    }

    [Fact]
    public async Task Persistent_problems_stop_the_session_after_three_reads()
    {
        var reads = new Queue<AchievementSnapshot>(Enumerable.Repeat(Bad(), 10));
        var (w, announced) = await Run(reads);
        Assert.Equal("problems", w.StopReason);
        Assert.Equal(AchievementSessionWatcher.MaxConsecutiveProblems, w.Polls);
        Assert.Empty(announced);
    }

    [Fact]
    public async Task A_transient_problem_does_not_stop_a_healthy_session()
    {
        var reads = new Queue<AchievementSnapshot>(new[] { Snap(E("a", false)), Bad(), Snap(E("a", true)) });
        var (_, announced) = await Run(reads);
        Assert.Equal(new[] { "Game:a" }, announced);
    }

    [Fact]
    public async Task It_stops_when_the_session_is_no_longer_allowed_and_announces_nothing_then()
    {
        bool allowed = true;
        var reads = new Queue<AchievementSnapshot>(new[] { Snap(E("a", false)), Snap(E("a", true)), Snap(E("b", true)) });
        var (w, announced) = await Run(reads, () => allowed, onDelay: () => allowed = false);
        Assert.Equal("not-allowed", w.StopReason);
        Assert.Empty(announced);   // the game ended before the second read was announced
    }

    [Fact]
    public async Task It_never_polls_when_not_allowed_from_the_start()
    {
        var reads = new Queue<AchievementSnapshot>(new[] { Snap(E("a", true)) });
        var (w, _) = await Run(reads, () => false);
        Assert.Equal(0, w.Polls);
        Assert.Equal("not-allowed", w.StopReason);
    }

    [Fact]
    public async Task Cancellation_ends_the_loop_cleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (w, _) = await Run(new Queue<AchievementSnapshot>(), ct: cts.Token);
        Assert.Equal("cancelled", w.StopReason);
        Assert.Equal(0, w.Polls);
    }

    [Fact]
    public async Task The_number_of_reads_is_bounded()
    {
        var reads = new Queue<AchievementSnapshot>(Enumerable.Repeat(Snap(E("a", false)), AchievementSessionWatcher.MaxPolls + 50));
        var (w, _) = await Run(reads);
        Assert.Equal("limit", w.StopReason);
        Assert.Equal(AchievementSessionWatcher.MaxPolls, w.Polls);
    }
}
