using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Watched folders (debounced, Gaming-aware re-index) and the hybrid-by-default search method with its honest fallbacks.</summary>
public sealed class KnowledgeWatchAndRetrievalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-watch-" + Guid.NewGuid().ToString("N"));

    public KnowledgeWatchAndRetrievalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Folder(string name = "docs")
    {
        string f = Path.Combine(_root, name);
        Directory.CreateDirectory(f);
        return f;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int milliseconds = 8000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    // ------------------------------------------------------------------ watcher

    [Fact]
    public async Task A_burst_of_changes_becomes_one_reindex_after_things_go_quiet()
    {
        string folder = Folder();
        var calls = new List<string>();
        using var watcher = new KnowledgeFolderWatcher(id => { lock (calls) calls.Add(id); }, () => false, TimeSpan.FromMilliseconds(250), dataRoot: Path.Combine(_root, "data"));
        Assert.True(watcher.Watch("s1", folder));

        for (int i = 0; i < 6; i++)
        {
            File.WriteAllText(Path.Combine(folder, "note" + i + ".txt"), "content " + i);
            await Task.Delay(30);
        }

        Assert.True(await WaitFor(() => { lock (calls) return calls.Count >= 1; }));
        await Task.Delay(700);
        lock (calls) Assert.Equal(new[] { "s1" }, calls);
    }

    [Fact]
    public async Task Edits_and_deletes_in_subfolders_also_trigger_but_editor_temp_files_do_not()
    {
        string folder = Folder();
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        string existing = Path.Combine(folder, "sub", "a.txt");
        File.WriteAllText(existing, "one");
        var calls = new List<string>();
        using var watcher = new KnowledgeFolderWatcher(id => { lock (calls) calls.Add(id); }, () => false, TimeSpan.FromMilliseconds(200), dataRoot: Path.Combine(_root, "data"));
        watcher.Watch("s1", folder);

        File.WriteAllText(Path.Combine(folder, "sub", "scratch.tmp"), "x");
        File.WriteAllText(Path.Combine(folder, "~$lock.docx"), "x");
        await Task.Delay(700);
        lock (calls) Assert.Empty(calls);

        File.Delete(existing);
        Assert.True(await WaitFor(() => { lock (calls) return calls.Count == 1; }));
    }

    [Fact]
    public async Task While_gaming_is_active_nothing_is_indexed_and_it_catches_up_afterwards()
    {
        string folder = Folder();
        bool gaming = true;
        var calls = new List<string>();
        using var watcher = new KnowledgeFolderWatcher(id => { lock (calls) calls.Add(id); }, () => Volatile.Read(ref gaming),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), Path.Combine(_root, "data"));
        watcher.Watch("s1", folder);

        File.WriteAllText(Path.Combine(folder, "a.txt"), "new");
        await Task.Delay(800);
        lock (calls) Assert.Empty(calls);

        Volatile.Write(ref gaming, false);
        Assert.True(await WaitFor(() => { lock (calls) return calls.Count == 1; }));
    }

    [Fact]
    public async Task Turning_it_off_stops_further_reindexing()
    {
        string folder = Folder();
        var calls = new List<string>();
        using var watcher = new KnowledgeFolderWatcher(id => { lock (calls) calls.Add(id); }, () => false, TimeSpan.FromMilliseconds(100), dataRoot: Path.Combine(_root, "data"));
        watcher.Watch("s1", folder);
        Assert.True(watcher.IsWatching("s1"));

        watcher.Unwatch("s1");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "new");
        await Task.Delay(600);

        Assert.False(watcher.IsWatching("s1"));
        lock (calls) Assert.Empty(calls);
    }

    [Fact]
    public void A_missing_sensitive_or_private_data_folder_cannot_be_watched()
    {
        string data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(data, "inner"));
        string ssh = Path.Combine(_root, "home", ".ssh");
        Directory.CreateDirectory(ssh);
        using var watcher = new KnowledgeFolderWatcher(_ => { }, () => false, TimeSpan.FromMilliseconds(100), dataRoot: data);

        Assert.False(watcher.Watch("a", Path.Combine(_root, "does-not-exist")));
        Assert.False(watcher.Watch("b", Path.Combine(data, "inner")));
        Assert.False(watcher.Watch("c", ssh));
        Assert.False(watcher.Watch("d", ""));
        Assert.False(watcher.IsWatching("a") || watcher.IsWatching("b") || watcher.IsWatching("c"));
    }

    [Fact]
    public async Task A_failing_reindex_does_not_kill_the_watcher()
    {
        string folder = Folder();
        int calls = 0;
        using var watcher = new KnowledgeFolderWatcher(_ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("boom"); },
            () => false, TimeSpan.FromMilliseconds(100), dataRoot: Path.Combine(_root, "data"));
        watcher.Watch("s1", folder);

        File.WriteAllText(Path.Combine(folder, "a.txt"), "1");
        Assert.True(await WaitFor(() => Volatile.Read(ref calls) == 1));
        File.WriteAllText(Path.Combine(folder, "b.txt"), "2");
        Assert.True(await WaitFor(() => Volatile.Read(ref calls) == 2));
    }

    [Fact]
    public void The_watched_set_survives_restarts_and_only_stores_what_changed()
    {
        string path = Path.Combine(_root, "watched.json");
        var store = new WatchedFolderStore(path);
        Assert.Empty(store.Get());
        store.Set("b", true);
        store.Set("a", true);
        store.Set("a", true);
        Assert.Equal(new[] { "a", "b" }, new WatchedFolderStore(path).Get().OrderBy(x => x));
        store.Set("a", false);
        store.Set("missing", false);
        Assert.Equal(new[] { "b" }, new WatchedFolderStore(path).Get());
        File.WriteAllText(path, "not json");
        Assert.Empty(new WatchedFolderStore(path).Get());
    }

    [Fact]
    public async Task A_watched_folder_end_to_end_makes_a_new_file_searchable_without_a_manual_reindex()
    {
        string folder = Folder("kb");
        using var index = new KnowledgeIndex(Path.Combine(_root, "k.db"));
        File.WriteAllText(Path.Combine(folder, "first.txt"), "The boiler pressure relief valve opens at six bar.");
        await index.IndexFolderAsync("s1", folder, null, null, default);
        int runs = 0;
        using var watcher = new KnowledgeFolderWatcher(id =>
        {
            Interlocked.Increment(ref runs);
            index.IndexFolderAsync(id, folder, null, null, default).GetAwaiter().GetResult();
        }, () => false, TimeSpan.FromMilliseconds(200), dataRoot: Path.Combine(_root, "data"));
        watcher.Watch("s1", folder);
        Assert.Empty(index.Search("turbine", KnowledgeSearchMode.Lexical, 5, new[] { "s1" }));

        File.WriteAllText(Path.Combine(folder, "second.txt"), "The turbine bearing is greased every month.");

        Assert.True(await WaitFor(() => index.Search("turbine", KnowledgeSearchMode.Lexical, 5, new[] { "s1" }).Count > 0));
        Assert.Equal(1, runs);
    }

    // ------------------------------------------------------------------ retrieval: hybrid by default, honest fallbacks

    /// <summary>Concepts instead of words, so a paraphrase with no shared keyword still lands on the same axis.</summary>
    private sealed class ConceptEmbedder : IRetrievalEmbeddingProvider
    {
        private static readonly string[][] Axes =
        {
            new[] { "car", "automobile", "vehicle" },
            new[] { "maintenance", "servicing", "repair" },
            new[] { "year", "yearly", "annual", "annually" },
            new[] { "weather", "rain", "sunny", "picnic" },
        };
        public bool Available { get; set; } = true;
        public RetrievalEmbeddingIdentity Identity { get; } = new("test", "concept-model", "1", 4, "none");
        public bool IsAvailable => Available;
        public float RelevanceThreshold => 0.5f;
        public float[]? TryEmbed(string text)
        {
            string lower = text.ToLowerInvariant();
            float[] v = Axes.Select(a => a.Any(lower.Contains) ? 1f : 0f).ToArray();
            float n = MathF.Sqrt(v.Sum(x => x * x));
            return n == 0 ? v : v.Select(x => x / n).ToArray();
        }
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) => texts.Select(TryEmbed).ToList();
    }

    private async Task<(KnowledgeIndex Index, bool[] KeywordOnly)> Corpus(ConceptEmbedder? embedder)
    {
        string docs = Folder("corpus");
        File.WriteAllText(Path.Combine(docs, "vehicles.txt"), "The automobile requires servicing every year to stay safe on the road.");
        File.WriteAllText(Path.Combine(docs, "weather.txt"), "Rain stopped and it stayed sunny for the picnic by the lake.");
        File.WriteAllText(Path.Combine(docs, "invoices.txt"), "Invoice number 4711 was paid on the first of March.");
        var keywordOnly = new[] { false };
        IRetrievalEmbeddingProvider? wrapped = embedder == null ? null : new SwitchableEmbedder(embedder, () => keywordOnly[0]);
        var index = KnowledgeJobService.CreateIndex(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"), wrapped);
        await index.IndexFolderAsync("s1", docs, null, null, default);
        return (index, keywordOnly);
    }

    private const string Paraphrase = "how often does my car need maintenance";

    [Fact]
    public async Task Automatic_search_is_hybrid_and_finds_a_paraphrase_that_shares_no_keyword()
    {
        var (index, _) = await Corpus(new ConceptEmbedder());
        using (index)
        {
            Assert.Empty(index.Search(Paraphrase, KnowledgeSearchMode.Lexical, 3, new[] { "s1" }));   // the keyword leg alone cannot

            IReadOnlyList<KnowledgeHit> hits = index.Search(Paraphrase, KnowledgeSearchMode.Auto, 3, new[] { "s1" });

            Assert.NotEmpty(hits);
            Assert.Equal("vehicles.txt", hits[0].FileName);
        }
    }

    [Fact]
    public async Task An_exact_identifier_still_wins_over_meaning()
    {
        var (index, _) = await Corpus(new ConceptEmbedder());
        using (index)
        {
            IReadOnlyList<KnowledgeHit> hits = index.Search("4711", KnowledgeSearchMode.Auto, 3, new[] { "s1" });
            Assert.Equal("invoices.txt", Assert.Single(hits).FileName);
        }
    }

    [Fact]
    public async Task Without_an_embedder_search_falls_back_to_keyword_and_still_answers_keyword_questions()
    {
        var (index, _) = await Corpus(null);
        using (index)
        {
            Assert.Empty(index.Search(Paraphrase, KnowledgeSearchMode.Auto, 3, new[] { "s1" }));      // honest: no meaning match without the model
            IReadOnlyList<KnowledgeHit> keyword = index.Search("picnic lake", KnowledgeSearchMode.Auto, 3, new[] { "s1" });
            Assert.Equal("weather.txt", keyword[0].FileName);
        }
    }

    [Fact]
    public async Task Choosing_keyword_only_switches_meaning_off_and_switching_back_restores_it()
    {
        var (index, keywordOnly) = await Corpus(new ConceptEmbedder());
        using (index)
        {
            keywordOnly[0] = true;
            Assert.Empty(index.Search(Paraphrase, KnowledgeSearchMode.Auto, 3, new[] { "s1" }));
            Assert.Equal("invoices.txt", index.Search("invoice march", KnowledgeSearchMode.Auto, 3, new[] { "s1" })[0].FileName);

            keywordOnly[0] = false;
            Assert.Equal("vehicles.txt", index.Search(Paraphrase, KnowledgeSearchMode.Auto, 3, new[] { "s1" })[0].FileName);
        }
    }

    [Fact]
    public void The_switchable_embedder_never_calls_the_model_while_keyword_only_is_chosen()
    {
        int calls = 0;
        var inner = new CountingEmbedder(() => calls++);
        bool keyword = true;
        var switchable = new SwitchableEmbedder(inner, () => keyword);

        Assert.False(switchable.IsAvailable);
        Assert.Null(switchable.TryEmbed("x"));
        Assert.All(switchable.TryEmbedBatch(new[] { "a", "b" }), v => Assert.Null(v));
        Assert.Equal(0, calls);

        keyword = false;
        Assert.True(switchable.IsAvailable);
        Assert.NotNull(switchable.TryEmbed("x"));
        Assert.Equal(1, calls);
        Assert.Same(inner, switchable.Inner);
    }

    private sealed class CountingEmbedder : IRetrievalEmbeddingProvider
    {
        private readonly Action _onEmbed;
        public CountingEmbedder(Action onEmbed) => _onEmbed = onEmbed;
        public RetrievalEmbeddingIdentity Identity { get; } = new("test", "counting", "1", 2, "none");
        public bool IsAvailable => true;
        public float RelevanceThreshold => 0.5f;
        public float[]? TryEmbed(string text) { _onEmbed(); return new[] { 1f, 0f }; }
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) => texts.Select(TryEmbed).ToList();
    }

    [Fact]
    public void The_status_explains_what_search_does_and_why_for_every_situation()
    {
        var installed = new ConceptEmbedder();
        var missing = new ConceptEmbedder { Available = false };

        KnowledgeRetrievalStatus hybrid = KnowledgeRetrieval.Describe("auto", installed, false, false);
        Assert.Equal("hybrid", hybrid.Mode);
        Assert.Contains("concept-model", hybrid.Reason);
        Assert.True(hybrid.EmbedderInstalled);
        Assert.False(hybrid.CanInstallEmbedder);
        Assert.Single(hybrid.Choices, c => c.Selected && c.Id == "auto");

        KnowledgeRetrievalStatus keywordChosen = KnowledgeRetrieval.Describe("keyword", installed, false, false);
        Assert.Equal("keyword", keywordChosen.Mode);
        Assert.Contains("as you chose", keywordChosen.Reason);
        Assert.True(keywordChosen.EmbedderInstalled);                       // installed, just not used
        Assert.Single(keywordChosen.Choices, c => c.Selected && c.Id == "keyword");

        KnowledgeRetrievalStatus notInstalled = KnowledgeRetrieval.Describe("auto", missing, false, false);
        Assert.Equal("keyword", notInstalled.Mode);
        Assert.Contains("not installed yet", notInstalled.Reason);
        Assert.True(notInstalled.CanInstallEmbedder);

        KnowledgeRetrievalStatus offline = KnowledgeRetrieval.Describe("auto", missing, false, true);
        Assert.Contains("strict offline", offline.Reason);
        Assert.False(offline.CanInstallEmbedder);
        Assert.True(offline.OfflineBlocked);

        KnowledgeRetrievalStatus none = KnowledgeRetrieval.Describe(null, null, true, false);
        Assert.Equal("auto", none.Method);
        Assert.Equal("keyword", none.Mode);
        Assert.True(none.RerankerInstalled);
        Assert.Equal(KnowledgeSearchMethods.Auto, KnowledgeSearchMethods.Normalize("garbage"));
        Assert.False(KnowledgeSearchMethods.IsValid("semantic"));
    }

    [Fact]
    public void The_search_method_is_saved_with_the_knowledge_settings()
    {
        string path = Path.Combine(_root, "settings.json");
        var store = new KnowledgeSettingsStore(path);
        Assert.Equal("auto", store.Get().SearchMethod);
        KnowledgeSettings s = store.Get();
        s.SearchMethod = "keyword";
        store.Save(s);
        Assert.Equal("keyword", new KnowledgeSettingsStore(path).Get().SearchMethod);
    }
}

