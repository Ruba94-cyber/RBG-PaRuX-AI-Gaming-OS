using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SignInAppListTests : IDisposable
{
    private const string GoodAumid = "Contoso.App_8wekyb3d8bbwe!App";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-signin-" + Guid.NewGuid().ToString("N"));

    public SignInAppListTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Exe(string name = "tool.exe")
    {
        string p = Path.Combine(_root, name);
        File.WriteAllText(p, "synthetic");
        return p;
    }

    private static string Json(object apps, object? settings = null) =>
        JsonSerializer.Serialize(new { Settings = settings ?? new { LaunchSpacingSeconds = 2 }, Apps = apps });

    private sealed class FakeLauncher : ISignInAppLauncher
    {
        public HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Launched = [];
        public int FailuresBeforeSuccess;
        public bool AppearsAfterLaunch = true;
        public bool IsProcessRunning(string n) => Running.Contains(n);
        public Task<string?> LaunchAsync(SignInAppEntry e, CancellationToken ct)
        {
            if (FailuresBeforeSuccess > 0) { FailuresBeforeSuccess--; return Task.FromResult<string?>("start-failed"); }
            Launched.Add(e.Name);
            if (AppearsAfterLaunch && SignInAppListPolicy.ProbeName(e) is { } n) Running.Add(n);
            return Task.FromResult<string?>(null);
        }
    }

    private static SignInAppListService Svc(FakeLauncher l, Func<bool>? gaming = null, Func<bool>? console = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(l, gaming ?? (() => false), console ?? (() => false), delay ?? ((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }));

    private SignInAppList List(bool enabled, params SignInAppEntry[] apps) =>
        new(enabled, new SignInAppListSettings(1, 1, 2, 1, 1), apps);

    [Fact]
    public void Parse_LegacySchema_IsAcceptedAndNeverEnabled()
    {
        string json = Json(new object[] { new { Name = "Tool", Kind = "exe", Path = Exe(), Args = "--min", Process = "tool", SkipInConsole = true },
            new { Name = "Store", Kind = "appx", Aumid = GoodAumid } },
            new { LaunchSpacingSeconds = 4, DesktopDelaySeconds = 20, ReadyTimeoutSeconds = 30, ConsoleReadyDelaySeconds = 6, ModeWaitSeconds = 9 });
        SignInAppListParseResult r = SignInAppListPolicy.Parse(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.False(r.Value!.Enabled);
        Assert.Equal(2, r.Value.Apps.Count);
        Assert.Equal(SignInAppKind.Appx, r.Value.Apps[1].Kind);
        Assert.Equal(4, r.Value.Settings.LaunchSpacingSeconds);
        Assert.True(r.Value.Apps[0].SkipInConsole);
    }

    [Fact]
    public void Default_List_IsOff() => Assert.False(SignInAppList.Empty.Enabled);

    [Theory]
    [InlineData("--a & calc")]
    [InlineData("--a | b")]
    [InlineData("x; y")]
    [InlineData("a > out.txt")]
    [InlineData("`whoami`")]
    [InlineData("a\r\nb")]
    public void Args_WithShellMetacharacters_AreRejected(string args)
    {
        var e = new SignInAppEntry("T", SignInAppKind.Exe, Exe(), args);
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(e));
    }

    [Fact]
    public void Args_TooLong_AreRejected() =>
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("T", SignInAppKind.Exe, Exe(), new string('a', 1025))));

    [Fact]
    public void Args_Plain_AreAccepted() =>
        Assert.Null(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("T", SignInAppKind.Exe, Exe(), "--minimized --profile \"Work\"")));

    [Fact]
    public void Path_Traversal_IsRejected()
    {
        Exe();
        string traversal = _root + @"\sub\..\tool.exe";
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("T", SignInAppKind.Exe, traversal)));
    }

    [Theory]
    [InlineData(@"tool.exe")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData(@"C:\definitely\missing\tool.exe")]
    public void Path_RelativeNetworkOrMissing_IsRejected(string path) =>
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("T", SignInAppKind.Exe, path)));

    [Fact]
    public void Path_NonExeExtension_IsRejected()
    {
        string p = Path.Combine(_root, "script.cmd");
        File.WriteAllText(p, "x");
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("T", SignInAppKind.Exe, p)));
    }

    [Fact]
    public void Path_ThroughJunction_IsRejected()
    {
        string real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "tool.exe"), "x");
        string link = Path.Combine(_root, "link");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no privilege to create a link here
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("T", SignInAppKind.Exe, Path.Combine(link, "tool.exe"))));
    }

    [Theory]
    [InlineData("Contoso.App_8wekyb3d8bbwe!App", true)]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("calc.exe", false)]
    [InlineData("Contoso.App_8wekyb3d8bbwe!App & calc", false)]
    [InlineData("Contoso.App_short!App", false)]
    [InlineData("Contoso.App_8wekyb3d8bbwe", false)]
    [InlineData("", false)]
    public void Aumid_Format(string aumid, bool ok) => Assert.Equal(ok, SignInAppListPolicy.IsValidAumid(aumid));

    [Fact]
    public void Appx_WithPathOrArgs_IsRejected()
    {
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("S", SignInAppKind.Appx, Exe(), null, GoodAumid)));
        Assert.NotNull(SignInAppListPolicy.ValidateEntry(new SignInAppEntry("S", SignInAppKind.Appx, null, "--x", GoodAumid)));
    }

    [Theory]
    [InlineData("LaunchSpacingSeconds", 301)]
    [InlineData("LaunchSpacingSeconds", -1)]
    [InlineData("ReadyTimeoutSeconds", 0)]
    [InlineData("ModeWaitSeconds", 121)]
    public void Settings_OutOfBounds_AreRejected(string key, int value)
    {
        string json = "{\"Settings\":{\"" + key + "\":" + value + "},\"Apps\":[]}";
        SignInAppListParseResult r = SignInAppListPolicy.Parse(json);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_TooManyApps_Malformed_AndOversize_Fail()
    {
        string many = Json(Enumerable.Range(0, 51).Select(i => new { Name = "A" + i, Kind = "appx", Aumid = GoodAumid }).ToArray());
        Assert.False(SignInAppListPolicy.Parse(many).Ok);
        Assert.False(SignInAppListPolicy.Parse("{not json").Ok);
        Assert.False(SignInAppListPolicy.Parse("[]").Ok);
        Assert.False(SignInAppListPolicy.Parse(new string(' ', SignInAppListPolicy.MaxFileBytes + 1)).Ok);
    }

    [Fact]
    public void Parse_UnknownKind_IsAnError() =>
        Assert.False(SignInAppListPolicy.Parse(Json(new object[] { new { Name = "X", Kind = "script", Path = Exe() } })).Ok);

    [Fact]
    public void Import_LegacyFile_IsReadOnlyAndDisabled()
    {
        string file = Path.Combine(_root, "DelayedApps.json");
        File.WriteAllText(file, Json(new object[] { new { Name = "Tool", Kind = "exe", Path = Exe() } }));
        DateTime before = File.GetLastWriteTimeUtc(file);
        SignInAppListParseResult r = SignInAppListPolicy.ImportLegacyFile(file);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.False(r.Value!.Enabled);
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
    }

    [Theory]
    [InlineData(@"relative.json")]
    [InlineData(@"\\server\share\DelayedApps.json")]
    [InlineData(@"C:\missing-dir\DelayedApps.json")]
    [InlineData(@"C:\a\..\DelayedApps.json")]
    [InlineData(@"C:\x\file.txt")]
    public void Import_BadPaths_Fail(string path) => Assert.False(SignInAppListPolicy.ImportLegacyFile(path).Ok);

    [Fact]
    public async Task Run_Disabled_LaunchesNothing()
    {
        var l = new FakeLauncher();
        SignInRunSnapshot s = await Svc(l).RunAsync(List(false, new SignInAppEntry("T", SignInAppKind.Exe, Exe())), default);
        Assert.Equal(SignInRunState.SkippedDisabled, s.State);
        Assert.Empty(l.Launched);
    }

    [Fact]
    public async Task Run_LaunchesInOrderWithSpacing()
    {
        var l = new FakeLauncher();
        var delays = new List<TimeSpan>();
        var svc = Svc(l, delay: (t, ct) => { delays.Add(t); return Task.CompletedTask; });
        SignInRunSnapshot s = await svc.RunAsync(List(true,
            new SignInAppEntry("A", SignInAppKind.Exe, Exe("a.exe")), new SignInAppEntry("B", SignInAppKind.Exe, Exe("b.exe"))), default);
        Assert.Equal(SignInRunState.Completed, s.State);
        Assert.Equal(["A", "B"], l.Launched);
        Assert.All(s.Results, r => Assert.Equal(SignInAppOutcome.Launched, r.Outcome));
        Assert.NotEmpty(delays); // lead delay + spacing happened
    }

    [Fact]
    public async Task Run_SkipsAppAlreadyRunning()
    {
        var l = new FakeLauncher();
        l.Running.Add("a");
        SignInRunSnapshot s = await Svc(l).RunAsync(List(true, new SignInAppEntry("A", SignInAppKind.Exe, Exe("a.exe"))), default);
        Assert.Empty(l.Launched);
        Assert.Equal(SignInAppOutcome.SkippedAlreadyRunning, s.Results[0].Outcome);
    }

    [Fact]
    public async Task Run_ConsoleMode_SkipsOnlySkipInConsoleApps()
    {
        var l = new FakeLauncher();
        SignInRunSnapshot s = await Svc(l, console: () => true).RunAsync(List(true,
            new SignInAppEntry("Desk", SignInAppKind.Exe, Exe("d.exe"), SkipInConsole: true),
            new SignInAppEntry("Keep", SignInAppKind.Exe, Exe("k.exe"))), default);
        Assert.Equal(["Keep"], l.Launched);
        Assert.Equal(SignInAppOutcome.SkippedConsoleMode, s.Results[0].Outcome);
    }

    [Fact]
    public async Task Run_GamingActiveAtStart_IsHardOff()
    {
        var l = new FakeLauncher();
        SignInRunSnapshot s = await Svc(l, gaming: () => true).RunAsync(List(true, new SignInAppEntry("A", SignInAppKind.Exe, Exe())), default);
        Assert.Equal(SignInRunState.SkippedGamingActive, s.State);
        Assert.Empty(l.Launched);
    }

    [Fact]
    public async Task Run_GamingStartsMidRun_StopsRemaining()
    {
        var l = new FakeLauncher();
        bool gaming = false;
        var svc = Svc(l, gaming: () => gaming, delay: (t, ct) => { if (l.Launched.Count == 1) gaming = true; return Task.CompletedTask; });
        SignInRunSnapshot s = await svc.RunAsync(List(true,
            new SignInAppEntry("A", SignInAppKind.Exe, Exe("a.exe")), new SignInAppEntry("B", SignInAppKind.Exe, Exe("b.exe"))), default);
        Assert.Equal(["A"], l.Launched);
        Assert.Equal(SignInRunState.SkippedGamingActive, s.State);
        Assert.Equal(2, s.Results.Count);
        Assert.Equal(SignInAppOutcome.SkippedGamingActive, s.Results[1].Outcome);
    }

    [Fact]
    public async Task Run_Cancellation_MarksRemainingCancelled()
    {
        var l = new FakeLauncher();
        using var cts = new CancellationTokenSource();
        var svc = Svc(l, delay: (t, ct) => { if (l.Launched.Count == 1) cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        SignInRunSnapshot s = await svc.RunAsync(List(true,
            new SignInAppEntry("A", SignInAppKind.Exe, Exe("a.exe")), new SignInAppEntry("B", SignInAppKind.Exe, Exe("b.exe"))), cts.Token);
        Assert.Equal(SignInRunState.Cancelled, s.State);
        Assert.Equal(["A"], l.Launched);
        Assert.Equal(SignInAppOutcome.Cancelled, s.Results[1].Outcome);
        Assert.Equal(s, svc.Snapshot());
    }

    [Fact]
    public async Task Run_Retries_AreBoundedAndHonest()
    {
        var l = new FakeLauncher { FailuresBeforeSuccess = 99 };
        SignInRunSnapshot s = await Svc(l).RunAsync(List(true, new SignInAppEntry("A", SignInAppKind.Exe, Exe())), default);
        Assert.Equal(SignInAppOutcome.Failed, s.Results[0].Outcome);
        Assert.Equal(SignInAppListService.MaxAttempts, s.Results[0].Attempts);
        Assert.Equal(SignInRunState.Completed, s.State);
    }

    [Fact]
    public async Task Run_RecoversAfterOneFailure()
    {
        var l = new FakeLauncher { FailuresBeforeSuccess = 1 };
        SignInRunSnapshot s = await Svc(l).RunAsync(List(true, new SignInAppEntry("A", SignInAppKind.Exe, Exe())), default);
        Assert.Equal(SignInAppOutcome.Launched, s.Results[0].Outcome);
        Assert.Equal(2, s.Results[0].Attempts);
    }

    [Fact]
    public async Task Run_StartedButNotObserved_IsReportedWithoutRelaunch()
    {
        var l = new FakeLauncher { AppearsAfterLaunch = false };
        SignInRunSnapshot s = await Svc(l).RunAsync(List(true, new SignInAppEntry("A", SignInAppKind.Exe, Exe())), default);
        Assert.Single(l.Launched);
        Assert.Equal("process-not-observed", s.Results[0].Reason);
    }

    [Fact]
    public async Task Run_EntryReplacedByMissingFile_IsSkippedInvalid()
    {
        var l = new FakeLauncher();
        string p = Exe("gone.exe");
        SignInAppList list = List(true, new SignInAppEntry("A", SignInAppKind.Exe, p));
        File.Delete(p);
        SignInRunSnapshot s = await Svc(l).RunAsync(list, default);
        Assert.Empty(l.Launched);
        Assert.Equal(SignInAppOutcome.SkippedInvalid, s.Results[0].Outcome);
    }

    [Fact]
    public async Task Run_ConsoleMode_UsesConsoleLeadDelay()
    {
        var l = new FakeLauncher();
        int slices = 0;
        var svc = Svc(l, console: () => true, delay: (t, ct) => { slices++; return Task.CompletedTask; });
        var list = new SignInAppList(true, new SignInAppListSettings(0, 10, 2, 3, 1), [new SignInAppEntry("A", SignInAppKind.Appx, Aumid: GoodAumid)]);
        await svc.RunAsync(list, default);
        Assert.Equal(3, slices); // ConsoleReadyDelaySeconds (3), not DesktopDelaySeconds (10); appx has no probe
    }
}
