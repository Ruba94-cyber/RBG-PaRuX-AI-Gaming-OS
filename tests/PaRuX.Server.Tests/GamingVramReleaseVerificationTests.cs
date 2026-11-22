using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Game start -> AI released: the release must be measured, not assumed. Fake telemetry, no hardware.</summary>
public sealed class GamingVramReleaseVerificationTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    private static Func<CancellationToken, Task<int?>> Sequence(params int?[] values)
    {
        int i = 0;
        return _ => Task.FromResult(values[Math.Min(Interlocked.Increment(ref i) - 1, values.Length - 1)]);
    }

    [Fact]
    public async Task Verifier_Verified_WhenOwnedProcessesGoneAndMemorySettles()
    {
        var report = await GamingVramReleaseVerifier.VerifyAsync(
            14_000, Sequence(9_000, 2_000, 1_000, 990), () => Array.Empty<int>(), CancellationToken.None,
            TimeSpan.FromSeconds(2), Fast);

        Assert.Equal(GamingVramReleaseOutcome.Verified, report.Outcome);
        Assert.Equal(990, report.AfterUsedMib);
        Assert.Equal(13_010, report.ReleasedMib);
    }

    [Fact]
    public async Task Verifier_NotReleased_WhenOwnedProcessSurvivesBoundedWait()
    {
        var report = await GamingVramReleaseVerifier.VerifyAsync(
            14_000, Sequence(14_000), () => new[] { 4242 }, CancellationToken.None, TimeSpan.FromMilliseconds(60), Fast);

        Assert.Equal(GamingVramReleaseOutcome.NotReleased, report.Outcome);
        Assert.Contains(4242, report.OwnedProcessesStillAlive);
        Assert.True(report.Waited >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Verifier_Unverified_WhenTelemetryUnavailable_NeverClaimsVerified()
    {
        var report = await GamingVramReleaseVerifier.VerifyAsync(
            null, Sequence((int?)null), () => Array.Empty<int>(), CancellationToken.None, TimeSpan.FromSeconds(1), Fast);

        Assert.Equal(GamingVramReleaseOutcome.Unverified, report.Outcome);
        Assert.Null(report.ReleasedMib);
    }

    [Fact]
    public async Task Verifier_Unverified_WhenMemoryNeverSettles()
    {
        int value = 20_000;
        var report = await GamingVramReleaseVerifier.VerifyAsync(
            20_000, _ => Task.FromResult<int?>(value -= 1_000), () => Array.Empty<int>(), CancellationToken.None,
            TimeSpan.FromMilliseconds(60), Fast);

        Assert.Equal(GamingVramReleaseOutcome.Unverified, report.Outcome);
    }

    [Fact]
    public async Task Verifier_SamplerFault_IsTreatedAsUnknown_NotAsFailure()
    {
        var report = await GamingVramReleaseVerifier.VerifyAsync(
            1_000, _ => throw new InvalidOperationException("nvml"), () => Array.Empty<int>(), CancellationToken.None,
            TimeSpan.FromMilliseconds(40), Fast);

        Assert.Equal(GamingVramReleaseOutcome.Unverified, report.Outcome);
    }

    [Fact]
    public async Task Verifier_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GamingVramReleaseVerifier.VerifyAsync(
            1, Sequence(1), () => new[] { 1 }, cts.Token, TimeSpan.FromSeconds(5), Fast));
    }

    private sealed class OkInput : IInputProvider
    {
        public string Id => "vram-test-input";
        public string Name => "Test";
        public bool IsInstalled => true;
        public string? ExecutablePath => null;
        public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    private static async Task<(WorkloadOrchestrator Orchestrator, bool Entered)> EnterAsync(
        Func<CancellationToken, Task<int?>> sampler, Func<IReadOnlyList<int>> owned)
    {
        string profiles = Path.Combine(Path.GetTempPath(), "parux-vram-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        string state = Path.Combine(Path.GetTempPath(), "parux-vram-state-" + Guid.NewGuid().ToString("N"));
        using var scope = LocalAiPaths.UseStateDirForTests(state);
        try
        {
            new GamingProfileStore(profiles).Save(new GamingProfileDto
            {
                Name = "Vram Verify", SessionType = "local", LauncherProvider = "none",
                InputProvider = "vram-test-input", SuspendProvider = "none", RestoreAiOnExit = false
            });
            var env = new TestHostEnvironment { EnvironmentName = "Testing" };
            var pm = new LocalAiProcessManager();
            var runtime = new LocalRuntimeService(env, pm, new ExternalTextProviderService());
            var orchestrator = new WorkloadOrchestrator(runtime, pm, new MediaJobService(null!), new SpeechService(runtime),
                new GamingProfileStore(profiles),
                inputProviders: new[] { new OkInput() },
                gpuUsedMibSampler: sampler, ownedInferenceProbe: owned);
            orchestrator.VramVerifyMaxWait = TimeSpan.FromMilliseconds(400);
            orchestrator.VramVerifyInterval = Fast;
            bool entered = await orchestrator.EnterGamingSessionAsync("Vram Verify", CancellationToken.None);
            return (orchestrator, entered);
        }
        finally
        {
            try { if (File.Exists(profiles)) File.Delete(profiles); } catch { }
            try { if (Directory.Exists(state)) Directory.Delete(state, true); } catch { }
        }
    }

    [Fact]
    public async Task Enter_ReportsVerifiedRelease_WithMeasuredMib()
    {
        var (o, entered) = await EnterAsync(Sequence(12_000, 3_000, 800, 790, 790), () => Array.Empty<int>());

        Assert.True(entered);
        var status = o.GetStatus();
        Assert.Equal("verified", status.AiReleaseVerification);
        Assert.Equal(11_210, status.AiVramReleasedMib);
        Assert.Contains(status.SuspendedAiWorkloads, w => w.StartsWith("GPU release:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enter_WithoutTelemetry_StillEntersButIsHonestlyUnverified()
    {
        var (o, entered) = await EnterAsync(Sequence((int?)null), () => Array.Empty<int>());

        Assert.True(entered);
        var status = o.GetStatus();
        Assert.Equal("unverified", status.AiReleaseVerification);
        Assert.Null(status.AiVramReleasedMib);
    }

    [Fact]
    public async Task Enter_FailsHonestly_WhenOwnedInferenceProcessSurvives_AndRollsBackWithoutGaming()
    {
        var (o, entered) = await EnterAsync(Sequence(12_000), () => new[] { 31337 });

        Assert.False(entered);
        Assert.NotEqual("GamingLocal", o.CurrentState);
        Assert.Contains("not released", o.GetStatus().Message ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
