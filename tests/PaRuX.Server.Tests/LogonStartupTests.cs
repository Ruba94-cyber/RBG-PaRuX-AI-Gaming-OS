using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure tests of the sign-in start decision, its arguments, the timing log and the reversible registration. Synthetic data only; nothing touches Windows.</summary>
public sealed class LogonStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-logon-" + Guid.NewGuid().ToString("N"));
    public LogonStartupTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static readonly PlayniteLauncherResolution NoAsk = new(PlayniteLauncherAction.None, "none");
    private static LogonStartupSettings S(bool always = false, bool tv = false, string? fallback = "normal", string? gpu = null, int helpers = 0,
        bool power = false, bool bar = false, bool list = false) => new(always, tv, fallback, gpu, helpers, power, bar, list);
    private static readonly LogonStartupRequest Logon = new(true, false, false);

    [Fact]
    public void Arguments_are_read_only_for_a_sign_in_start()
    {
        Assert.False(LogonStartupArguments.Parse(new[] { "PaRuX.exe" }).Logon);
        Assert.False(LogonStartupArguments.Parse(new[] { "PaRuX.exe", "--logon-force" }).Force);   // alone: ignored
        var r = LogonStartupArguments.Parse(new[] { "PaRuX.exe", "--LOGON", "--logon-force" });
        Assert.True(r.Logon); Assert.True(r.Force);
        var n = LogonStartupArguments.Parse(new[] { "PaRuX.exe", "--logon", "--logon-force", "--no-start" });
        Assert.True(n.NoStart); Assert.False(n.Force);   // --no-start wins over force
        Assert.Same(LogonStartupRequest.None, LogonStartupArguments.Parse(null));
    }

    [Fact]
    public void Nothing_turned_on_means_a_quiet_minimised_desktop_start_at_sign_in()
    {
        LogonPlan p = LogonStartupPolicy.Decide(Logon, NoAsk, S(), new(false, false, null, false));
        Assert.Equal(LogonMode.Desktop, p.Mode);
        Assert.Equal(LogonWindow.Minimized, p.Window);
        Assert.Contains(p.Steps, s => s.Code == "minimize");
        Assert.DoesNotContain(p.Steps, s => s.Code == "playnite");
    }

    [Fact]
    public void A_manual_start_without_the_fallback_keeps_the_normal_window()
    {
        LogonPlan p = LogonStartupPolicy.Decide(LogonStartupRequest.None, NoAsk, S(), new(false, false, null, false));
        Assert.Equal(LogonWindow.Normal, p.Window);
    }

    [Fact]
    public void Always_console_does_not_need_a_tv_or_a_monitor()
    {
        LogonPlan p = LogonStartupPolicy.Decide(Logon, NoAsk, S(always: true, helpers: 2, power: true, bar: true, list: true), new(false, false, null, false));
        Assert.Equal(LogonMode.Console, p.Mode);
        Assert.Equal(new[] { "support-apps", "playnite", "power-profile", "game-bar", "sign-in-list", "timing-log" }, p.Steps.Select(s => s.Code).ToArray());
    }

    [Theory]
    [InlineData(true, true, LogonMode.Console)]
    [InlineData(true, false, LogonMode.Desktop)]    // chosen TV not connected
    [InlineData(false, false, LogonMode.Desktop)]   // no TV chosen at all
    public void The_tv_preference_opens_console_only_when_a_chosen_tv_is_connected(bool selected, bool present, LogonMode expected)
    {
        LogonPlan p = LogonStartupPolicy.Decide(Logon, NoAsk, S(tv: true), new(selected, present, null, false));
        Assert.Equal(expected, p.Mode);
    }

    [Theory]
    [InlineData(true, LogonMode.Console)]
    [InlineData(false, LogonMode.Desktop)]
    [InlineData(null, LogonMode.Desktop)]   // never probed: the rule is not satisfied
    public void The_optional_graphics_card_rule_gates_console(bool? gpuPresent, LogonMode expected)
    {
        LogonPlan p = LogonStartupPolicy.Decide(Logon, NoAsk, S(tv: true, gpu: "Example GPU*"), new(true, true, gpuPresent, false));
        Assert.Equal(expected, p.Mode);
        if (expected == LogonMode.Desktop) Assert.Contains("graphics card", p.Reason.En);
    }

    [Fact]
    public void Explicit_requests_win_and_a_running_game_beats_everything()
    {
        var settingsAsked = new PlayniteLauncherResolution(PlayniteLauncherAction.Settings, "Ctrl held");
        Assert.Equal(LogonMode.Settings, LogonStartupPolicy.Decide(Logon, settingsAsked, S(always: true), new(false, false, null, false)).Mode);
        var streaming = new PlayniteLauncherResolution(PlayniteLauncherAction.Streaming, "Shift held");
        Assert.Equal(LogonMode.Streaming, LogonStartupPolicy.Decide(Logon, streaming, S(), new(false, false, null, false)).Mode);
        var consoleAsked = new PlayniteLauncherResolution(PlayniteLauncherAction.Console, "argument");
        Assert.Equal(LogonMode.Console, LogonStartupPolicy.Decide(Logon, consoleAsked, S(gpu: "x"), new(false, false, false, false)).Mode);
        Assert.Equal(LogonMode.Desktop, LogonStartupPolicy.Decide(Logon, consoleAsked, S(always: true), new(false, false, null, true)).Mode);   // game running
    }

    [Fact]
    public void Force_skips_the_tv_and_gpu_checks_and_no_start_blocks_console()
    {
        var force = new LogonStartupRequest(true, true, false);
        Assert.Equal(LogonMode.Console, LogonStartupPolicy.Decide(force, NoAsk, S(tv: true, gpu: "x"), new(true, false, false, false)).Mode);
        var noStart = new LogonStartupRequest(true, false, true);
        Assert.Equal(LogonMode.Desktop, LogonStartupPolicy.Decide(noStart, NoAsk, S(always: true), new(false, false, null, false)).Mode);
    }

    [Fact]
    public void Every_step_and_reason_exists_in_english_and_italian()
    {
        LogonPlan p = LogonStartupPolicy.Decide(Logon, NoAsk, S(always: true, helpers: 1, power: true, bar: true, list: true), new(false, false, null, false));
        Assert.All(p.Steps, s => { Assert.False(string.IsNullOrWhiteSpace(s.Text.En)); Assert.False(string.IsNullOrWhiteSpace(s.Text.It)); Assert.NotEqual(s.Text.En, s.Text.It); });
        Assert.Contains("Mode: Console", p.Describe(false));
        Assert.Contains("Modalità: Console", p.Describe(true));
    }

    [Theory]
    [InlineData("RTX 5080", "NVIDIA GeForce RTX 5080 Laptop GPU", true)]
    [InlineData("geforce*5080", "NVIDIA GeForce RTX 5080", true)]
    [InlineData("5080*geforce", "NVIDIA GeForce RTX 5080", false)]   // order matters with wildcards
    [InlineData("", "anything", true)]                                  // no rule
    [InlineData("Radeon", "NVIDIA GeForce RTX 5080", false)]
    public void Gpu_rule_matches_names_case_insensitively(string pattern, string adapter, bool expected) =>
        Assert.Equal(expected, LogonGpuRule.Matches(pattern, new[] { "Basic Display", adapter }));

    [Fact]
    public void Gpu_rule_with_no_adapter_is_not_satisfied() => Assert.False(LogonGpuRule.Matches("RTX", Array.Empty<string>()));

    [Fact]
    public void Timing_lines_carry_the_measured_elapsed_time_and_the_file_stays_bounded()
    {
        var lines = new List<string>();
        long ms = 0;
        var rec = new StartupTimingRecorder(() => ms, () => new DateTime(2026, 1, 2, 3, 4, 5, 6), lines.Add);
        rec.Mark("start");
        ms = 1319;
        rec.Mark("TV detection complete: True");
        Assert.Equal("2026-01-02 03:04:05.006 +0ms start", lines[0]);
        Assert.Equal("2026-01-02 03:04:05.006 +1319ms TV detection complete: True", lines[1]);

        string path = Path.Combine(_root, "logs", "StartupTiming.log");
        for (int i = 0; i < 6000; i++) StartupTimingFile.Append(path, "2026-01-02 03:04:05.006 +" + i + "ms " + new string('x', 60));
        Assert.True(new FileInfo(path).Length < StartupTimingFile.MaxBytes + 2048);
        Assert.Contains("+5999ms", File.ReadLines(path).Last());
    }

    // ---- reversible registration (fake Run key) ----

    private LogonRegistration Reg(FakeRunKeyStore store) => new(store, Path.Combine(_root, "journal.json"));
    private const string Exe = @"C:\Apps\PaRuX\PaRuX.exe";

    [Fact]
    public void Enable_then_restore_puts_back_exactly_what_was_there()
    {
        var store = new FakeRunKeyStore();
        store.Set(LogonRegistration.ValueName, LogonRegistration.CommandFor(Exe, false));
        LogonRegistration reg = Reg(store);
        Assert.Equal(LogonRegistrationState.RegisteredPlain, reg.Status(Exe));
        Assert.Null(reg.Enable(Exe));
        Assert.Equal(LogonRegistrationState.RegisteredForLogon, reg.Status(Exe));
        Assert.Equal("\"" + Exe + "\" --logon", store.Get(LogonRegistration.ValueName));
        Assert.Null(reg.Restore());
        Assert.Equal("\"" + Exe + "\"", store.Get(LogonRegistration.ValueName));
        Assert.False(File.Exists(Path.Combine(_root, "journal.json")));
    }

    [Fact]
    public void Restore_removes_the_value_when_there_was_none()
    {
        var store = new FakeRunKeyStore();
        LogonRegistration reg = Reg(store);
        Assert.Null(reg.Enable(Exe));
        Assert.Null(reg.Restore());
        Assert.Null(store.Get(LogonRegistration.ValueName));
        Assert.Equal(LogonRegistrationState.NotRegistered, reg.Status(Exe));
    }

    [Fact]
    public void A_foreign_command_is_never_replaced_without_explicit_consent_and_is_restored_after()
    {
        var store = new FakeRunKeyStore();
        store.Set(LogonRegistration.ValueName, "\"D:\\Other\\tool.exe\" /x");
        LogonRegistration reg = Reg(store);
        Assert.Equal(LogonRegistrationState.Foreign, reg.Status(Exe));
        Assert.NotNull(reg.Enable(Exe));
        Assert.Equal("\"D:\\Other\\tool.exe\" /x", store.Get(LogonRegistration.ValueName));
        Assert.Null(reg.Enable(Exe, replaceForeign: true));
        Assert.Null(reg.Restore());
        Assert.Equal("\"D:\\Other\\tool.exe\" /x", store.Get(LogonRegistration.ValueName));
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("")]
    [InlineData("C:\\a\"b\\x.exe")]
    public void Enable_refuses_a_path_that_is_not_a_safe_absolute_path(string exe)
    {
        var store = new FakeRunKeyStore();
        Assert.NotNull(Reg(store).Enable(exe));
        Assert.Null(store.Get(LogonRegistration.ValueName));
    }

    [Fact]
    public void Enable_twice_keeps_the_original_journal()
    {
        var store = new FakeRunKeyStore();
        store.Set(LogonRegistration.ValueName, "\"" + Exe + "\"");
        LogonRegistration reg = Reg(store);
        Assert.Null(reg.Enable(Exe));
        Assert.Null(reg.Enable(Exe));
        Assert.Null(reg.Restore());
        Assert.Equal("\"" + Exe + "\"", store.Get(LogonRegistration.ValueName));
    }

    [Fact]
    public void The_start_dry_run_has_no_process_launch_or_registry_dependency_in_the_pure_policy()
    {
        // Contract: the decision lives in Contracts and only reads its inputs; the executor is the app shell.
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "PaRuX.Contracts", "LogonStartup.cs"));
        Assert.DoesNotContain("Process.Start", src);
        Assert.DoesNotContain("Registry.", src);
    }

    private static string FindRepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "src", "PaRuX.Contracts"))) return dir;
        throw new DirectoryNotFoundException();
    }
}

