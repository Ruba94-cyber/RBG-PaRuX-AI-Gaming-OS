using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PaRuX.Server;
using PaRuX.Server.ChatCode;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Policy-level tests: a scripted launcher stands in for the OS sandbox, no process is started.</summary>
public sealed class ChatCodeRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-chatcode-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _python;

    public ChatCodeRunnerTests()
    {
        Directory.CreateDirectory(_root);
        _python = Path.Combine(_root, "python.exe");
        File.WriteAllText(_python, "fake");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class ScriptedLauncher : IChatCodeProcessLauncher
    {
        public Func<ChatCodeLaunch, CancellationToken, Task<ChatCodeProcessResult>> Behavior =
            (_, _) => Task.FromResult(new ChatCodeProcessResult(0, "hi\n", false, false, false, null, null));
        public List<ChatCodeLaunch> Launches { get; } = new();
        public string? ScriptSeen;
        public Task<ChatCodeProcessResult> RunAsync(ChatCodeLaunch launch, CancellationToken ct)
        {
            Launches.Add(launch);
            ScriptSeen = File.ReadAllText(Path.Combine(launch.WorkDirectory, "main.py"));
            return Behavior(launch, ct);
        }
    }

    private ChatCodeRunner Runner(ScriptedLauncher launcher, bool gaming = false, bool hasPython = true) =>
        new(launcher, () => hasPython ? _python : null, () => gaming, Path.Combine(_root, "runs"));

    [Fact]
    public async Task Completed_run_returns_output_and_removes_the_run_folder()
    {
        var launcher = new ScriptedLauncher();
        ChatCodeResult r = await Runner(launcher).RunAsync(new ChatCodeRequest("print('hi')"), default);
        Assert.Equal(ChatCodeStatus.Completed, r.Status);
        Assert.Equal("hi\n", r.Output);
        Assert.Equal("print('hi')", launcher.ScriptSeen);
        Assert.Equal(new[] { "-I", "-B", "main.py" }, launcher.Launches[0].Arguments);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "runs")));
    }

    [Fact]
    public async Task Missing_runtime_is_blocked_needs_runtime_and_nothing_starts()
    {
        var launcher = new ScriptedLauncher();
        ChatCodeResult r = await Runner(launcher, hasPython: false).RunAsync(new ChatCodeRequest("print(1)"), default);
        Assert.Equal(ChatCodeStatus.BlockedNeedsRuntime, r.Status);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task Gaming_mode_blocks_before_any_folder_or_process()
    {
        var launcher = new ScriptedLauncher();
        ChatCodeResult r = await Runner(launcher, gaming: true).RunAsync(new ChatCodeRequest("print(1)"), default);
        Assert.Equal(ChatCodeStatus.BlockedGaming, r.Status);
        Assert.Empty(launcher.Launches);
        Assert.False(Directory.Exists(Path.Combine(_root, "runs")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_code_is_rejected(string code)
    {
        ChatCodeResult r = await Runner(new ScriptedLauncher()).RunAsync(new ChatCodeRequest(code), default);
        Assert.Equal(ChatCodeStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task Oversized_code_and_nul_are_rejected()
    {
        var runner = Runner(new ScriptedLauncher());
        Assert.Equal(ChatCodeStatus.Rejected, (await runner.RunAsync(new ChatCodeRequest(new string('x', ChatCodeLimits.MaxCodeCharacters + 1)), default)).Status);
        Assert.Equal(ChatCodeStatus.Rejected, (await runner.RunAsync(new ChatCodeRequest("a\0b"), default)).Status);
    }

    [Fact]
    public async Task Limits_are_clamped_so_the_model_cannot_raise_them()
    {
        var launcher = new ScriptedLauncher();
        await Runner(launcher).RunAsync(new ChatCodeRequest("x=1", new ChatCodeLimits { TimeoutSeconds = 9999, MemoryBytes = long.MaxValue, MaxOutputBytes = int.MaxValue }), default);
        ChatCodeLimits l = launcher.Launches[0].Limits;
        Assert.Equal(ChatCodeLimits.MaxTimeoutSeconds, l.TimeoutSeconds);
        Assert.True(l.MemoryBytes <= 1L << 30);
        Assert.True(l.MaxOutputBytes <= 256 * 1024);
    }

    [Fact]
    public async Task Timeout_and_limit_results_are_reported_honestly()
    {
        var launcher = new ScriptedLauncher { Behavior = (_, _) => Task.FromResult(new ChatCodeProcessResult(null, "partial", false, true, false, null, null)) };
        ChatCodeResult t = await Runner(launcher).RunAsync(new ChatCodeRequest("while True: pass"), default);
        Assert.Equal(ChatCodeStatus.TimedOut, t.Status);
        Assert.Contains("process tree was stopped", t.Message);

        launcher.Behavior = (_, _) => Task.FromResult(new ChatCodeProcessResult(null, "x", true, false, true, "the output passed 64 KiB", null));
        ChatCodeResult o = await Runner(launcher).RunAsync(new ChatCodeRequest("print('x'*10**9)"), default);
        Assert.Equal(ChatCodeStatus.OutputLimitExceeded, o.Status);
        Assert.True(o.OutputTruncated);
    }

    [Fact]
    public async Task Sandbox_failure_is_never_run_unsandboxed()
    {
        var launcher = new ScriptedLauncher { Behavior = (_, _) => Task.FromResult(new ChatCodeProcessResult(null, "", false, false, false, null, "AppContainer refused")) };
        ChatCodeResult r = await Runner(launcher).RunAsync(new ChatCodeRequest("print(1)"), default);
        Assert.Equal(ChatCodeStatus.SandboxUnavailable, r.Status);
        Assert.Contains("NOT run", r.Message);
        Assert.Single(launcher.Launches);
    }

    [Fact]
    public async Task Cancellation_returns_cancelled_and_cleans_up()
    {
        using var cts = new CancellationTokenSource();
        var launcher = new ScriptedLauncher { Behavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; } };
        Task<ChatCodeResult> run = Runner(launcher).RunAsync(new ChatCodeRequest("while True: pass"), cts.Token);
        while (launcher.Launches.Count == 0) await Task.Delay(10);
        cts.Cancel();
        Assert.Equal(ChatCodeStatus.Cancelled, (await run).Status);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "runs")));
    }

    [Fact]
    public async Task Artifacts_are_collected_filtered_and_bounded()
    {
        var launcher = new ScriptedLauncher
        {
            Behavior = (l, _) =>
            {
                string o = Path.Combine(l.WorkDirectory, "out");
                File.WriteAllBytes(Path.Combine(o, "plot.png"), new byte[] { 1, 2, 3 });
                File.WriteAllText(Path.Combine(o, "page.html"), "<html><head></head><body><script>fetch('http://x')</script></body></html>");
                File.WriteAllText(Path.Combine(o, "data.csv"), "a,b\n1,2");
                File.WriteAllText(Path.Combine(o, "run.exe"), "MZ");
                File.WriteAllBytes(Path.Combine(o, "big.png"), new byte[3 << 20]);
                Directory.CreateDirectory(Path.Combine(o, "sub"));
                return Task.FromResult(new ChatCodeProcessResult(0, "", false, false, false, null, null));
            }
        };
        ChatCodeResult r = await Runner(launcher).RunAsync(new ChatCodeRequest("x"), default);
        Assert.Equal(new[] { "data.csv", "page.html", "plot.png" }, r.Artifacts.Select(a => a.Name).ToArray());
        Assert.Equal("image", r.Artifacts.Single(a => a.Name == "plot.png").Kind);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), r.Artifacts.Single(a => a.Name == "plot.png").Content);
        Assert.Contains(r.SkippedArtifacts, s => s.StartsWith("run.exe"));
        Assert.Contains(r.SkippedArtifacts, s => s.StartsWith("big.png"));
        Assert.Contains(r.SkippedArtifacts, s => s.StartsWith("sub"));
    }

    [Fact]
    public async Task Html_artifact_gets_a_network_denying_content_security_policy_first()
    {
        var launcher = new ScriptedLauncher
        {
            Behavior = (l, _) =>
            {
                File.WriteAllText(Path.Combine(l.WorkDirectory, "out", "p.html"), "<html><head><title>t</title></head><body>x</body></html>");
                File.WriteAllText(Path.Combine(l.WorkDirectory, "out", "q.html"), "just text");
                return Task.FromResult(new ChatCodeProcessResult(0, "", false, false, false, null, null));
            }
        };
        ChatCodeResult r = await Runner(launcher).RunAsync(new ChatCodeRequest("x"), default);
        string p = r.Artifacts.Single(a => a.Name == "p.html").Content;
        Assert.True(p.IndexOf("Content-Security-Policy", StringComparison.Ordinal) < p.IndexOf("<title>", StringComparison.Ordinal));
        Assert.Contains("connect-src 'none'", p);
        Assert.Contains("default-src 'none'", r.Artifacts.Single(a => a.Name == "q.html").Content);
    }

    [Theory]
    [InlineData("..\\evil.txt")]
    [InlineData("a/b.txt")]
    [InlineData("C:\\x.txt")]
    [InlineData("CON.txt")]
    [InlineData(".hidden")]
    public async Task Attachment_names_cannot_escape_the_run_folder(string name)
    {
        var launcher = new ScriptedLauncher();
        ChatCodeResult r = await Runner(launcher).RunAsync(
            new ChatCodeRequest("x", null, new[] { new ChatCodeInputFile(name, new byte[] { 1 }) }), default);
        Assert.Equal(ChatCodeStatus.Rejected, r.Status);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task Only_user_attached_files_appear_in_the_input_folder()
    {
        bool sawFile = false;
        var launcher = new ScriptedLauncher
        {
            Behavior = (l, _) =>
            {
                sawFile = File.ReadAllText(Path.Combine(l.WorkDirectory, "in", "notes.txt")) == "hello";
                Assert.Equal(new[] { "main.py" }, Directory.GetFiles(l.WorkDirectory).Select(Path.GetFileName).ToArray());
                return Task.FromResult(new ChatCodeProcessResult(0, "", false, false, false, null, null));
            }
        };
        await Runner(launcher).RunAsync(new ChatCodeRequest("x", null, new[] { new ChatCodeInputFile("notes.txt", Encoding.UTF8.GetBytes("hello")) }), default);
        Assert.True(sawFile);
    }

    [Fact]
    public async Task Oversized_attachments_are_rejected()
    {
        ChatCodeResult r = await Runner(new ScriptedLauncher()).RunAsync(
            new ChatCodeRequest("x", new ChatCodeLimits { MaxInputBytes = 1 << 20 }, new[] { new ChatCodeInputFile("a.bin", new byte[2 << 20]) }), default);
        Assert.Equal(ChatCodeStatus.Rejected, r.Status);
    }
}

