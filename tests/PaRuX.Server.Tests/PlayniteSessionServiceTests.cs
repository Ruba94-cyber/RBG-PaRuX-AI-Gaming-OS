using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Playnite;
using Xunit;

namespace PaRuX.Server.Tests;

internal sealed class FakeYieldTarget : IPlayniteYieldTarget
{
    public string CurrentState { get; set; } = "AiReady";
    public long? ActiveSessionVersion { get; set; }
    public int EnterCalls, ExitCalls;
    public int EnterFailures, ExitFailures;
    public long NextVersion = 7;
    public bool EnterThrows;
    public List<(int Pid, long Version)> Registered = new();
    public List<long> Cleared = new();

    public Task<long?> EnterAsync(CancellationToken ct)
    {
        EnterCalls++;
        if (EnterThrows) throw new InvalidOperationException("boom");
        if (EnterFailures > 0) { EnterFailures--; return Task.FromResult<long?>(null); }
        CurrentState = "GamingLocal";
        ActiveSessionVersion = NextVersion;
        return Task.FromResult<long?>(NextVersion);
    }

    public Task<bool> ExitAsync(long version, CancellationToken ct)
    {
        ExitCalls++;
        if (ExitFailures > 0) { ExitFailures--; return Task.FromResult(false); }
        CurrentState = "AiReady";
        ActiveSessionVersion = null;
        return Task.FromResult(true);
    }

    public void RegisterOwnedGame(int pid, DateTime startUtc, string imagePath, long version) => Registered.Add((pid, version));
    public void ClearOwnedGame(long version) => Cleared.Add(version);
}

internal sealed class FakeHost : IPlayniteProcessHost
{
    public sealed class Proc : IOwnedPlayniteProcess
    {
        public int Id { get; init; }
        public DateTime StartUtc { get; init; } = DateTime.UtcNow;
        public bool HasExited { get; set; }
        public bool ExitsOnClose = true;
        public int CloseCalls;
        public bool CloseMainWindow() { CloseCalls++; if (ExitsOnClose) { HasExited = true; Exited?.Invoke(); } return true; }
        public bool WaitForExit(int ms) => HasExited;
        public event Action? Exited;
        public void Dispose() { }
        public void ExitNow() { HasExited = true; Exited?.Invoke(); }
    }

    public List<(string Exe, string Dir, bool Minimized)> Starts = new();
    public List<int> Focused = new();
    public Proc? Last;
    public bool ReturnNull;
    public IOwnedPlayniteProcess? Start(string exePath, string workingDirectory, bool minimized)
    {
        Starts.Add((exePath, workingDirectory, minimized));
        if (ReturnNull) return null;
        return Last = new Proc { Id = 4000 + Starts.Count };
    }
    public bool Focus(int pid) { Focused.Add(pid); return true; }
}

internal sealed class FakeEnv : IPlayniteEnvironment
{
    public string LocalAppData { get; set; } = @"C:\Users\x\AppData\Local";
    public string AppData { get; set; } = @"C:\Users\x\AppData\Roaming";
    public string ProgramFiles { get; set; } = @"C:\Program Files";
    public List<PlayniteUninstallEntry> Uninstall = new();
    public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Versions = new(StringComparer.OrdinalIgnoreCase);
    public List<PlayniteRunningProcess> Running = new();
    public IEnumerable<PlayniteUninstallEntry> UninstallEntries() => Uninstall;
    public bool FileExists(string path) => Files.Contains(path);
    public string? FileVersion(string path) => Versions.TryGetValue(path, out string? v) ? v : null;
    public IReadOnlyList<PlayniteRunningProcess> RunningPlaynite() => Running.ToList();
}

