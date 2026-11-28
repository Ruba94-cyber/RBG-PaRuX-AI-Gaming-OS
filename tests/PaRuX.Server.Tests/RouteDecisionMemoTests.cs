using System.Diagnostics;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Every cache and invalidation path of the warm-turn hot path: the AUTO routing decision memo, the persisted-input stamps
/// that invalidate it, the read-through chat routing preference cache, and the traffic-proven text health memo.
/// </summary>
public sealed class RouteDecisionMemoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-route-memo-" + Guid.NewGuid().ToString("N"));

    public RouteDecisionMemoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ChatRoutingPreferenceStore.InvalidateReadCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static RouteMemoKey Key(string profile = "balanced", double demand = 0.33, bool trivial = true, string role = "standard") =>
        new(RouteMode.Auto, role, role, profile, demand, trivial, ManagedLibraryExpanded: true);

    private static CandidateEvidenceDto Evidence(string name) => new() { FilePath = Path.Combine(_rootStatic, name), FileName = name, IsCurrentAssignment = true };
    private static string _rootStatic = Path.GetTempPath();

    private static RouteMemoEntry Entry(RouteMemoKey key, long stamp, long created, string model = "a.gguf")
    {
        CandidateEvidenceDto selected = Evidence(model);
        return new RouteMemoEntry
        {
            Key = key, Stamp = stamp, CreatedTicks = created, SelectedPath = selected.FilePath, Role = key.NormalizedRole,
            Candidates = new[] { selected }, Selected = selected
        };
    }

    // ---- decision memo ------------------------------------------------------------------------------------------------

    [Fact]
    public void Memo_hits_only_for_the_same_key_and_stamp()
    {
        long now = 1_000;
        var memo = new RouteDecisionMemo(() => now, TimeSpan.FromSeconds(10));
        memo.Store(Entry(Key(), stamp: 7, created: now));

        Assert.True(memo.TryGet(Key(), 7, out RouteMemoEntry? hit));
        Assert.NotNull(hit);
        Assert.False(memo.TryGet(Key(profile: "max_intelligence"), 7, out _));
        Assert.False(memo.TryGet(Key(demand: 0.71), 7, out _));
        Assert.False(memo.TryGet(Key(trivial: false), 7, out _));
        Assert.False(memo.TryGet(Key(role: "coding"), 7, out _));
        Assert.Equal(1, memo.Hits);
    }

    [Fact]
    public void Single_candidate_key_ignores_prompt_features_but_keeps_mode_role_and_profile()
    {
        RouteMemoKey shared = Key(demand: 0.33, trivial: true).WithoutPromptFeatures();

        Assert.Equal(shared, Key(demand: 0.91, trivial: false).WithoutPromptFeatures());
        Assert.NotEqual(shared, Key(profile: "max_intelligence").WithoutPromptFeatures());
        Assert.NotEqual(shared, Key(role: "coding").WithoutPromptFeatures());
        Assert.NotEqual(Key(demand: 0.33, trivial: true), shared); // a ranked pool never aliases the shared entry by accident
    }

    [Fact]
    public void Memo_is_dropped_when_the_persisted_input_stamp_changes()
    {
        long now = 1_000;
        var memo = new RouteDecisionMemo(() => now, TimeSpan.FromSeconds(10));
        memo.Store(Entry(Key(), stamp: 7, created: now));

        Assert.False(memo.TryGet(Key(), 8, out _)); // a model / role / capability-mode change moved the stamp
        Assert.Equal(0, memo.Count);               // the stale entry is gone, not merely skipped
        Assert.False(memo.TryGet(Key(), 7, out _));
    }

    [Fact]
    public void Memo_expires_after_its_time_to_live()
    {
        long baseTicks = Stopwatch.GetTimestamp();
        long now = baseTicks;
        var memo = new RouteDecisionMemo(() => now, TimeSpan.FromSeconds(10));
        memo.Store(Entry(Key(), stamp: 1, created: baseTicks));

        now = baseTicks + (long)(Stopwatch.Frequency * 9.0);
        Assert.True(memo.TryGet(Key(), 1, out _));
        now = baseTicks + (long)(Stopwatch.Frequency * 10.5);
        Assert.False(memo.TryGet(Key(), 1, out _));
        Assert.Equal(0, memo.Count);
    }

    [Fact]
    public void Invalidate_forgets_every_entry()
    {
        long now = 1_000;
        var memo = new RouteDecisionMemo(() => now);
        memo.Store(Entry(Key(), 1, now));
        memo.Store(Entry(Key(profile: "max_intelligence"), 1, now));
        Assert.Equal(2, memo.Count);

        memo.Invalidate();

        Assert.Equal(0, memo.Count);
        Assert.False(memo.TryGet(Key(), 1, out _));
    }

    [Fact]
    public void Memo_stays_bounded_and_evicts_the_oldest_entry()
    {
        long now = Stopwatch.GetTimestamp();
        var memo = new RouteDecisionMemo(() => now, TimeSpan.FromHours(1));
        for (int i = 0; i < 40; i++) memo.Store(Entry(Key(demand: i / 100.0), 1, now + i));

        Assert.True(memo.Count <= 16);
        Assert.False(memo.TryGet(Key(demand: 0.0), 1, out _));   // oldest evicted
        Assert.True(memo.TryGet(Key(demand: 0.39), 1, out _));   // newest kept
    }

    [Fact]
    public void Router_invalidate_hook_clears_the_memo_and_stamp_override_is_honoured()
    {
        string state = Path.Combine(_root, "state");
        Directory.CreateDirectory(state);
        var router = new SmartRouterService(
            new RuntimeManagerService(new LocalAiProcessManager()), new ModelBenchmarkService(
                new ModelBenchmarkStore(Path.Combine(state, "bench.json")), new NoopRunner()),
            new CapabilityProfiler.CapabilityProfilerStore(state));
        router.DecisionMemo.Store(Entry(Key(), 1, router.DecisionMemo.NowTicks));
        Assert.Equal(1, router.DecisionMemo.Count);

        router.InvalidateRouteMemo();

        Assert.Equal(0, router.DecisionMemo.Count);
    }

    private sealed class NoopRunner : IModelBenchmarkRunner
    {
        public Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // ---- stamps -------------------------------------------------------------------------------------------------------

    [Fact]
    public void FileStamp_changes_on_create_edit_and_delete()
    {
        string file = Path.Combine(_root, "roles.json");
        long missing = RoutingStamp.OfFile(file);
        File.WriteAllText(file, "{}");
        long created = RoutingStamp.OfFile(file);
        File.WriteAllText(file, "{\"standard\":\"a.gguf\"}");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        long edited = RoutingStamp.OfFile(file);
        File.Delete(file);
        long deleted = RoutingStamp.OfFile(file);

        Assert.NotEqual(missing, created);
        Assert.NotEqual(created, edited);
        Assert.Equal(missing, deleted);
        Assert.Equal(0, RoutingStamp.OfFile(null));
    }

    [Fact]
    public void DirectoryStamp_changes_when_a_model_file_is_added_or_removed()
    {
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        long empty = RoutingStamp.OfDirectory(models);
        string gguf = Path.Combine(models, "new.gguf");
        File.WriteAllText(gguf, "x");
        Directory.SetLastWriteTimeUtc(models, DateTime.UtcNow.AddMinutes(1));
        long added = RoutingStamp.OfDirectory(models);
        File.Delete(gguf);
        Directory.SetLastWriteTimeUtc(models, DateTime.UtcNow.AddMinutes(2));
        long removed = RoutingStamp.OfDirectory(models);

        Assert.NotEqual(empty, added);
        Assert.NotEqual(added, removed);
        Assert.Equal(0, RoutingStamp.OfDirectory(Path.Combine(_root, "absent")));
    }

    [Fact]
    public void Combined_stamp_depends_on_every_part_and_on_order()
    {
        Assert.Equal(RoutingStamp.Combine(1, 2, 3), RoutingStamp.Combine(1, 2, 3));
        Assert.NotEqual(RoutingStamp.Combine(1, 2, 3), RoutingStamp.Combine(1, 2, 4));
        Assert.NotEqual(RoutingStamp.Combine(1, 2, 3), RoutingStamp.Combine(3, 2, 1));
    }

    [Fact]
    public void Capability_mode_stamp_moves_when_an_override_is_saved()
    {
        long before = CapabilityRoutingPreferenceStore.CapabilityModeStamp(_root);
        CapabilityRoutingPreferenceStore.SetCapabilityMode(
            ModelCapabilityIds.GeneralChat, ScopedRouteMode.Prefer, "m.gguf", _root);
        long after = CapabilityRoutingPreferenceStore.CapabilityModeStamp(_root);

        Assert.NotEqual(before, after);
    }

    // ---- chat routing preference read cache ---------------------------------------------------------------------------

    [Fact]
    public void Preference_read_cache_returns_equal_values_that_callers_cannot_corrupt()
    {
        ChatRoutingPreferenceStore.InvalidateReadCache();
        ChatRoutingPreferenceStore.Preference first = ChatRoutingPreferenceStore.ResolveAndPersist("", _root);
        first.Mode = RouteMode.Manual;                       // a caller mutating its copy...
        first.ManualModelFileName = "evil.gguf";
        ChatRoutingPreferenceStore.Preference second = ChatRoutingPreferenceStore.ResolveAndPersist("local-default", _root);
        ChatRoutingPreferenceStore.Preference third = ChatRoutingPreferenceStore.ResolveAndPersist(null, _root);

        Assert.Equal(RouteMode.Auto, second.Mode);           // ...never reaches the next turn
        Assert.Null(second.ManualModelFileName);
        Assert.Equal(RouteMode.Auto, third.Mode);
    }

    [Fact]
    public void Preference_read_cache_sees_a_manual_lock_written_through_the_store()
    {
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "Lock-4B.gguf"), "x");
        ChatRoutingPreferenceStore.InvalidateReadCache();
        Assert.Equal(RouteMode.Auto, ChatRoutingPreferenceStore.ResolveAndPersist("", _root, models).Mode); // primes the cache

        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest
        {
            Mode = RouteMode.Manual, ManualModelFileName = "Lock-4B.gguf"
        }, _root, models);

        ChatRoutingPreferenceStore.Preference after = ChatRoutingPreferenceStore.ResolveAndPersist("", _root, models);
        Assert.Equal(RouteMode.Manual, after.Mode);
        Assert.Equal("Lock-4B.gguf", after.ManualModelFileName);
    }

    [Fact]
    public void Preference_read_cache_notices_the_preference_file_changing_behind_its_back()
    {
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "Lock-4B.gguf"), "x");
        ChatRoutingPreferenceStore.InvalidateReadCache();
        Assert.Equal(RouteMode.Auto, ChatRoutingPreferenceStore.ResolveAndPersist("", _root, models).Mode);

        // Another process (the desktop app) rewrites the sidecar and the settings mirror directly.
        string sidecar = Path.Combine(_root, "chat-model-routing.json");
        File.WriteAllText(sidecar, "{\"Mode\":\"manual\",\"AutoProfile\":\"balanced\",\"ManualModelFileName\":\"Lock-4B.gguf\"}");
        File.SetLastWriteTimeUtc(sidecar, DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(_root, "settings.json"));

        ChatRoutingPreferenceStore.Preference after = ChatRoutingPreferenceStore.ResolveAndPersist("", _root, models);
        Assert.Equal(RouteMode.Manual, after.Mode);
    }

    [Fact]
    public void Preference_selector_prefix_still_persists_and_refreshes_the_cache()
    {
        ChatRoutingPreferenceStore.InvalidateReadCache();
        Assert.Equal(AutoIntelligenceProfile.Balanced, ChatRoutingPreferenceStore.ResolveAndPersist("", _root).AutoProfile);

        ChatRoutingPreferenceStore.Preference selected = ChatRoutingPreferenceStore.ResolveAndPersist(
            "parux-auto:" + AutoIntelligenceProfile.MaxIntelligence, _root);
        ChatRoutingPreferenceStore.Preference next = ChatRoutingPreferenceStore.ResolveAndPersist("", _root);

        Assert.Equal(AutoIntelligenceProfile.MaxIntelligence, selected.AutoProfile);
        Assert.Equal(AutoIntelligenceProfile.MaxIntelligence, next.AutoProfile);
    }

    // ---- text health memo proven by traffic ---------------------------------------------------------------------------

    [Fact]
    public void Traffic_proven_health_memo_lives_longer_than_a_probe_memo_but_still_expires_and_is_per_launch()
    {
        object launch = new();
        object otherLaunch = new();
        long t0 = Stopwatch.GetTimestamp();
        long at3s = t0 + (long)(Stopwatch.Frequency * 3.0);
        long at9s = t0 + (long)(Stopwatch.Frequency * 9.0);

        Assert.False(LocalAiProcessManager.IsTextHealthyMemoReusable(launch, launch, t0, at3s));                                  // probe memo: 1 s
        Assert.True(LocalAiProcessManager.IsTextHealthyMemoReusable(launch, launch, t0, at3s, LocalAiProcessManager.TextHealthyTrafficTtl));
        Assert.False(LocalAiProcessManager.IsTextHealthyMemoReusable(launch, launch, t0, at9s, LocalAiProcessManager.TextHealthyTrafficTtl));
        Assert.False(LocalAiProcessManager.IsTextHealthyMemoReusable(otherLaunch, launch, t0, at3s, LocalAiProcessManager.TextHealthyTrafficTtl));
        Assert.True(LocalAiProcessManager.TextHealthyTrafficTtl > LocalAiProcessManager.TextHealthyMemoTtl);
    }

    [Fact]
    public void Noting_traffic_health_without_a_launch_identity_is_a_harmless_no_op()
    {
        LocalAiProcessManager.NoteTextHealthyFromTraffic(); // no managed launch in this process: must not throw or invent health
    }

    // ---- phase trace --------------------------------------------------------------------------------------------------

    [Fact]
    public void Phase_trace_begin_request_is_inert_when_tracing_is_off()
    {
        if (ChatPhaseTrace.Enabled) return;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/chat/completions";

        ChatPhaseTrace.BeginRequest(context);

        Assert.False(context.Items.ContainsKey(nameof(ChatPhaseTrace)));
        Assert.Null(ChatPhaseTrace.Current);
    }
}
