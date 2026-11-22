using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class StreamingModeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-stream-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_dir, "display-journal.json");

    public StreamingModeServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHost : IStreamingHost
    {
        public SunshineProbe Probe = new(true, false);
        public bool HangStart, FailStop;
        public readonly List<string> Log = new();
        public SunshineProbe ProbeSunshine() => Probe;
        public async Task<OwnedProcessHandle> StartSunshineAsync(CancellationToken ct)
        {
            Log.Add("start");
            if (HangStart) await Task.Delay(Timeout.Infinite, ct);
            return new OwnedProcessHandle(4242, DateTime.UtcNow);
        }
        public Task<bool> StopOwnedSunshineAsync(OwnedProcessHandle h, CancellationToken ct)
        {
            Log.Add("stop " + h.Pid);
            return Task.FromResult(!FailStop);
        }
    }

    private sealed class FakeDisplay : IStreamingDisplayController
    {
        public readonly Dictionary<string, string> Values = new() { ["D1|Hdr"] = "off", ["D1|RefreshRate"] = "60" };
        public bool FailApply, FailRestore, MonitorGone, Hang;
        public Action? OnApply;
        public string? Read(string id, DisplayChangeKind k) => Values.TryGetValue(id + "|" + k, out string? v) ? v : null;
        public bool Apply(string id, DisplayChangeKind k, string v)
        {
            if (Hang) Thread.Sleep(2000);
            if (FailApply) return false;
            Values[id + "|" + k] = v; OnApply?.Invoke(); return true;
        }
        public RestoreOutcome Restore(DisplayJournalEntry e)
        {
            if (MonitorGone) return RestoreOutcome.NotApplicable;
            if (FailRestore) return RestoreOutcome.Failed;
            Values[e.DisplayId + "|" + e.Kind] = e.PreviousValue;
            return RestoreOutcome.Restored;
        }
    }

    private (StreamingModeService svc, FakeHost host, FakeDisplay disp, DisplayChangeJournal journal) Make(int timeoutMs = 500)
    {
        var host = new FakeHost(); var disp = new FakeDisplay(); var journal = new DisplayChangeJournal(JournalPath);
        return (new StreamingModeService(host, disp, journal, TimeSpan.FromMilliseconds(timeoutMs)), host, disp, journal);
    }

    private static StreamingStartRequest Req(bool sun = true, bool disp = true, string kind = "Hdr") =>
        new(sun, new StreamingDisplayTarget("D1", Enum.Parse<DisplayChangeKind>(kind), kind == "Hdr" ? "on" : "120"), disp);

    [Fact]
    public async Task Start_WithConsent_StartsOwnedSunshine_ChangesDisplay_AndJournals()
    {
        var (svc, host, disp, journal) = Make();
        StreamingStatus s = await svc.StartAsync(Req(), CancellationToken.None);
        Assert.Equal(StreamingState.Active, s.State);
        Assert.True(s.SunshineOwnedByPaRuX);
        Assert.True(s.DisplayRestorePending);
        Assert.Equal("on", disp.Values["D1|Hdr"]);
        Assert.Single(journal.Pending());
        Assert.Equal("off", journal.Pending()[0].PreviousValue);
    }

    [Fact]
    public async Task Stop_RestoresDisplay_StopsOwnedSunshine_ClearsJournal()
    {
        var (svc, host, disp, journal) = Make();
        await svc.StartAsync(Req(), CancellationToken.None);
        StreamingStatus s = await svc.StopAsync();
        Assert.Equal(StreamingState.Idle, s.State);
        Assert.Equal("off", disp.Values["D1|Hdr"]);
        Assert.Empty(journal.Pending());
        Assert.Contains("stop 4242", host.Log);
        Assert.False(s.SunshineOwnedByPaRuX);
    }

    [Fact]
    public async Task Start_WithoutSunshineConsent_FailsHonestly_AndStartsNothing()
    {
        var (svc, host, disp, _) = Make();
        StreamingStatus s = await svc.StartAsync(Req(sun: false), CancellationToken.None);
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Contains("consent", s.Message);
        Assert.DoesNotContain("start", host.Log);
        Assert.Equal("off", disp.Values["D1|Hdr"]);
    }

    [Fact]
    public async Task Start_WithoutDisplayConsent_SkipsDisplay_ButStillActive()
    {
        var (svc, _, disp, journal) = Make();
        StreamingStatus s = await svc.StartAsync(Req(disp: false), CancellationToken.None);
        Assert.Equal(StreamingState.Active, s.State);
        Assert.Equal("off", disp.Values["D1|Hdr"]);
        Assert.Empty(journal.Pending());
        Assert.Contains(s.Steps, x => x.Name == "display" && x.Outcome == StreamingStepOutcome.Skipped);
    }

    [Fact]
    public async Task Start_SunshineNotInstalled_FailsAndNeverInstalls()
    {
        var (svc, host, _, _) = Make();
        host.Probe = new SunshineProbe(false, false);
        StreamingStatus s = await svc.StartAsync(Req(), CancellationToken.None);
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Contains("not installed", s.Message);
        Assert.Empty(host.Log);
    }

    [Fact]
    public async Task UserManagedRunningSunshine_IsNeverStopped()
    {
        var (svc, host, _, _) = Make();
        host.Probe = new SunshineProbe(true, true);
        StreamingStatus s = await svc.StartAsync(Req(sun: false), CancellationToken.None);
        Assert.Equal(StreamingState.Active, s.State);
        Assert.False(s.SunshineOwnedByPaRuX);
        await svc.StopAsync();
        Assert.Empty(host.Log); // neither started nor stopped
    }

    [Fact]
    public async Task Cancellation_DuringSunshineStart_RollsBack_AndLeavesNoJournal()
    {
        var (svc, host, disp, journal) = Make(5000);
        host.HangStart = true;
        using var cts = new CancellationTokenSource();
        Task<StreamingStatus> t = svc.StartAsync(Req(), cts.Token);
        cts.CancelAfter(50);
        StreamingStatus s = await t;
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Contains(s.Steps, x => x.Outcome == StreamingStepOutcome.Cancelled);
        Assert.Empty(journal.Pending());
        Assert.Equal("off", disp.Values["D1|Hdr"]);
    }

    [Fact]
    public async Task Cancellation_AlreadyCancelledToken_DoesNotChangeDisplay()
    {
        var (svc, _, disp, journal) = Make();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        StreamingStatus s = await svc.StartAsync(Req(), cts.Token);
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Equal("off", disp.Values["D1|Hdr"]);
        Assert.Empty(journal.Pending());
    }

    [Fact]
    public async Task Timeout_DuringSunshineStart_IsBounded_AndReportedAsTimedOut()
    {
        var (svc, host, _, _) = Make(100);
        host.HangStart = true;
        StreamingStatus s = await svc.StartAsync(Req(), CancellationToken.None);
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Contains(s.Steps, x => x.Outcome == StreamingStepOutcome.TimedOut);
    }

    [Fact]
    public async Task DisplayApplyTimeout_RestoresFromJournal_AndStopsOwnedSunshine()
    {
        var (svc, host, disp, journal) = Make(150);
        disp.Hang = true;
        StreamingStatus s = await svc.StartAsync(Req(), CancellationToken.None);
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Empty(journal.Pending());
        Assert.Contains("stop 4242", host.Log);
    }

    [Fact]
    public async Task DisplayApplyRejected_RollsBackSunshine_AndClearsJournal()
    {
        var (svc, host, disp, journal) = Make();
        disp.FailApply = true;
        StreamingStatus s = await svc.StartAsync(Req(), CancellationToken.None);
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Empty(journal.Pending());
        Assert.Contains("stop 4242", host.Log);
    }

    [Fact]
    public async Task UnreadableCurrentValue_ChangesNothing()
    {
        var (svc, _, disp, journal) = Make();
        StreamingStatus s = await svc.StartAsync(
            new StreamingStartRequest(true, new StreamingDisplayTarget("D9", DisplayChangeKind.Hdr, "on"), true), CancellationToken.None);
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.Empty(journal.Pending());
        Assert.False(disp.Values.ContainsKey("D9|Hdr"));
    }

    [Fact]
    public async Task Stop_WhenRestoreFails_KeepsJournal_ReportsFailed_AndBlocksNextStart()
    {
        var (svc, _, disp, journal) = Make();
        await svc.StartAsync(Req(), CancellationToken.None);
        disp.FailRestore = true;
        StreamingStatus s = await svc.StopAsync();
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.True(s.DisplayRestorePending);
        Assert.Single(journal.Pending());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(Req(), CancellationToken.None));
    }

    [Fact]
    public async Task CrashRecovery_NewInstance_RestoresDisplayFromJournal()
    {
        var (svc, _, disp, _) = Make();
        await svc.StartAsync(Req(), CancellationToken.None);
        // Simulated crash: the first service is abandoned without Stop; display is still changed.
        Assert.Equal("on", disp.Values["D1|Hdr"]);
        var svc2 = new StreamingModeService(new FakeHost(), disp, new DisplayChangeJournal(JournalPath), TimeSpan.FromMilliseconds(500));
        DisplayRecoveryReport r = svc2.RecoverAfterCrash();
        Assert.Equal(1, r.Restored);
        Assert.True(r.Clean);
        Assert.Equal("off", disp.Values["D1|Hdr"]);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task CrashRecovery_MonitorGone_DiscardsEntry()
    {
        var (svc, _, disp, journal) = Make();
        await svc.StartAsync(Req(), CancellationToken.None);
        disp.MonitorGone = true;
        DisplayRecoveryReport r = new StreamingModeService(new FakeHost(), disp, journal, TimeSpan.FromSeconds(1)).RecoverAfterCrash();
        Assert.Equal(1, r.Discarded);
        Assert.Empty(journal.Pending());
    }

    [Fact]
    public async Task CrashRecovery_RestoreFails_KeepsEntryForRetry_ThenSucceeds()
    {
        var (svc, _, disp, journal) = Make();
        await svc.StartAsync(Req(), CancellationToken.None);
        disp.FailRestore = true;
        var svc2 = new StreamingModeService(new FakeHost(), disp, journal, TimeSpan.FromSeconds(1));
        Assert.Equal(1, svc2.RecoverAfterCrash().Failed);
        Assert.Single(journal.Pending());
        disp.FailRestore = false;
        Assert.Equal(1, svc2.RecoverAfterCrash().Restored);
        Assert.Empty(journal.Pending());
    }

    [Fact]
    public void Journal_CorruptFile_IsQuarantined_AndReported()
    {
        File.WriteAllText(JournalPath, "{ not json");
        var journal = new DisplayChangeJournal(JournalPath);
        DisplayRecoveryReport r = journal.RecoverAfterCrash(new FakeDisplay());
        Assert.True(r.JournalWasCorrupt);
        Assert.False(r.Clean);
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains(".corrupt-"));
    }

    [Fact]
    public void Journal_Begin_IsBounded()
    {
        var journal = new DisplayChangeJournal(JournalPath);
        for (int i = 0; i < DisplayChangeJournal.MaxEntries; i++)
            journal.Begin(new DisplayJournalEntry("e" + i, DisplayChangeKind.Hdr, "D1", "off", "on", DateTime.UtcNow, "t"));
        Assert.Throws<InvalidOperationException>(() =>
            journal.Begin(new DisplayJournalEntry("x", DisplayChangeKind.Hdr, "D1", "off", "on", DateTime.UtcNow, "t")));
    }

    [Fact]
    public void Journal_Recovery_RestoresNewestFirst_AndOnlyOwnedEntries()
    {
        var journal = new DisplayChangeJournal(JournalPath);
        journal.Begin(new DisplayJournalEntry("a", DisplayChangeKind.Hdr, "D1", "off", "on", DateTime.UtcNow, "streaming-mode"));
        journal.Begin(new DisplayJournalEntry("b", DisplayChangeKind.RefreshRate, "D1", "60", "120", DateTime.UtcNow, "other"));
        journal.Begin(new DisplayJournalEntry("c", DisplayChangeKind.Resolution, "D1", "1080p", "4k", DateTime.UtcNow, "streaming-mode"));
        var order = new List<string>();
        journal.RecoverAfterCrash(new Recorder(order), "streaming-mode");
        Assert.Equal(new[] { "c", "a" }, order);
        Assert.Equal("b", Assert.Single(journal.Pending()).Id);
    }

    private sealed class Recorder : IDisplayChangeRestorer
    {
        private readonly List<string> _log;
        public Recorder(List<string> log) => _log = log;
        public RestoreOutcome Restore(DisplayJournalEntry e) { _log.Add(e.Id); return RestoreOutcome.Restored; }
    }

    [Fact]
    public async Task DoubleStart_IsRejected_WhileActive()
    {
        var (svc, _, _, _) = Make();
        await svc.StartAsync(Req(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(Req(), CancellationToken.None));
    }

    [Fact]
    public async Task Stop_WhenSunshineStopFails_ReportsFailed_AndStillOwned()
    {
        var (svc, host, _, _) = Make();
        await svc.StartAsync(Req(disp: false), CancellationToken.None);
        host.FailStop = true;
        StreamingStatus s = await svc.StopAsync();
        Assert.Equal(StreamingState.Failed, s.State);
        Assert.True(s.SunshineOwnedByPaRuX);
    }
}