public sealed class ChatCodeToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-chatcode-tool-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _python;
    private int _runs;
    private bool _gaming;

    public ChatCodeToolTests()
    {
        Directory.CreateDirectory(_root);
        _python = Path.Combine(_root, "python.exe");
        File.WriteAllText(_python, "fake");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class Counting(Func<int> bump) : IChatCodeProcessLauncher
    {
        public Task<ChatCodeProcessResult> RunAsync(ChatCodeLaunch l, CancellationToken ct)
        {
            bump();
            return Task.FromResult(new ChatCodeProcessResult(0, "42\n", false, false, false, null, null));
        }
    }

    private ChatCodeTools Tools(bool installed = true, TimeSpan? wait = null)
    {
        var runtime = new ChatCodeRuntime(Path.Combine(_root, "s.json"), () => installed ? _python : null,
            _ => Task.FromResult(_python), () => _gaming, () => Array.Empty<ChatCodeDetectedPython>());
        var runner = new ChatCodeRunner(new Counting(() => ++_runs), runtime.UsableInterpreter, () => _gaming, Path.Combine(_root, "runs"));
        return new ChatCodeTools(runner, runtime, new AgentLibraryConsentStore(), new ChatCodeRunStore(), () => _gaming)
        { ApprovalWait = wait ?? TimeSpan.FromSeconds(5) };
    }

    private static string Args(string code) => new JsonObject { ["code"] = code }.ToJsonString();

    private static async Task ApproveWhenPending(ChatCodeTools t, bool approve)
    {
        for (int i = 0; i < 200; i++)
        {
            AgentLibraryConsentItem? item = t.Consent.List().FirstOrDefault(x => x.State == "pending");
            if (item != null) { if (approve) t.Consent.Approve(item.Id); else t.Consent.Deny(item.Id); return; }
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Tool_is_advertised_only_when_runtime_installed_and_not_gaming()
    {
        Assert.Single(Tools().GetToolSchemas());
        Assert.Empty(Tools(installed: false).GetToolSchemas());
        _gaming = true;
        Assert.Empty(Tools().GetToolSchemas());
        _gaming = false;
        Assert.Empty(Tools().GetToolSchemas(new[] { "parux_kb_*" }));
    }

    [Fact]
    public async Task Nothing_runs_until_the_user_approves_and_approval_is_single_use()
    {
        ChatCodeTools t = Tools();
        Task<string> call = t.CallAsync("run_python", Args("print(6*7)"), default);
        await Task.Delay(300);
        Assert.Equal(0, _runs);
        Assert.Contains("print(6*7)", Assert.Single(t.Consent.List()).Summary);
        await ApproveWhenPending(t, approve: true);
        string result = await call;
        Assert.Equal(1, _runs);
        Assert.Contains("42", result);
        Assert.Contains("run_id", result);
        Assert.Empty(t.Consent.List());   // consumed, so the same approval cannot be replayed
    }

    [Fact]
    public async Task Denied_code_is_not_run()
    {
        ChatCodeTools t = Tools();
        Task<string> call = t.CallAsync("run_python", Args("print(1)"), default);
        await ApproveWhenPending(t, approve: false);
        string result = await call;
        Assert.StartsWith("Code tool failed:", result);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task Unanswered_approval_times_out_without_running()
    {
        string result = await Tools(wait: TimeSpan.FromMilliseconds(300)).CallAsync("run_python", Args("print(1)"), default);
        Assert.Contains("not answer", result);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task Approval_for_other_code_cannot_be_reused()
    {
        ChatCodeTools t = Tools(wait: TimeSpan.FromMilliseconds(400));
        string? id = t.Consent.Request("run_python", AgentLibraryConsentStore.Bind("run_python", "print('safe')", "20"), "x");
        t.Consent.Approve(id!);
        string result = await t.CallAsync("run_python", Args("import os; os.remove('x')"), default);
        Assert.Equal(0, _runs);
        Assert.StartsWith("Code tool failed:", result);
    }

    [Fact]
    public async Task Missing_runtime_is_reported_as_blocked_not_silently_installed()
    {
        string result = await Tools(installed: false).CallAsync("run_python", Args("print(1)"), default);
        Assert.StartsWith("Code tool failed:", result);
        Assert.Contains("BLOCKED", result);
        Assert.Contains("Settings", result);
    }

    [Fact]
    public async Task Gaming_blocks_the_call()
    {
        _gaming = true;
        string result = await Tools().CallAsync("run_python", Args("print(1)"), default);
        Assert.Contains("Gaming", result);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task Bad_arguments_are_rejected_before_asking_the_user()
    {
        ChatCodeTools t = Tools();
        Assert.Contains("not valid JSON", await t.CallAsync("run_python", "{oops", default));
        Assert.Contains("timeout_seconds", await t.CallAsync("run_python", "{\"code\":\"x\",\"timeout_seconds\":9999}", default));
        Assert.Empty(t.Consent.List());
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_approval_throws_and_runs_nothing()
    {
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tools().CallAsync("run_python", Args("print(1)"), cts.Token));
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task Runtime_install_is_explicit_refused_during_gaming_and_reports_failure()
    {
        int installs = 0;
        var runtime = new ChatCodeRuntime(Path.Combine(_root, "r.json"), () => null,
            _ => { installs++; throw new InvalidOperationException("offline"); }, () => _gaming, () => Array.Empty<ChatCodeDetectedPython>());
        Assert.Equal("not-installed", runtime.GetStatus().State);
        Assert.Equal(0, installs);                         // detecting never installs
        _gaming = true;
        Assert.NotNull(runtime.BeginInstall());
        _gaming = false;
        Assert.Null(runtime.BeginInstall());
        for (int i = 0; i < 100 && runtime.GetStatus().State == "installing"; i++) await Task.Delay(20);
        ChatCodeRuntimeStatus s = runtime.GetStatus();
        Assert.Equal("failed", s.State);
        Assert.Contains("offline", s.Error);
        runtime.SetSkipped(true);
        Assert.Equal("skipped", runtime.GetStatus().State);
        Assert.NotNull(runtime.BeginInstall());
    }
}

/// <summary>
/// Real OS enforcement: the AppContainer + Job Object launcher against Windows' own programs and, when this PC has one,
/// a COPY of a Python install (the original is never touched). Skips silently off Windows or when AppContainer is unavailable.
/// </summary>
public sealed class ChatCodeAppContainerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-cc-it-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _runs;
    private readonly IChatCodeProcessLauncher _launcher = new AppContainerChatCodeLauncher();

    public ChatCodeAppContainerIntegrationTests()
    {
        _runs = Path.Combine(_root, "runs");
        Directory.CreateDirectory(_runs);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private async Task<ChatCodeProcessResult?> Run(string exe, string[] args, ChatCodeLimits? limits = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string work = Path.Combine(_runs, "run-" + Guid.NewGuid().ToString("N")[..8], "work");
        Directory.CreateDirectory(work);
        // Windows' own programs are already readable by AppContainers; never touch the ACL of System32.
        string[] readRoots = exe.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>() : new[] { Path.GetDirectoryName(exe)! };
        var launch = new ChatCodeLaunch(exe, args, work, _runs, readRoots,
            new Dictionary<string, string>(), (limits ?? new ChatCodeLimits { TimeoutSeconds = 10 }).Clamped());
        ChatCodeProcessResult r = await _launcher.RunAsync(launch, ct);
        return r;
    }

    private static bool Unavailable(ChatCodeProcessResult? r)
    {
        if (r == null) return true;   // not Windows
        if (r.LaunchError != null) Assert.Fail("AppContainer sandbox could not be established on this Windows host: " + r.LaunchError);
        return false;
    }

    [Fact]
    public async Task Program_runs_and_its_output_is_captured()
    {
        ChatCodeProcessResult? r = await Run(System32("cmd.exe"), new[] { "/c", "echo sandboxed-ok" });
        if (Unavailable(r)) return;
        Assert.Equal(0, r!.ExitCode);
        Assert.Contains("sandboxed-ok", r.Output);
    }

    [Fact]
    public async Task Loopback_network_is_unreachable_from_the_sandbox()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int accepted = 0;
        _ = Task.Run(async () => { while (true) { try { using var c = await listener.AcceptTcpClientAsync(); Interlocked.Increment(ref accepted); } catch { return; } } });
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        ChatCodeProcessResult? r = await Run(System32("curl.exe"), new[] { "-s", "-m", "4", "-o", "NUL", "-w", "%{http_code}", "http://127.0.0.1:" + port + "/" });
        if (Unavailable(r)) return;
        await Task.Delay(300);
        Assert.Equal(0, accepted);
        Assert.NotEqual(0, r!.ExitCode);
    }

    [Fact]
    public async Task Files_outside_the_run_folder_are_not_readable()
    {
        string secret = Path.Combine(_root, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-VALUE");
        ChatCodeProcessResult? r = await Run(System32("cmd.exe"), new[] { "/c", "type \"" + secret + "\"" });
        if (Unavailable(r)) return;
        Assert.DoesNotContain("TOP-SECRET-VALUE", r!.Output);
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public async Task Infinite_loop_is_stopped_by_the_timeout()
    {
        var sw = Stopwatch.StartNew();
        ChatCodeProcessResult? r = await Run(System32("cmd.exe"), new[] { "/c", "for /l %i in (1,0,2) do @rem" }, new ChatCodeLimits { TimeoutSeconds = 2 });
        if (Unavailable(r)) return;
        Assert.True(r!.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Second_process_cannot_be_started_so_a_fork_bomb_is_bounded()
    {
        ChatCodeProcessResult? r = await Run(System32("cmd.exe"), new[] { "/c", "cmd /c echo CHILD-RAN" });
        if (Unavailable(r)) return;
        Assert.DoesNotContain("CHILD-RAN", r!.Output);
    }

    [Fact]
    public async Task Huge_output_is_cut_off_and_the_program_stopped()
    {
        ChatCodeProcessResult? r = await Run(System32("cmd.exe"),
            new[] { "/c", "for /l %i in (1,1,100000000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" },
            new ChatCodeLimits { TimeoutSeconds = 20, MaxOutputBytes = 8 * 1024 });
        if (Unavailable(r)) return;
        Assert.True(r!.LimitExceeded);
        Assert.True(r.Output.Length <= 8 * 1024);
    }

    [Fact]
    public async Task Cancellation_kills_the_program_promptly()
    {
        using var cts = new CancellationTokenSource(700);
        var sw = Stopwatch.StartNew();
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            ChatCodeProcessResult? r = await Run(System32("cmd.exe"), new[] { "/c", "for /l %i in (1,0,2) do @rem" }, new ChatCodeLimits { TimeoutSeconds = 30 }, cts.Token);
            if (Unavailable(r)) return;
            Assert.Fail("The run should have been cancelled.");
        }
        catch (OperationCanceledException) { Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Real_python_copy_runs_code_but_cannot_use_network_or_read_user_files()
    {
        string? source = FindPythonInstall();
        if (source == null || !OperatingSystem.IsWindows()) return;
        string copy = Path.Combine(_root, "py");
        CopyDirectory(source, copy);
        string python = Path.Combine(copy, "python.exe");

        string secret = Path.Combine(_root, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-VALUE");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int accepted = 0;
        _ = Task.Run(async () => { while (true) { try { using var c = await listener.AcceptTcpClientAsync(); Interlocked.Increment(ref accepted); } catch { return; } } });
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var runner = new ChatCodeRunner(_launcher, () => python, () => false, _runs);
        string code = "import os, socket\n"
            + "print('hello', 6*7)\n"
            + "try:\n    socket.create_connection(('127.0.0.1', " + port + "), 3); print('NET-OK')\nexcept OSError as e: print('net-blocked')\n"
            + "try:\n    print(open(r'" + secret + "').read())\nexcept OSError as e: print('file-blocked')\n"
            + "open(os.path.join(os.environ['PARUX_OUT'], 'a.txt'), 'w').write('artifact')\n";
        ChatCodeResult r = await runner.RunAsync(new ChatCodeRequest(code, new ChatCodeLimits { TimeoutSeconds = 30 }), default);
        if (r.Status == ChatCodeStatus.SandboxUnavailable) Assert.Fail(r.Message);
        Assert.Equal(ChatCodeStatus.Completed, r.Status);
        Assert.Contains("hello 42", r.Output);
        Assert.Contains("net-blocked", r.Output);
        Assert.Contains("file-blocked", r.Output);
        Assert.DoesNotContain("TOP-SECRET-VALUE", r.Output);
        Assert.Equal(0, accepted);
        Assert.Contains(r.Artifacts, a => a.Name == "a.txt" && a.Content == "artifact");

        ChatCodeResult loop = await runner.RunAsync(new ChatCodeRequest("while True: pass", new ChatCodeLimits { TimeoutSeconds = 2 }), default);
        Assert.Equal(ChatCodeStatus.TimedOut, loop.Status);

        ChatCodeResult spawn = await runner.RunAsync(new ChatCodeRequest(
            "import subprocess\ntry:\n    subprocess.run(['cmd','/c','echo CHILD-RAN'], check=True)\n    print('SPAWNED')\nexcept Exception: print('spawn-blocked')"), default);
        Assert.DoesNotContain("CHILD-RAN", spawn.Output);

        ChatCodeResult memory = await runner.RunAsync(new ChatCodeRequest(
            "try:\n    b = bytearray(2*1024**3); print('ALLOCATED')\nexcept MemoryError: print('memory-capped')"), default);
        Assert.DoesNotContain("ALLOCATED", memory.Output);
    }

    private static string? FindPythonInstall()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, "Python3*").FirstOrDefault(d => File.Exists(Path.Combine(d, "python.exe")))
                : null;
        }
        catch (IOException) { return null; }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, dir);
            if (rel.StartsWith("Doc", StringComparison.OrdinalIgnoreCase) || rel.Contains("site-packages", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("include", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("libs", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("tcl", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.Combine(to, rel));
        }
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            string target = Path.Combine(to, rel);
            if (!Directory.Exists(Path.GetDirectoryName(target))) continue;
            File.Copy(file, target, overwrite: true);
        }
    }
}
