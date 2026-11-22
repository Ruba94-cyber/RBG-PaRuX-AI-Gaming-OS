using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Audit gaps 1 and 2: the Guard retry path measures the release like the orchestrator entry path, and the
/// orchestrator stops HardOff children itself instead of waiting for the Guard tick. Fake telemetry, no hardware.</summary>
public sealed class GamingReleaseHandoffGapTests : IDisposable
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-handoff-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public GamingReleaseHandoffGapTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Func<CancellationToken, Task<int?>> Sequence(params int?[] values)
    {
        int i = 0;
        return _ => Task.FromResult(values[Math.Min(Interlocked.Increment(ref i) - 1, values.Length - 1)]);
    }

    private static GamingGuardService NewGuard(Func<CancellationToken, Task<int?>> sampler, Func<IReadOnlyList<int>> owned)
    {
        var process = new LocalAiProcessManager(0);
        var runtime = new LocalRuntimeService(new Env(), process, new ExternalTextProviderService());
        var guard = new GamingGuardService(runtime, new MediaJobService(null!), new SpeechService(), () => true,
            _ => Task.FromResult(true));
        guard.UseReleaseVerification(sampler, owned);
        guard.VramVerifyMaxWait = TimeSpan.FromMilliseconds(300);
        guard.VramVerifyInterval = Fast;
        return guard;
    }

    [Fact]
    public async Task Guard_retry_reports_verified_with_measured_mib()
    {
        GamingGuardService guard = NewGuard(Sequence(12_000, 3_000, 800, 790, 790), () => Array.Empty<int>());

        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.False(guard.ShutdownPending);
        Assert.Equal("verified", guard.LastReleaseVerification);
        Assert.Equal(11_210, guard.LastVramReleasedMib);
    }

    [Fact]
    public async Task Guard_retry_without_telemetry_succeeds_but_is_honestly_unverified()
    {
        GamingGuardService guard = NewGuard(Sequence((int?)null), () => Array.Empty<int>());

        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.False(guard.ShutdownPending);
        Assert.Equal("unverified", guard.LastReleaseVerification);
        Assert.Null(guard.LastVramReleasedMib);
    }

    [Fact]
    public async Task Guard_retry_with_a_surviving_owned_process_stays_pending_and_reports_not_released()
    {
        GamingGuardService guard = NewGuard(Sequence(12_000), () => new[] { 31337 });
        DateTime now = DateTime.UtcNow;

        await guard.TickAsync(now, CancellationToken.None);

        Assert.True(guard.ShutdownPending);
        Assert.Equal("not_released", guard.LastReleaseVerification);
        Assert.Contains("not released", guard.LastShutdownFailure ?? "", StringComparison.OrdinalIgnoreCase);

        // The retry runs the check again once the interval passes.
        await guard.TickAsync(now + GamingShutdownRetry.RetryInterval + TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.True(guard.ShutdownPending);
    }

    private sealed class Child : IGamingHardOffParticipant
    {
        public int Stops;
        public bool Confirms = true;
        public bool StopForGaming() { Stops++; return Confirms; }
        public void ReopenAfterGaming() { }
    }

    private sealed class OkInput : IInputProvider
    {
        public string Id => "handoff-test-input";
        public string Name => "Test";
        public bool IsInstalled => true;
        public string? ExecutablePath => null;
        public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    private async Task<(bool Entered, Child Child, WorkloadOrchestrator Orchestrator)> EnterWithChildAsync(bool childConfirms)
    {
        string profiles = Path.Combine(_work, "profiles-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(_work);
        new GamingProfileStore(profiles).Save(new GamingProfileDto
        {
            Name = "Handoff", SessionType = "local", LauncherProvider = "none",
            InputProvider = "handoff-test-input", SuspendProvider = "none", RestoreAiOnExit = false
        });
        var pm = new LocalAiProcessManager();
        var runtime = new LocalRuntimeService(new Env(), pm, new ExternalTextProviderService());
        var child = new Child { Confirms = childConfirms };
        runtime.HardOff.Register(child);
        var orchestrator = new WorkloadOrchestrator(runtime, pm, new MediaJobService(null!), new SpeechService(runtime),
            new GamingProfileStore(profiles), inputProviders: new[] { new OkInput() },
            gpuUsedMibSampler: Sequence(1_000), ownedInferenceProbe: () => Array.Empty<int>());
        orchestrator.VramVerifyMaxWait = TimeSpan.FromMilliseconds(200);
        orchestrator.VramVerifyInterval = Fast;
        bool entered = await orchestrator.EnterGamingSessionAsync("Handoff", CancellationToken.None);
        return (entered, child, orchestrator);
    }

    [Fact]
    public async Task Orchestrator_hard_off_stops_registered_children_synchronously_without_a_guard_tick()
    {
        var (entered, child, _) = await EnterWithChildAsync(childConfirms: true);

        Assert.True(entered);
        Assert.True(child.Stops >= 1, "the avatar/retrieval/vision child was not stopped by the orchestrator itself");
    }

    [Fact]
    public async Task Orchestrator_entry_fails_honestly_when_a_hard_off_child_does_not_confirm_exit()
    {
        var (entered, child, orchestrator) = await EnterWithChildAsync(childConfirms: false);

        Assert.False(entered);
        Assert.True(child.Stops >= 1);
        Assert.NotEqual("GamingLocal", orchestrator.CurrentState);
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
