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
    public void Sign_in_desktop_start_follows_the_normal_window_choice_and_comes_to_the_front()
    {
        // Regression: a --logon start used to minimise even when the owner chose "normal".
        LogonPlan p = LogonStartupPolicy.Decide(Logon, NoAsk, S(fallback: "normal"), new(false, false, null, false));
        Assert.Equal(LogonMode.Desktop, p.Mode);
        Assert.Equal(LogonWindow.Normal, p.Window);
        Assert.Contains(p.Steps, s => s.Code == "show-window");
        Assert.DoesNotContain(p.Steps, s => s.Code == "minimize");
        Assert.DoesNotContain(p.Steps, s => s.Code == "playnite");
    }

    [Theory]
    [InlineData("minimized", LogonWindow.Minimized)]
    [InlineData("normal", LogonWindow.Normal)]
    [InlineData(null, LogonWindow.Normal)]
    [InlineData("garbage", LogonWindow.Normal)]
    public void Sign_in_desktop_window_is_minimised_only_when_chosen(string? fallback, LogonWindow expected)
    {
        Assert.Equal(expected, LogonStartupPolicy.Decide(Logon, NoAsk, S(fallback: fallback), new(false, false, null, false)).Window);
        // The chosen TV is absent: the same rule applies (the real owner case: "Desktop (The chosen TV is not connected.)").
        Assert.Equal(expected, LogonStartupPolicy.Decide(Logon, NoAsk, S(tv: true, fallback: fallback), new(true, false, null, false)).Window);
    }

    [Fact]
    public void Minimised_choice_keeps_the_minimise_step()
    {
        LogonPlan p = LogonStartupPolicy.Decide(Logon, NoAsk, S(fallback: "minimized"), new(false, false, null, false));
        Assert.Contains(p.Steps, s => s.Code == "minimize");
        Assert.DoesNotContain(p.Steps, s => s.Code == "show-window");
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

    // ---- scheduled task first, Run value as the fallback (fake Task Scheduler + fake Run key) ----

    private const string User = @"PC\owner";
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private LogonRegistration TaskReg(FakeRunKeyStore run, FakeLogonTaskStore tasks) => new(run, tasks, Path.Combine(_root, "journal.json"), User, Sid);

    [Fact]
    public void Enable_prefers_the_current_user_task_and_removes_paruxs_own_run_value()
    {
        var run = new FakeRunKeyStore();
        run.Set(LogonRegistration.ValueName, LogonRegistration.CommandFor(Exe, true));   // a Run value Windows may never execute
        var tasks = new FakeLogonTaskStore();
        LogonRegistration reg = TaskReg(run, tasks);
        LogonEnableResult r = reg.EnableDetailed(Exe);
        Assert.True(r.Ok);
        Assert.Equal(LogonMechanism.ScheduledTask, r.Mechanism);
        Assert.Null(run.Get(LogonRegistration.ValueName));   // one mechanism only: no second copy at sign-in
        LogonTaskInfo t = tasks.Get(LogonRegistration.TaskName)!;
        Assert.Equal(Exe, t.Command);
        Assert.Equal("--logon", t.Arguments);
        Assert.Equal(LogonMechanism.ScheduledTask, reg.Describe(Exe).Mechanism);
        Assert.Equal(LogonRegistrationState.RegisteredForLogon, reg.Status(Exe));

        // Turning off removes the task and does NOT bring the old --logon Run value back (it was PaRuX's own sign-in entry).
        Assert.Null(reg.Restore(Exe));
        Assert.Null(tasks.Get(LogonRegistration.TaskName));
        Assert.Null(run.Get(LogonRegistration.ValueName));
        Assert.Equal(LogonRegistrationState.NotRegistered, reg.Status(Exe));
    }

    [Fact]
    public void The_task_definition_is_for_this_user_only_least_privilege_normal_priority_and_escaped()
    {
        string exe = @"C:\Program Files\Pa&RuX\PaRuX.exe";
        string xml = LogonRegistration.BuildTaskXml(exe, User, Sid);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        string V(string local) => doc.Descendants().First(e => e.Name.LocalName == local).Value;
        Assert.Equal(User, doc.Descendants().First(e => e.Name.LocalName == "LogonTrigger").Elements().First(e => e.Name.LocalName == "UserId").Value);
        Assert.Equal(Sid, doc.Descendants().First(e => e.Name.LocalName == "Principal").Elements().First(e => e.Name.LocalName == "UserId").Value);
        Assert.Equal("InteractiveToken", V("LogonType"));
        Assert.Equal("LeastPrivilege", V("RunLevel"));
        Assert.Equal("4", V("Priority"));
        Assert.Equal("PT0S", V("ExecutionTimeLimit"));
        Assert.Equal("false", V("DisallowStartIfOnBatteries"));
        Assert.Equal(exe, V("Command"));
        Assert.Equal("--logon", V("Arguments"));
        Assert.Equal(@"C:\Program Files\Pa&RuX", V("WorkingDirectory"));
        Assert.DoesNotContain("HighestAvailable", xml);
        Assert.DoesNotContain("GroupId", xml);   // never "any user"
    }

    [Fact]
    public void When_task_scheduler_refuses_the_run_value_is_the_fallback_and_admin_need_is_reported()
    {
        var run = new FakeRunKeyStore();
        var tasks = new FakeLogonTaskStore { FailRegister = LogonTaskCallResult.Failed("UnauthorizedAccessException 0x80070005", accessDenied: true) };
        LogonRegistration reg = TaskReg(run, tasks);
        LogonEnableResult r = reg.EnableDetailed(Exe);
        Assert.True(r.Ok);
        Assert.Equal(LogonMechanism.RunKey, r.Mechanism);
        Assert.True(r.NeedsAdministrator);
        Assert.NotNull(r.Note);
        Assert.Equal("\"" + Exe + "\" --logon", run.Get(LogonRegistration.ValueName));
        Assert.Equal(LogonMechanism.RunKey, reg.Describe(Exe).Mechanism);

        // Turning it on again once the task works moves to the task and drops the Run value.
        tasks.FailRegister = null;
        Assert.Equal(LogonMechanism.ScheduledTask, reg.EnableDetailed(Exe).Mechanism);
        Assert.Null(run.Get(LogonRegistration.ValueName));
        Assert.Null(reg.Restore(Exe));
        Assert.Null(run.Get(LogonRegistration.ValueName));
        Assert.Null(tasks.Get(LogonRegistration.TaskName));
    }

    [Fact]
    public void A_plain_parux_run_value_and_a_foreign_task_are_put_back_exactly()
    {
        var run = new FakeRunKeyStore();
        run.Set(LogonRegistration.ValueName, LogonRegistration.CommandFor(Exe, false));
        var tasks = new FakeLogonTaskStore();
        string foreignXml = LogonRegistration.BuildTaskXml(@"D:\Other\tool.exe", User, Sid).Replace("--logon", "/quiet");
        tasks.Register(LogonRegistration.TaskName, foreignXml);
        LogonRegistration reg = TaskReg(run, tasks);

        Assert.True(reg.Describe(Exe).ForeignTask);
        LogonEnableResult refused = reg.EnableDetailed(Exe);
        Assert.False(refused.Ok);
        Assert.Equal(@"D:\Other\tool.exe", tasks.Get(LogonRegistration.TaskName)!.Command);   // untouched without consent
        Assert.False(File.Exists(Path.Combine(_root, "journal.json")));

        Assert.True(reg.EnableDetailed(Exe, replaceForeign: true).Ok);
        Assert.Equal(Exe, tasks.Get(LogonRegistration.TaskName)!.Command);
        Assert.Null(reg.Restore(Exe));
        Assert.Equal(foreignXml, tasks.Get(LogonRegistration.TaskName)!.Xml);
        Assert.Equal("\"" + Exe + "\"", run.Get(LogonRegistration.ValueName));
    }

    [Fact]
    public void A_foreign_run_value_does_not_block_the_task_and_is_left_alone()
    {
        var run = new FakeRunKeyStore();
        run.Set(LogonRegistration.ValueName, "\"D:\\Other\\tool.exe\" /x");
        var tasks = new FakeLogonTaskStore();
        LogonRegistration reg = TaskReg(run, tasks);
        Assert.Equal(LogonMechanism.ScheduledTask, reg.EnableDetailed(Exe).Mechanism);
        Assert.Equal("\"D:\\Other\\tool.exe\" /x", run.Get(LogonRegistration.ValueName));
        Assert.Null(reg.Restore(Exe));
        Assert.Equal("\"D:\\Other\\tool.exe\" /x", run.Get(LogonRegistration.ValueName));
    }

    [Fact]
    public void An_existing_own_logon_task_is_adopted_and_turning_off_removes_it_without_a_journal()
    {
        // The local remedy on the owner's PC: "PaRuX at sign-in" created by hand, pointing at the installed PaRuX with --logon.
        var run = new FakeRunKeyStore();
        var tasks = new FakeLogonTaskStore();
        tasks.Register(LogonRegistration.TaskName, LogonRegistration.BuildTaskXml(Exe, User, Sid));
        LogonRegistration reg = TaskReg(run, tasks);
        LogonRegistrationStatus s = reg.Describe(Exe);
        Assert.Equal(LogonMechanism.ScheduledTask, s.Mechanism);
        Assert.False(s.ForeignTask);
        Assert.Equal(1, tasks.RegisterCalls);
        Assert.True(reg.EnableDetailed(Exe).Ok);
        Assert.Equal(1, tasks.RegisterCalls);   // nothing re-registered
        Assert.Null(reg.Restore(Exe));
        Assert.Null(tasks.Get(LogonRegistration.TaskName));
    }

    [Fact]
    public void A_disabled_own_task_is_not_reported_as_registered()
    {
        var tasks = new FakeLogonTaskStore();
        tasks.Tasks[LogonRegistration.TaskName] = new LogonTaskInfo(Exe, "--logon", false, null, null, "<Task/>");
        Assert.Equal(LogonMechanism.None, TaskReg(new FakeRunKeyStore(), tasks).Describe(Exe).Mechanism);
    }

    [Theory]
    [InlineData(false, 0, null, 60, LogonStartHealth.NotRegistered)]
    [InlineData(true, null, null, 60, LogonStartHealth.Unknown)]          // sign-in time unknown
    [InlineData(true, 0, 20, 60, LogonStartHealth.Started)]                // started 20 s after sign-in
    [InlineData(true, 0, -3, 60, LogonStartHealth.Started)]                // within the clock skew
    [InlineData(true, 0, -3600, 60, LogonStartHealth.Unknown)]             // previous session only, still within the grace
    [InlineData(true, 0, -3600, 600, LogonStartHealth.NotStartedAtLastSignIn)]
    [InlineData(true, 0, null, 600, LogonStartHealth.NotStartedAtLastSignIn)]
    public void The_last_sign_in_check_warns_only_when_registered_and_no_logon_start_happened_since(bool registered, int? signInAt, int? startAt, int nowAt, LogonStartHealth expected)
    {
        var t0 = new DateTimeOffset(2026, 11, 25, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset? At(int? s) => s is null ? null : t0.AddSeconds(s.Value);
        Assert.Equal(expected, LogonStartHealthPolicy.Evaluate(registered, At(signInAt), At(startAt), t0.AddSeconds(nowAt)));
    }

    [Fact]
    public void The_logon_start_marker_round_trips_and_a_damaged_one_reads_as_none()
    {
        string path = Path.Combine(_root, "logs", "last-logon-start.json");
        Assert.Null(LogonStartMarker.Read(path));
        var when = new DateTimeOffset(2026, 11, 25, 21, 45, 9, TimeSpan.Zero);
        LogonStartMarker.Write(path, when, null);
        Assert.Equal(when, LogonStartMarker.Read(path));
        File.WriteAllText(path, "{not json");
        Assert.Null(LogonStartMarker.Read(path));
    }

    [Fact]
    public void App_wiring_uses_the_task_store_records_logon_starts_and_shows_the_window_when_chosen()
    {
        string app = Path.Combine(FindRepoRoot(), "src", "PaRuX.App");
        string reg = File.ReadAllText(Path.Combine(app, "Services", "RegistryRunKeyStore.cs"));
        Assert.Contains("new ScheduledTaskLogonStore()", reg);
        string store = File.ReadAllText(Path.Combine(app, "Services", "ScheduledTaskLogonStore.cs"));
        Assert.Contains("Schedule.Service", store);
        Assert.Contains("LogonInteractiveToken = 3", store);
        Assert.DoesNotContain("HighestAvailable", store);
        string logon = File.ReadAllText(Path.Combine(app, "MainWindow.LogonStartup.cs"));
        Assert.Contains("RecordLogonStart();", logon);
        Assert.Contains("App.ShowMainWindow();", logon);
        string appXaml = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));
        Assert.Contains("if (StartupLogonRequest.Logon) NormalizeLogonStartPriority();", appXaml);
        // The orchestrator runs for every --logon process, whatever started it (Task Scheduler or Run): no activation-kind or
        // working-directory gate between the parsed request and the start sequence.
        string seq = File.ReadAllText(Path.Combine(app, "MainWindow.SignInApps.cs"));
        Assert.Contains("if (!hasPlayRequest && App.StartupLogonRequest.Logon)", seq);
        Assert.Contains("LogonStartupArguments.Parse(Environment.GetCommandLineArgs())", appXaml);
        string settings = File.ReadAllText(Path.Combine(app, "Views", "SettingsView.LogonStartup.cs"));
        Assert.Contains("LogonStartHealthPolicy.Evaluate", settings);
        Assert.Contains("EnableDetailed(exe)", settings);
        Assert.Contains("Restore(exe)", settings);
        Assert.Contains("_Window_Combo", settings);
    }

    [Fact]
    public void Uninstall_cleanup_argument_is_recognised_case_insensitively_and_never_triggers_logon_start()
    {
        Assert.False(LogonStartupArguments.IsUninstallCleanupRequest(null));
        Assert.False(LogonStartupArguments.IsUninstallCleanupRequest(new[] { "PaRuX.exe" }));
        Assert.False(LogonStartupArguments.IsUninstallCleanupRequest(new[] { "PaRuX.exe", "--logon" }));
        Assert.True(LogonStartupArguments.IsUninstallCleanupRequest(new[] { "PaRuX.exe", "--uninstall-logon-cleanup" }));
        Assert.True(LogonStartupArguments.IsUninstallCleanupRequest(new[] { "PaRuX.exe", "--UNINSTALL-LOGON-CLEANUP" }));
        Assert.False(LogonStartupArguments.Parse(new[] { "PaRuX.exe", "--uninstall-logon-cleanup" }).Logon);
    }

    [Fact]
    public void Uninstall_cleanup_removes_own_task_and_run_value_with_or_without_logon_flag()
    {
        var run = new FakeRunKeyStore();
        var tasks = new FakeLogonTaskStore();
        run.Set(LogonRegistration.ValueName, LogonRegistration.CommandFor(Exe, true));
        tasks.Register(LogonRegistration.TaskName, LogonRegistration.BuildTaskXml(Exe, User, Sid));

        LogonRegistration.RemoveOwnOnUninstall(run, tasks, Exe);
        Assert.Null(run.Get(LogonRegistration.ValueName));
        Assert.Null(tasks.Get(LogonRegistration.TaskName));

        // Plain Run value (without --logon) and plain task pointing to Exe are also removed.
        run.Set(LogonRegistration.ValueName, LogonRegistration.CommandFor(Exe, false));
        tasks.Tasks[LogonRegistration.TaskName] = new LogonTaskInfo("\"" + Exe + "\"", null, true, null, null, "<Task/>");

        LogonRegistration.RemoveOwnOnUninstall(run, tasks, Exe);
        Assert.Null(run.Get(LogonRegistration.ValueName));
        Assert.Null(tasks.Get(LogonRegistration.TaskName));
    }

    [Fact]
    public void Uninstall_cleanup_never_touches_foreign_entries_or_entries_for_another_parux_copy()
    {
        const string otherPaRuX = @"D:\Portable\PaRuX\PaRuX.exe";
        var run = new FakeRunKeyStore();
        var tasks = new FakeLogonTaskStore();

        // Entries pointing to a different copy of PaRuX must stay untouched.
        run.Set(LogonRegistration.ValueName, LogonRegistration.CommandFor(otherPaRuX, true));
        tasks.Register(LogonRegistration.TaskName, LogonRegistration.BuildTaskXml(otherPaRuX, User, Sid));

        LogonRegistration.RemoveOwnOnUninstall(run, tasks, Exe);
        Assert.Equal(LogonRegistration.CommandFor(otherPaRuX, true), run.Get(LogonRegistration.ValueName));
        Assert.Equal(otherPaRuX, tasks.Get(LogonRegistration.TaskName)!.Command);

        // Completely foreign entries must also stay untouched.
        run.Set(LogonRegistration.ValueName, "\"D:\\Other\\tool.exe\" /x");
        tasks.Tasks[LogonRegistration.TaskName] = new LogonTaskInfo(@"D:\Other\tool.exe", "--logon", true, null, null, "<Task/>");

        LogonRegistration.RemoveOwnOnUninstall(run, tasks, Exe);
        Assert.Equal("\"D:\\Other\\tool.exe\" /x", run.Get(LogonRegistration.ValueName));
        Assert.Equal(@"D:\Other\tool.exe", tasks.Get(LogonRegistration.TaskName)!.Command);
    }

    private sealed class ThrowingRunKeyStore : IRunKeyStore
    {
        public string? Get(string name) => throw new UnauthorizedAccessException("denied");
        public void Set(string name, string value) => throw new UnauthorizedAccessException("denied");
        public void Delete(string name) => throw new UnauthorizedAccessException("denied");
    }

    private sealed class FailingDeleteTaskStore : ILogonTaskStore
    {
        public LogonTaskInfo? Task { get; set; }
        public LogonTaskInfo? Get(string name) => Task;
        public LogonTaskCallResult Register(string name, string xml) => LogonTaskCallResult.Failed("denied", accessDenied: true);
        public LogonTaskCallResult Delete(string name) => LogonTaskCallResult.Failed("denied", accessDenied: true);
    }

    [Fact]
    public void Uninstall_cleanup_ignores_missing_entries_invalid_paths_and_permission_errors()
    {
        var run = new FakeRunKeyStore();
        var tasks = new FakeLogonTaskStore();

        // Missing entries: no-op, no exception.
        LogonRegistration.RemoveOwnOnUninstall(run, tasks, Exe);
        LogonRegistration.RemoveOwnOnUninstall(null, null, Exe);

        // Invalid path: never touches even a matching-looking store.
        run.Set(LogonRegistration.ValueName, LogonRegistration.CommandFor(Exe, true));
        LogonRegistration.RemoveOwnOnUninstall(run, tasks, "relative.exe");
        LogonRegistration.RemoveOwnOnUninstall(run, tasks, "");
        Assert.Equal(LogonRegistration.CommandFor(Exe, true), run.Get(LogonRegistration.ValueName));

        // Permission / access-denied errors: swallowed cleanly so uninstall never fails.
        var failingTasks = new FailingDeleteTaskStore { Task = new LogonTaskInfo(Exe, "--logon", true, null, null, "<Task/>") };
        LogonRegistration.RemoveOwnOnUninstall(new ThrowingRunKeyStore(), failingTasks, Exe);
    }

    [Fact]
    public void Msi_uninstall_runs_headless_logon_cleanup_as_user_and_elevated_only_on_full_removal_even_with_NoRedist()
    {
        string repoRoot = FindRepoRoot();
        string wxs = File.ReadAllText(Path.Combine(repoRoot, "installer", "PaRuX.Setup", "Package.wxs"));
        string outsideRedist = System.Text.RegularExpressions.Regex.Replace(
            wxs, @"<\?if \$\(IncludeRedist\) = 1\?>.*?<\?endif\?>", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.Contains("Id=\"PaRuXLogonCleanupUser\"", outsideRedist);
        Assert.Contains("Id=\"PaRuXLogonCleanupElevated\"", outsideRedist);

        System.Text.RegularExpressions.Match userCa = System.Text.RegularExpressions.Regex.Match(
            outsideRedist, @"<CustomAction\s+Id=""PaRuXLogonCleanupUser"".*?/>", System.Text.RegularExpressions.RegexOptions.Singleline);
        System.Text.RegularExpressions.Match elevatedCa = System.Text.RegularExpressions.Regex.Match(
            outsideRedist, @"<CustomAction\s+Id=""PaRuXLogonCleanupElevated"".*?/>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(userCa.Success);
        Assert.True(elevatedCa.Success);

        foreach (string ca in new[] { userCa.Value, elevatedCa.Value })
        {
            Assert.Contains("Directory=\"INSTALLFOLDER\"", ca);
            Assert.Contains("Execute=\"deferred\"", ca);
            Assert.Contains("Return=\"ignore\"", ca);
            Assert.Contains("ExeCommand=\"&quot;[INSTALLFOLDER]PaRuX.exe&quot; --uninstall-logon-cleanup\"", ca);
        }
        Assert.Contains("Impersonate=\"yes\"", userCa.Value);
        Assert.Contains("Impersonate=\"no\"", elevatedCa.Value);

        // Scheduled strictly before RemoveFiles and ONLY on full uninstall (never on a major upgrade).
        Assert.Contains("<Custom Action=\"PaRuXLogonCleanupUser\" Before=\"PaRuXLogonCleanupElevated\" Condition=\"REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE\" />", outsideRedist);
        Assert.Contains("<Custom Action=\"PaRuXLogonCleanupElevated\" Before=\"RemoveFiles\" Condition=\"REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE\" />", outsideRedist);

        // Headless entry point runs before WinUI Application.Start and does not log to PaRuXData.
        string program = File.ReadAllText(Path.Combine(repoRoot, "src", "PaRuX.App", "Program.cs"));
        int cleanupIdx = program.IndexOf("IsUninstallCleanupRequest", StringComparison.Ordinal);
        int appStartIdx = program.IndexOf("Application.Start", StringComparison.Ordinal);
        Assert.True(cleanupIdx > 0 && appStartIdx > cleanupIdx, "Uninstall cleanup must execute before WinUI Application.Start.");
        Assert.Contains("LogonRegistration.RemoveOwnOnUninstall", program);
        Assert.Contains("new ScheduledTaskLogonStore(log: false)", program);
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