public sealed class PadFreezeExclusionTests
{
    [Fact]
    public void Names_are_trimmed_deduplicated_and_bounded()
    {
        var list = PadFreezeExclusion.Normalize(new[] { " Game A ", "game a", "", "B\u0007", new string('x', 200) });
        Assert.Equal(3, list.Count);
        Assert.Equal("Game A", list[0]);
        Assert.Equal("B", list[1]);
        Assert.Equal(PadFreezeExclusion.MaxLength, list[2].Length);
        Assert.Equal(PadFreezeExclusion.MaxEntries, PadFreezeExclusion.Normalize(Enumerable.Range(0, 500).Select(i => "g" + i)).Count);
    }

    [Theory]
    [InlineData("Some Game", null, true)]
    [InlineData("other", "somegame", false)]
    [InlineData("other", "some game.EXE", true)]
    [InlineData(null, null, false)]
    public void Matches_the_game_name_or_the_program_name_ignoring_case_and_exe(string? game, string? process, bool expected) =>
        Assert.Equal(expected, PadFreezeExclusion.IsExcluded(new[] { "SOME GAME" }, game, process));

    [Fact]
    public void An_empty_list_excludes_nothing() => Assert.False(PadFreezeExclusion.IsExcluded(Array.Empty<string>(), "Any", "any.exe"));
}
