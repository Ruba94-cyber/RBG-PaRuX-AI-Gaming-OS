using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The Local AI OFF guarantee: Off during a slow start must not be followed by a runtime that finished loading anyway.</summary>
public sealed class OffDuringStartTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-off-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;
    private readonly List<IReadOnlyList<string>> _launches = new();
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private bool _healthy;

    public OffDuringStartTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        Write(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        Write(LocalAiPaths.SmallModel);
    }

    public void Dispose()
    {
        _release.Set();
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
    }

    private LocalAiProcessManager NewManager(bool slow) => new(0)
    {
        InventoryProbe = () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd()),
        TextHealthProbeForTests = _ => Task.FromResult(_healthy),
        LaunchInterceptForTests = (_, arguments) =>
        {
            _launches.Add(arguments);
            LocalAiProcessManager.RecordActiveTextRuntime(arguments);
            if (slow)
            {
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(20));
            }
            _healthy = true;
            return new LocalAiProcessManager.RuntimeResult(true, "Started: intercepted");
        }
    };

    [Fact]
    public async Task A_start_queued_behind_a_slow_one_never_launches_after_Off_and_Off_waits_for_the_running_one()
    {
        var manager = NewManager(slow: true);
        Task<LocalAiProcessManager.RuntimeResult> first = Task.Run(() => manager.RunActionAsync("On", CancellationToken.None));
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(20)));

        // A second start (what Gaming's eager restore does) queues behind the first for the gate.
        Task<LocalAiProcessManager.RuntimeResult> queued = Task.Run(() => manager.StartTextAsync(CancellationToken.None));
        await Task.Delay(200);
        Task<LocalAiProcessManager.RuntimeResult> off = Task.Run(() => manager.RunActionAsync("Off", CancellationToken.None));
        await Task.Delay(300);
        Assert.False(off.IsCompleted); // Off waits for the start in flight instead of answering "Stopped." over it

        _release.Set();
        await Task.WhenAll(first, off, queued).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(_launches); // the queued start never launched
        Assert.False(queued.Result.Success);
        Assert.Contains("OFF", queued.Result.Message + " off ", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("OFF", manager.GetMode());
        Assert.True(off.Result.Success);
    }

    [Fact]
    public async Task Ensure_after_Off_is_refused_and_launches_nothing()
    {
        var manager = NewManager(slow: false);
        await manager.RunActionAsync("Off", CancellationToken.None);

        LocalAiProcessManager.RuntimeResult result = await manager.StartTextAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(_launches);
    }
}
