using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

[Collection("OverheadBenchmarkSerial")]
public sealed class MediaCachedRamAdmissionTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-media-ram-evict-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public MediaCachedRamAdmissionTests()
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

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static ExecutionResourceDecision RejectedRam(double ramPercent = 91.5) => new(
        Allowed: false,
        TelemetryComplete: true,
        SystemRamUsedPercent: ramPercent,
        MaximumVramUsedPercent: 50,
        MaximumGpuTemperatureC: 65,
        Message: $"Execution Profile resource admission rejected the workload: system RAM {ramPercent:0.0}% > 86%.");

    private static ExecutionResourceDecision AdmittedRam(double ramPercent = 72.0) => new(
        Allowed: true,
        TelemetryComplete: true,
        SystemRamUsedPercent: ramPercent,
        MaximumVramUsedPercent: 50,
        MaximumGpuTemperatureC: 65,
        Message: "Execution Profile resource admission passed measured hardware guardrails.");

    [Fact]
    public void GetMediaWorkingSetBytes_returns_zero_when_no_media_process_is_tracked()
    {
        var manager = new LocalAiProcessManager(16384);
        Assert.Equal(0, manager.GetMediaWorkingSetBytes());
    }

    [Fact]
    public async Task TryFreeMediaCachedModelsAsync_returns_false_when_media_is_not_healthy()
    {
        var manager = new LocalAiProcessManager(16384)
        {
            MediaPortForTests = FreePort(),
            MediaHealthProbeForTests = _ => Task.FromResult(false)
        };

        bool freed = await manager.TryFreeMediaCachedModelsAsync(CancellationToken.None);

        Assert.False(freed);
    }

    [Fact]
    public async Task TryFreeMediaCachedModelsAsync_posts_unload_and_free_memory_when_healthy()
    {
        int port = FreePort();
        string? capturedMethod = null;
        string? capturedPath = null;
        string? capturedBody = null;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                string path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                if (path == "/system_stats")
                {
                    byte[] ok = Encoding.UTF8.GetBytes("{\"system\":{}}");
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.OutputStream.WriteAsync(ok);
                    ctx.Response.Close();
                }
                else if (path == "/free")
                {
                    capturedMethod = ctx.Request.HttpMethod;
                    capturedPath = path;
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    capturedBody = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
        }, cts.Token);

        try
        {
            var manager = new LocalAiProcessManager(16384) { MediaPortForTests = port };
            bool freed = await manager.TryFreeMediaCachedModelsAsync(cts.Token);

            Assert.True(freed);
            Assert.Equal("POST", capturedMethod);
            Assert.Equal("/free", capturedPath);
            Assert.Equal("{\"unload_models\":true,\"free_memory\":true}", capturedBody);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await server; } catch { }
        }
    }

    [Fact]
    public async Task Media_cache_eviction_retries_after_free_and_admits_without_restarting_when_ram_drops()
    {
        int checks = 0;
        int frees = 0;
        int evicts = 0;

        ExecutionResourceDecision decision = await LocalRuntimeService.EvaluateRamWithMediaCacheEvictionAsync(
            checkRam: () => Task.FromResult(++checks == 1 ? RejectedRam() : AdmittedRam()),
            getMediaWorkingSetBytes: () => 2_000_000_000L,
            tryFreeMediaCachedModels: _ => { frees++; return Task.FromResult(true); },
            evictStaleMediaRuntime: _ => { evicts++; return Task.FromResult(true); },
            cancellationToken: CancellationToken.None,
            settleDelay: _ => Task.CompletedTask);

        Assert.True(decision.Allowed);
        Assert.Equal(2, checks);
        Assert.Equal(1, frees);
        Assert.Equal(0, evicts);
    }

    [Fact]
    public async Task Media_cache_eviction_restarts_stale_comfy_when_ram_stays_high_and_working_set_exceeds_threshold()
    {
        int checks = 0;
        int frees = 0;
        int evicts = 0;

        ExecutionResourceDecision decision = await LocalRuntimeService.EvaluateRamWithMediaCacheEvictionAsync(
            checkRam: () => Task.FromResult(++checks < 3 ? RejectedRam() : AdmittedRam()),
            getMediaWorkingSetBytes: () => 3_000_000_000L,
            tryFreeMediaCachedModels: _ => { frees++; return Task.FromResult(true); },
            evictStaleMediaRuntime: _ => { evicts++; return Task.FromResult(true); },
            cancellationToken: CancellationToken.None,
            settleDelay: _ => Task.CompletedTask);

        Assert.True(decision.Allowed);
        Assert.Equal(3, checks);
        Assert.Equal(1, frees);
        Assert.Equal(1, evicts);
    }

    [Fact]
    public async Task Media_cache_eviction_rejects_honestly_when_ram_stays_over_ceiling_after_eviction()
    {
        int checks = 0;
        int frees = 0;
        int evicts = 0;

        ExecutionResourceDecision decision = await LocalRuntimeService.EvaluateRamWithMediaCacheEvictionAsync(
            checkRam: () => { checks++; return Task.FromResult(RejectedRam(90.4)); },
            getMediaWorkingSetBytes: () => 3_000_000_000L,
            tryFreeMediaCachedModels: _ => { frees++; return Task.FromResult(true); },
            evictStaleMediaRuntime: _ => { evicts++; return Task.FromResult(true); },
            cancellationToken: CancellationToken.None,
            settleDelay: _ => Task.CompletedTask);

        Assert.False(decision.Allowed);
        Assert.Equal(90.4, decision.SystemRamUsedPercent);
        Assert.Equal(3, checks);
        Assert.Equal(1, frees);
        Assert.Equal(1, evicts);
    }

    [Fact]
    public async Task Media_cache_eviction_does_not_evict_when_working_set_is_below_threshold()
    {
        int checks = 0;
        int frees = 0;
        int evicts = 0;

        ExecutionResourceDecision decision = await LocalRuntimeService.EvaluateRamWithMediaCacheEvictionAsync(
            checkRam: () => { checks++; return Task.FromResult(RejectedRam()); },
            getMediaWorkingSetBytes: () => 1_000_000_000L,
            tryFreeMediaCachedModels: _ => { frees++; return Task.FromResult(true); },
            evictStaleMediaRuntime: _ => { evicts++; return Task.FromResult(true); },
            cancellationToken: CancellationToken.None,
            settleDelay: _ => Task.CompletedTask);

        Assert.False(decision.Allowed);
        Assert.Equal(1, checks);
        Assert.Equal(0, frees);
        Assert.Equal(0, evicts);
    }
}