/// <summary>Routes for web sources, watched folders and the search method: desktop owner only, nothing stored or fetched on a refused request.</summary>
public sealed class KnowledgeWebWatchEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public KnowledgeWebWatchEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient Desktop()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://192.168.0.10/admin")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("")]
    public async Task Adding_a_private_or_invalid_address_is_a_400_with_no_source_created(string url)
    {
        using HttpClient desktop = Desktop();
        HttpResponseMessage response = await desktop.PostAsJsonAsync("/api/v1/knowledge/web-sources", new WebSourceRequest { Url = url, Mode = "page" });
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, "status " + response.StatusCode);
    }

    [Fact]
    public async Task Listing_and_the_retrieval_status_work_for_the_desktop_and_not_without_the_token()
    {
        using HttpClient desktop = Desktop();
        Assert.Equal(HttpStatusCode.OK, (await desktop.GetAsync("/api/v1/knowledge/web-sources")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desktop.GetAsync("/api/v1/knowledge/watched")).StatusCode);
        HttpResponseMessage retrieval = await desktop.GetAsync("/api/v1/knowledge/retrieval");
        Assert.Equal(HttpStatusCode.OK, retrieval.StatusCode);
        string json = await retrieval.Content.ReadAsStringAsync();
        Assert.Contains("\"mode\"", json);
        Assert.Contains("\"choices\"", json);

        using HttpClient anonymous = _factory.CreateClient();
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/knowledge/web-sources")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/v1/knowledge/web-sources", new WebSourceRequest { Url = "https://example.com/" })).StatusCode);
    }

    [Fact]
    public async Task An_invalid_search_method_is_rejected_and_not_saved()
    {
        using HttpClient desktop = Desktop();
        HttpResponseMessage response = await desktop.PutAsJsonAsync("/api/v1/knowledge/settings", new KnowledgeSettingsUpdate { SearchMethod = "semantic-only" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Watching_an_unknown_source_is_a_404()
    {
        using HttpClient desktop = Desktop();
        HttpResponseMessage response = await desktop.PostAsJsonAsync("/api/v1/knowledge/sources/does-not-exist/watch", new WatchFolderRequest { Enabled = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
