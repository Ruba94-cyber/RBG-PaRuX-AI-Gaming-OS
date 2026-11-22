using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Persistence, legacy import and sign-in trigger of the after-sign-in app list (synthetic files only).</summary>
public sealed class SignInAppListPersistenceTests : IDisposable
{
    private const string GoodAumid = "Contoso.App_8wekyb3d8bbwe!App";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-signin-store-" + Guid.NewGuid().ToString("N"));

    public SignInAppListPersistenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string StorePath => Path.Combine(_root, "server", "sign-in-apps.json");
    private SignInAppListStore NewStore() => new(StorePath);

    private string Exe(string name = "tool.exe")
    {
        string p = Path.Combine(_root, name);
        File.WriteAllText(p, "synthetic");
        return p;
    }

    private static SignInAppEntry ExeEntry(string name, string path) => new(name, SignInAppKind.Exe, Path: path);

    private sealed class FakeLauncher : ISignInAppLauncher
    {
        public List<string> Launched = [];
        public bool IsProcessRunning(string n) => false;
        public Task<string?> LaunchAsync(SignInAppEntry e, CancellationToken ct) { Launched.Add(e.Name); return Task.FromResult<string?>(null); }
    }

    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };

    private static readonly DateTimeOffset SignIn = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private static SignInTriggerRequest Req(string key = "s1", int shellAfterMinutes = 1, bool console = false) =>
        new(key, SignIn, SignIn.AddMinutes(shellAfterMinutes), console);

    // ---- persistence ----

    [Fact]
    public void Fresh_Store_IsEmptyAndOff()
    {
        SignInAppListState s = NewStore().Load();
        Assert.False(s.List.Enabled);
        Assert.Empty(s.List.Apps);
        Assert.False(s.Damaged);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Save_RoundTrips_EnabledSettingsAndOrder()
    {
        var store = NewStore();
        var settings = new SignInAppListSettings(7, 11, 33, 4, 9);
        var (state, error) = store.Save(new SignInAppListSaveRequest(true, settings,
            [ExeEntry("B", Exe("b.exe")), new("Store", SignInAppKind.Appx, Aumid: GoodAumid), ExeEntry("A", Exe("a.exe")) with { SkipInConsole = true, Args = "--min" }]));
        Assert.Null(error);
        Assert.NotNull(state);

        SignInAppListState back = NewStore().Load();
        Assert.True(back.List.Enabled);
        Assert.Equal(settings, back.List.Settings);
        Assert.Equal(["B", "Store", "A"], back.List.Apps.Select(a => a.Name).ToArray());
        Assert.Equal(SignInAppKind.Appx, back.List.Apps[1].Kind);
        Assert.True(back.List.Apps[2].SkipInConsole);
        Assert.Equal("--min", back.List.Apps[2].Args);
    }

    [Fact]
    public void Save_RejectsInvalidEntry_AndLeavesFileUntouched()
    {
        var store = NewStore();
        Assert.Null(store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("Good", Exe())])).Error);
        string before = File.ReadAllText(StorePath);

        Assert.NotNull(store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("Bad", Path.Combine(_root, "missing.exe"))])).Error);
        Assert.NotNull(store.Save(new SignInAppListSaveRequest(true, null, [new("X", SignInAppKind.Exe, Path: Exe("c.exe"), Args: "a & b")])).Error);
        Assert.NotNull(store.Save(new SignInAppListSaveRequest(true, new SignInAppListSettings(LaunchSpacingSeconds: 9999), null)).Error);
        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    [Fact]
    public void Save_RejectsDuplicateNames_AndTooManyApps()
    {
        var store = NewStore();
        string exe = Exe();
        Assert.NotNull(store.Save(new SignInAppListSaveRequest(false, null, [ExeEntry("Same", exe), ExeEntry("same", exe)])).Error);
        var many = Enumerable.Range(0, SignInAppListPolicy.MaxApps + 1).Select(i => ExeEntry("n" + i, exe)).ToArray();
        Assert.NotNull(store.Save(new SignInAppListSaveRequest(false, null, many)).Error);
    }

    [Fact]
    public void Save_KeepsPreviousGoodFileAsBackup()
    {
        var store = NewStore();
        store.Save(new SignInAppListSaveRequest(false, null, [ExeEntry("One", Exe())]));
        store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("One", Exe()), ExeEntry("Two", Exe("two.exe"))]));
        Assert.True(File.Exists(store.BackupPath));
        SignInAppListParseResult bak = SignInAppListPolicy.ParseStored(File.ReadAllText(store.BackupPath));
        Assert.Single(bak.Value!.Apps);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "*.tmp"));
    }

    [Fact]
    public void DamagedFile_FallsBackToBackup_OrReportsDamaged_WithoutOverwritingOnRead()
    {
        var store = NewStore();
        store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("One", Exe())]));
        store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("One", Exe()), ExeEntry("Two", Exe("two.exe"))]));
        File.WriteAllText(StorePath, "{ not json");
        SignInAppListState viaBackup = store.Load();
        Assert.False(viaBackup.Damaged);
        Assert.Single(viaBackup.List.Apps);
        Assert.NotEmpty(viaBackup.Warnings);

        File.Delete(store.BackupPath);
        SignInAppListState damaged = store.Load();
        Assert.True(damaged.Damaged);
        Assert.False(damaged.List.Enabled);
        Assert.Equal("{ not json", File.ReadAllText(StorePath));
        Assert.False(store.MarkRan("x"));
    }

    [Fact]
    public void OversizedFile_IsTreatedAsDamaged()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, new string(' ', SignInAppListPolicy.MaxFileBytes + 10));
        Assert.True(NewStore().Load().Damaged);
    }

    [Fact]
    public void VanishedExe_IsKeptAndFlagged_NotDropped()
    {
        string exe = Exe("gone.exe");
        var store = NewStore();
        store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("Gone", exe), ExeEntry("Other", Exe("o.exe"))]));
        File.Delete(exe);

        SignInAppListState s = NewStore().Load();
        Assert.Equal(2, s.List.Apps.Count);
        Assert.NotEmpty(s.Warnings);
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(s.List.Apps[0]));

        // An unchanged broken entry survives an unrelated edit (here: the opt-in flag) ...
        var (kept, keptError) = store.Save(new SignInAppListSaveRequest(false, null, null));
        Assert.Null(keptError);
        Assert.Equal(2, kept!.List.Apps.Count);
        // ... and the owner can still remove it.
        var (removed, error) = store.Save(new SignInAppListSaveRequest(true, null, [s.List.Apps[1]]));
        Assert.Null(error);
        Assert.Single(removed!.List.Apps);
    }

    [Fact]
    public void LinkedStoreFile_IsNotWritten()
    {
        var store = NewStore();
        store.Save(new SignInAppListSaveRequest(false, null, [ExeEntry("One", Exe())]));
        string target = Path.Combine(_root, "elsewhere.json");
        File.Move(StorePath, target);
        try { File.CreateSymbolicLink(StorePath, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // no symlink right on this PC
        Assert.True(store.Load().Damaged);
        Assert.NotNull(store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("One", Exe())])).Error);
    }

    // ---- legacy import (preview + confirm semantics live in the store/endpoint; the parse is read only) ----

    private string LegacyFile(object apps, string name = "DelayedApps.json")
    {
        string p = Path.Combine(_root, name);
        File.WriteAllText(p, JsonSerializer.Serialize(new { Settings = new { LaunchSpacingSeconds = 5 }, Apps = apps }));
        return p;
    }

    [Fact]
    public void Import_Replace_SavesDisabled_AndNeverChangesTheSourceFile()
    {
        string legacy = LegacyFile(new object[] { new { Name = "Tool", Kind = "exe", Path = Exe(), SkipInConsole = true } });
        string before = File.ReadAllText(legacy);
        var store = NewStore();
        store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("Old", Exe("old.exe"))]));

        SignInAppListParseResult parsed = SignInAppListPolicy.ImportLegacyFile(legacy);
        Assert.True(parsed.Ok);
        var (state, error) = store.ApplyImport(parsed.Value!, append: false);
        Assert.Null(error);
        Assert.False(state!.List.Enabled);
        Assert.Equal(["Tool"], state.List.Apps.Select(a => a.Name).ToArray());
        Assert.Equal(5, state.List.Settings.LaunchSpacingSeconds);
        Assert.False(NewStore().Load().List.Enabled);
        Assert.Equal(before, File.ReadAllText(legacy));
    }

    [Fact]
    public void Import_Append_KeepsExisting_SkipsSameName_AndStaysDisabled()
    {
        string legacy = LegacyFile(new object[]
        {
            new { Name = "Tool", Kind = "exe", Path = Exe("t.exe") },
            new { Name = "New", Kind = "exe", Path = Exe("n.exe") },
        });
        var store = NewStore();
        store.Save(new SignInAppListSaveRequest(true, new SignInAppListSettings(LaunchSpacingSeconds: 9), [ExeEntry("tool", Exe("t2.exe"))]));
        var (state, error) = store.ApplyImport(SignInAppListPolicy.ImportLegacyFile(legacy).Value!, append: true);
        Assert.Null(error);
        Assert.Equal(["tool", "New"], state!.List.Apps.Select(a => a.Name).ToArray());
        Assert.Equal(9, state.List.Settings.LaunchSpacingSeconds);
        Assert.False(state.List.Enabled);
    }

    [Fact]
    public void Import_InvalidFile_ChangesNothing()
    {
        var store = NewStore();
        store.Save(new SignInAppListSaveRequest(true, null, [ExeEntry("Keep", Exe())]));
        string before = File.ReadAllText(StorePath);
        string bad = LegacyFile(new object[] { new { Name = "Bad", Kind = "exe", Path = Path.Combine(_root, "nope.exe") } });
        Assert.False(SignInAppListPolicy.ImportLegacyFile(bad).Ok);
        Assert.False(SignInAppListPolicy.ImportLegacyFile(Path.Combine(_root, "missing.json")).Ok);
        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    // ---- trigger ----

    [Fact]
    public void TriggerPolicy_Decisions()
    {
        SignInTriggerDecision D(bool enabled = true, int n = 1, bool gaming = false, bool running = false, string? last = null, SignInTriggerRequest? r = null) =>
            SignInTriggerPolicy.Decide(enabled, n, gaming, running, last, r ?? Req());
        Assert.Equal(SignInTriggerDecision.Started, D());
        Assert.Equal(SignInTriggerDecision.Disabled, D(enabled: false));
        Assert.Equal(SignInTriggerDecision.EmptyList, D(n: 0));
        Assert.Equal(SignInTriggerDecision.GamingActive, D(gaming: true));
        Assert.Equal(SignInTriggerDecision.AlreadyRanThisSignIn, D(last: "s1"));
        Assert.Equal(SignInTriggerDecision.Started, D(last: "older"));
        Assert.Equal(SignInTriggerDecision.NotAtSignIn, D(r: Req(shellAfterMinutes: 11)));
        Assert.Equal(SignInTriggerDecision.NotAtSignIn, D(r: Req(shellAfterMinutes: -5)));
        Assert.Equal(SignInTriggerDecision.SignInUnknown, D(r: new SignInTriggerRequest(null, null, null, false)));
        Assert.Equal(SignInTriggerDecision.SignInUnknown, D(r: Req(key: new string('k', 65))));
        Assert.Equal(SignInTriggerDecision.AlreadyRunning, D(running: true));
    }

    private SignInAppListRunner NewRunner(SignInAppListStore store, FakeLauncher launcher, Func<bool>? gaming = null) =>
        new(store, launcher, gaming ?? (() => false), NoDelay);

    private SignInAppListStore Seed(bool enabled, params SignInAppEntry[] apps)
    {
        var store = NewStore();
        Assert.Null(store.Save(new SignInAppListSaveRequest(enabled, new SignInAppListSettings(0, 0, 1, 0, 0), apps)).Error);
        return store;
    }

    [Fact]
    public async Task Trigger_Disabled_LaunchesNothing_AndConsumesNothing()
    {
        var launcher = new FakeLauncher();
        var store = Seed(false, ExeEntry("A", Exe()));
        using var runner = NewRunner(store, launcher);
        Assert.Equal(SignInTriggerDecision.Disabled, runner.Trigger(Req()).Decision);
        await runner.CurrentRun;
        Assert.Empty(launcher.Launched);
        Assert.Null(store.Load().LastRunSessionKey);
    }

    [Fact]
    public async Task Trigger_Enabled_RunsOncePerSignIn_InOrder()
    {
        var launcher = new FakeLauncher();
        var store = Seed(true, ExeEntry("A", Exe("a.exe")), ExeEntry("B", Exe("b.exe")));
        using var runner = NewRunner(store, launcher);

        Assert.Equal(SignInTriggerDecision.Started, runner.Trigger(Req("s1")).Decision);
        await runner.CurrentRun;
        Assert.Equal(["A", "B"], launcher.Launched);
        Assert.Equal(SignInRunState.Completed, runner.Snapshot().State);
        Assert.Equal("s1", store.Load().LastRunSessionKey);

        // The shell restarts in the same sign-in: nothing runs again, even from a fresh runner (state is persisted).
        using var second = NewRunner(NewStore(), launcher);
        Assert.Equal(SignInTriggerDecision.AlreadyRanThisSignIn, second.Trigger(Req("s1")).Decision);
        await second.CurrentRun;
        Assert.Equal(2, launcher.Launched.Count);

        // A new sign-in runs again.
        Assert.Equal(SignInTriggerDecision.Started, second.Trigger(Req("s2")).Decision);
        await second.CurrentRun;
        Assert.Equal(4, launcher.Launched.Count);
    }

    [Fact]
    public async Task Trigger_GamingActive_DoesNotRun_AndKeepsTheSignInUnconsumed()
    {
        var launcher = new FakeLauncher();
        var store = Seed(true, ExeEntry("A", Exe()));
        using var runner = NewRunner(store, launcher, gaming: () => true);
        Assert.Equal(SignInTriggerDecision.GamingActive, runner.Trigger(Req()).Decision);
        await runner.CurrentRun;
        Assert.Empty(launcher.Launched);
        Assert.Null(store.Load().LastRunSessionKey);
        Assert.Equal(SignInTriggerDecision.GamingActive, runner.RunNow().Decision);
    }

    [Fact]
    public async Task Trigger_ConsoleActive_SkipsSkipInConsoleApps()
    {
        var launcher = new FakeLauncher();
        var store = Seed(true, ExeEntry("Desk", Exe("d.exe")) with { SkipInConsole = true }, ExeEntry("Always", Exe("e.exe")));
        using var runner = NewRunner(store, launcher);
        runner.Trigger(Req(console: true));
        await runner.CurrentRun;
        Assert.Equal(["Always"], launcher.Launched);
        Assert.Contains(runner.Snapshot().Results, r => r.Name == "Desk" && r.Outcome == SignInAppOutcome.SkippedConsoleMode);
    }

    [Fact]
    public async Task Trigger_ShellStartedLongAfterSignIn_DoesNotRun()
    {
        var launcher = new FakeLauncher();
        var store = Seed(true, ExeEntry("A", Exe()));
        using var runner = NewRunner(store, launcher);
        Assert.Equal(SignInTriggerDecision.NotAtSignIn, runner.Trigger(Req(shellAfterMinutes: 120)).Decision);
        await runner.CurrentRun;
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Cancel_StopsARunInProgress()
    {
        var launcher = new FakeLauncher();
        var store = Seed(true, ExeEntry("A", Exe("a.exe")), ExeEntry("B", Exe("b.exe")));
        var gate = new TaskCompletionSource();
        using var runner = new SignInAppListRunner(store, launcher, () => false, async (_, ct) => { await gate.Task.WaitAsync(ct); });
        // A lead delay of 0 means the first wait is skipped; give the run a wait via a non-zero desktop delay.
        store.Save(new SignInAppListSaveRequest(true, new SignInAppListSettings(0, 5, 1, 0, 0), null));
        Assert.Equal(SignInTriggerDecision.Started, runner.Trigger(Req()).Decision);
        runner.Cancel();
        await runner.CurrentRun;
        Assert.Equal(SignInRunState.Cancelled, runner.Snapshot().State);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void Contracts_SerializeEnumsAsStrings_ForTheApi()
    {
        var status = new SignInAppListStatus(true, new SignInAppListSettings(),
            [new SignInAppEntryStatus(new SignInAppEntry("A", SignInAppKind.Appx, Aumid: GoodAumid), null)],
            new SignInRunSnapshot(SignInRunState.Idle, 0, 0, []), null, false, 50);
        string json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"kind\":\"Appx\"", json);
        Assert.Contains("\"state\":\"Idle\"", json);
        SignInAppListStatus? back = JsonSerializer.Deserialize<SignInAppListStatus>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(SignInAppKind.Appx, back!.Apps[0].Entry.Kind);
    }
}