public sealed class PlayniteSessionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-pl-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessWorld _world = new FakeProcessWorld().Add(100, 1, "Playnite.FullscreenApp.exe", -60);
    private readonly FakeYieldTarget _target = new();
    private DateTime _now = FakeProcessWorld.T0;
    private readonly PlayniteSettingsStore _settings;
    private readonly PlayniteSessionService _service;
    private readonly FakeHost _host = new();

    public PlayniteSessionServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new PlayniteSettingsStore(Path.Combine(_dir, "s.json"));
        var env = new FakeEnv();
        _service = new PlayniteSessionService(_settings, new PlayniteLocator(env),
            new PlayniteLauncher(_host, env, _ => true), _world, _target,
            new PlayniteGameSessionTracker(9999, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)), () => _now);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task Run(double fromSeconds, double toSeconds)
    {
        for (double s = fromSeconds; s <= toSeconds; s++)
        {
            _now = FakeProcessWorld.T0.AddSeconds(s);
            await _service.TickAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Game_start_yields_the_gpu_once()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        Assert.Equal(1, _target.EnterCalls);
        Assert.Equal("yielded", _service.GetSession().State);
        Assert.Equal("game", _service.GetSession().GameProcessName);
    }

    [Fact]
    public async Task Game_exit_restores_ai_availability()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        _world.Remove(200);
        await Run(31, 60);
        Assert.Equal(1, _target.ExitCalls);
        Assert.Equal("idle", _service.GetSession().State);
        Assert.Equal("AiReady", _target.CurrentState);
        Assert.Contains(7L, _target.Cleared);
    }

    [Fact]
    public async Task Nothing_happens_without_a_game()
    {
        await Run(0, 60);
        Assert.Equal(0, _target.EnterCalls);
        Assert.Equal("idle", _service.GetSession().State);
    }

    [Fact]
    public async Task Entry_is_retried_with_backoff_then_succeeds()
    {
        _target.EnterFailures = 2;
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 40);
        Assert.Equal(3, _target.EnterCalls);
        Assert.Equal("yielded", _service.GetSession().State);
        Assert.Null(_service.GetSession().LastError);
    }

    [Fact]
    public async Task Entry_failure_is_reported_honestly_after_bounded_attempts()
    {
        _target.EnterFailures = 99;
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 120);
        Assert.Equal(PlayniteSessionService.MaxAttempts, _target.EnterCalls);
        PlayniteSessionDto s = _service.GetSession();
        Assert.Equal("failed", s.State);
        Assert.False(string.IsNullOrWhiteSpace(s.LastError));
    }

    [Fact]
    public async Task Entry_exception_counts_as_a_failure_not_a_crash()
    {
        _target.EnterThrows = true;
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 120);
        Assert.Equal("failed", _service.GetSession().State);
        Assert.Contains("failed", _service.GetSession().LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exit_is_retried_then_succeeds()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        _target.ExitFailures = 2;
        _world.Remove(200);
        await Run(31, 80);
        Assert.Equal(3, _target.ExitCalls);
        Assert.Equal("idle", _service.GetSession().State);
    }

    [Fact]
    public async Task Exit_failure_is_reported_never_hidden()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        _target.ExitFailures = 99;
        _world.Remove(200);
        await Run(31, 150);
        Assert.Equal(PlayniteSessionService.MaxAttempts, _target.ExitCalls);
        Assert.Equal("failed", _service.GetSession().State);
        Assert.Contains("Return to AI", _service.GetSession().LastError!);
    }

    [Fact]
    public async Task Already_active_gaming_is_not_driven_or_ended_by_playnite()
    {
        _target.CurrentState = "GamingLocal";
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        _world.Remove(200);
        await Run(31, 60);
        Assert.Equal(0, _target.EnterCalls);
        Assert.Equal(0, _target.ExitCalls);
    }

    [Fact]
    public async Task Owner_returning_to_ai_during_the_game_is_respected()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        _target.ActiveSessionVersion = null;     // the owner pressed Return to AI
        _target.CurrentState = "AiReady";
        await Run(31, 60);
        Assert.Equal(1, _target.EnterCalls);      // never re-entered during the same game
        Assert.Equal("user-override", _service.GetSession().State);
        _world.Remove(200);
        await Run(61, 90);
        Assert.Equal(0, _target.ExitCalls);       // nothing of ours left to restore
    }

    [Fact]
    public async Task Disabled_ai_yield_never_touches_the_orchestrator()
    {
        var s = _settings.Get(); s.AiYieldEnabled = false; _settings.Save(s);
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 60);
        Assert.Equal(0, _target.EnterCalls);
        Assert.Equal("disabled", _service.GetSession().State);
    }

    [Fact]
    public async Task Skipped_component_never_touches_the_orchestrator()
    {
        _settings.SetSkipped(true);
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 60);
        Assert.Equal(0, _target.EnterCalls);
    }

    [Fact]
    public async Task Closing_playnite_during_the_game_keeps_ai_off_until_the_game_ends()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        _world.Remove(100);
        await Run(31, 100);
        Assert.Equal(0, _target.ExitCalls);
        _world.Remove(200);
        await Run(101, 130);
        Assert.Equal(1, _target.ExitCalls);
    }

    [Fact]
    public async Task Relaunch_drives_a_second_session()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        _world.Remove(200);
        await Run(31, 60);
        _target.NextVersion = 9;
        _world.Add(201, 100, "game2.exe", 70);
        await Run(70, 90);
        Assert.Equal(2, _target.EnterCalls);
        Assert.Equal("yielded", _service.GetSession().State);
    }

    [Fact]
    public async Task Rapid_start_stop_does_not_reach_the_orchestrator()
    {
        for (int i = 0; i < 4; i++)
        {
            _world.Add(300 + i, 100, "stub.exe", i * 10);
            await Run(i * 10, i * 10 + 1);
            _world.Remove(300 + i);
            await Run(i * 10 + 2, i * 10 + 8);
        }
        Assert.Equal(0, _target.EnterCalls);
    }

    [Fact]
    public async Task Cancellation_stops_a_tick_cleanly()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 12);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _now = FakeProcessWorld.T0.AddSeconds(20);
        // The entry attempt observes the cancelled token through the target; the fake ignores it, so it must at least not throw.
        await _service.TickAsync(cts.Token);
    }

    [Fact]
    public async Task Pause_ownership_is_registered_only_for_a_playnite_that_paRuX_started()
    {
        _world.Add(200, 100, "game.exe", 10);
        await Run(10, 30);
        Assert.Empty(_target.Registered);   // Playnite was not started by PaRuX in this test
    }

    [Fact]
    public async Task Foreign_process_activity_never_changes_state()
    {
        _world.Add(500, 1, "chrome.exe", 5).Add(501, 500, "chrome-render.exe", 5);
        await Run(5, 60);
        Assert.Equal(0, _target.EnterCalls);
        Assert.Equal(0, _service.GetSession().TrackedProcessCount);
    }
}
