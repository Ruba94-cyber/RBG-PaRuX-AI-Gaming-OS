using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Corporate Lite ships the pinned Vulkan engine next to the validated CPU engine. These tests pin the contract:
/// Vulkan only when present AND an adapter exists AND IT has not disabled it; any Vulkan failure falls back to the CPU
/// engine, observably; the memory admission counts the GPU's shared memory against the same RAM budget; and the package
/// builder/verifier carry the pinned runtime. Test parallelization is disabled assembly-wide, so the process environment
/// variables used here are restored in Dispose without racing another test.
/// </summary>
public sealed class CorporateGpuBackendTests : IDisposable
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private static double Estimate(int context) => context switch
    {
        4096 => 3100, 3072 => 2750, 2048 => 1600, _ => throw new ArgumentOutOfRangeException(nameof(context))
    };

    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-corpgpu-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _scopes = [];
    private readonly Dictionary<string, string?> _savedEnvironment = [];

    public CorporateGpuBackendTests()
    {
        _scopes.Add(LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai")));
        _scopes.Add(LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state")));
        _scopes.Add(ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_work, "models")));
        SetEnvironment(CorporateMode.EnvironmentVariable, "1");
        SetEnvironment(CorporateGpuBackendPolicy.DisableEnvironmentVariable, null);
        CorporateGpuBackendPolicy.ResetForTests();
        VulkanDeviceSelector.ResetForTests();
        VulkanDeviceSelector.ListDevicesOverrideForTests = (_, _) =>
            Task.FromResult("Vulkan0: AMD Radeon 780M Graphics (16384 MiB, 15000 MiB free)");
    }

    public void Dispose()
    {
        VulkanDeviceSelector.ResetForTests();
        CorporateGpuBackendPolicy.ResetForTests();
        foreach ((string name, string? value) in _savedEnvironment) Environment.SetEnvironmentVariable(name, value);
        for (int i = _scopes.Count - 1; i >= 0; i--) _scopes[i].Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void SetEnvironment(string name, string? value)
    {
        if (!_savedEnvironment.ContainsKey(name)) _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    // ---- pure backend decision --------------------------------------------------------------------------------------------

    [Fact]
    public void Vulkan_is_chosen_only_when_corporate_runtime_present_and_adapter_usable()
    {
        var inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia());
        var decision = CorporateGpuBackendPolicy.Decide(true, null, true, inventory, false);
        Assert.Equal(CorporateGpuBackendPolicy.Backend.Vulkan, decision.Backend);
        Assert.Equal("NVIDIA Test Adapter", decision.Adapter?.Name);

        Assert.Equal(CorporateGpuBackendPolicy.Backend.Cpu,
            CorporateGpuBackendPolicy.Decide(true, null, false, inventory, false).Backend);          // runtime absent
        Assert.Equal(CorporateGpuBackendPolicy.Backend.Cpu,
            CorporateGpuBackendPolicy.Decide(true, null, true, GpuInventoryFixtures.Unavailable(), false).Backend); // no adapter
        Assert.Equal(CorporateGpuBackendPolicy.Backend.Cpu,
            CorporateGpuBackendPolicy.Decide(true, null, true, null, false).Backend);                // inventory failed
        Assert.Equal(CorporateGpuBackendPolicy.Backend.Cpu,
            CorporateGpuBackendPolicy.Decide(true, null, true, inventory, recentlyFailed: true).Backend); // failed a moment ago
        Assert.Equal(CorporateGpuBackendPolicy.Backend.Cpu,
            CorporateGpuBackendPolicy.Decide(false, null, true, inventory, false).Backend);          // not Corporate: never
    }

    [Fact]
    public void Software_renderers_are_not_a_usable_adapter()
    {
        var software = new GpuAdapterStatus { Name = "Microsoft Basic Render Driver", VendorId = 0x1414, IsSoftware = true };
        var inventory = GpuInventoryFixtures.Inventory(software);
        Assert.Null(CorporateGpuBackendPolicy.PickAdapter(inventory));
        Assert.Equal(CorporateGpuBackendPolicy.Backend.Cpu,
            CorporateGpuBackendPolicy.Decide(true, null, true, inventory, false).Backend);
    }

    [Theory]
    [InlineData("0", true)] [InlineData("false", true)] [InlineData("OFF", true)] [InlineData(" no ", true)]
    [InlineData("cpu", true)] [InlineData(null, false)] [InlineData("", false)] [InlineData("1", false)] [InlineData("auto", false)]
    public void The_IT_switch_forces_cpu_only(string? value, bool disabled)
    {
        Assert.Equal(disabled, CorporateGpuBackendPolicy.IsDisabledByEnvironment(value));
        var inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia());
        var decision = CorporateGpuBackendPolicy.Decide(true, value, true, inventory, false);
        Assert.Equal(disabled ? CorporateGpuBackendPolicy.Backend.Cpu : CorporateGpuBackendPolicy.Backend.Vulkan, decision.Backend);
        if (disabled) Assert.Contains("PARUX_CORPORATE_GPU", decision.Reason);
    }

    [Theory]
    [InlineData(null, false)]       // default: an integrated GPU measured slower than the CPU engine, so the CPU runs
    [InlineData("1", false)]
    [InlineData("auto", false)]
    [InlineData("force", true)]     // IT/owner opts in explicitly
    [InlineData(" Integrated ", true)]
    [InlineData("always", true)]
    [InlineData("0", false)]        // the kill switch still wins
    public void An_integrated_gpu_only_runs_the_model_when_forced(string? value, bool vulkan)
    {
        var inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd());
        var decision = CorporateGpuBackendPolicy.Decide(true, value, true, inventory, false);
        Assert.Equal(vulkan ? CorporateGpuBackendPolicy.Backend.Vulkan : CorporateGpuBackendPolicy.Backend.Cpu, decision.Backend);
        if (!vulkan && value is not "0") Assert.Contains("integrated GPU", decision.Reason);
    }

    [Fact]
    public void A_discrete_gpu_keeps_vulkan_by_default()
    {
        var inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia());
        Assert.Equal(CorporateGpuBackendPolicy.Backend.Vulkan,
            CorporateGpuBackendPolicy.Decide(true, null, true, inventory, false).Backend);
    }

    [Fact]
    public void A_failed_model_stays_on_cpu_for_a_bounded_time_only()
    {
        DateTime now = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        CorporateGpuBackendPolicy.MarkFailed("m.gguf", now);
        Assert.True(CorporateGpuBackendPolicy.RecentlyFailed("m.gguf", now.AddMinutes(9)));
        Assert.False(CorporateGpuBackendPolicy.RecentlyFailed("m.gguf", now.AddMinutes(11)));
        Assert.False(CorporateGpuBackendPolicy.RecentlyFailed("other.gguf", now));
    }

    [Fact]
    public void Launch_arguments_add_only_device_and_full_offload_to_the_validated_cpu_arguments()
    {
        List<string> cpu = LocalAiProcessManager.BuildCorporateCpuArguments("m.gguf", 8080, 16, 3072, 180);
        List<string> vulkan = LocalAiProcessManager.BuildCorporateVulkanArguments("m.gguf", 8080, 16, 3072, 180, "Vulkan1");
        Assert.DoesNotContain("--device", cpu);
        Assert.DoesNotContain("--gpu-layers", cpu);
        Assert.Equal(cpu, vulkan.Take(cpu.Count).ToList());
        Assert.Equal(["--device", "Vulkan1", "--gpu-layers", "all"], vulkan.Skip(cpu.Count).ToArray());
        Assert.Equal("127.0.0.1", vulkan[vulkan.IndexOf("--host") + 1]);
    }

    // ---- memory: shared GPU memory counts against the same RAM budget ----------------------------------------------------

    [Fact]
    public void Vulkan_plan_never_admits_what_the_cpu_plan_refuses_and_adds_the_shared_memory_overhead()
    {
        GpuAdapterStatus igpu = GpuInventoryFixtures.IntegratedAmd();
        Assert.True(CorporateGpuBackendPolicy.IsIntegrated(igpu));
        bool sawReducedOrRefused = false;
        for (ulong mib = 1500; mib <= 14000; mib += 32)
        {
            var ram = new SystemRamSnapshot(16 * GiB, mib * 1024UL * 1024UL);
            var cpu = CorporateCpuMemoryPlanner.Select(ram, 86, 0, Estimate);
            var vk = CorporateGpuBackendPolicy.PlanVulkan(ram, 86, 0, Estimate, igpu);
            if (vk.Fits)
            {
                Assert.True(cpu.Fits, "Vulkan admitted at " + mib + " MiB where CPU refuses.");
                Assert.True(vk.ContextTokens <= cpu.ContextTokens);
                Assert.Equal(Estimate(vk.ContextTokens) + CorporateGpuBackendPolicy.IntegratedOverheadMib, vk.NeededMib);
            }
            if (cpu.Fits && (!vk.Fits || vk.ContextTokens < cpu.ContextTokens)) sawReducedOrRefused = true;
        }
        Assert.True(sawReducedOrRefused, "the shared-memory overhead must be able to tip the decision to a smaller context or the CPU");
    }

    [Fact]
    public void A_discrete_card_has_a_smaller_host_overhead_than_a_shared_memory_igpu()
    {
        GpuAdapterStatus dgpu = GpuInventoryFixtures.Nvidia(12L * 1024 * 1024 * 1024);
        Assert.False(CorporateGpuBackendPolicy.IsIntegrated(dgpu));
        Assert.True(CorporateGpuBackendPolicy.OverheadMib(dgpu) < CorporateGpuBackendPolicy.OverheadMib(GpuInventoryFixtures.IntegratedAmd()));
    }

    [Fact]
    public void A_model_larger_than_the_adapter_can_address_is_not_offloaded_even_with_plenty_of_ram()
    {
        var tiny = new GpuAdapterStatus
        {
            Name = "Tiny GPU", VendorId = 0x8086, DedicatedVideoMemoryBytes = 256L * 1024 * 1024, SharedSystemMemoryBytes = 1L * 1024 * 1024 * 1024
        };
        var ram = new SystemRamSnapshot(64 * GiB, 60 * GiB);
        Assert.True(CorporateCpuMemoryPlanner.Select(ram, 86, 0, Estimate).Fits);
        Assert.False(CorporateGpuBackendPolicy.PlanVulkan(ram, 86, 0, Estimate, tiny).Fits);
    }

    [Fact]
    public void Unmeasurable_ram_fails_closed_for_vulkan_too()
    {
        Assert.False(CorporateGpuBackendPolicy.PlanVulkan(null, 86, 0, Estimate, GpuInventoryFixtures.IntegratedAmd()).Fits);
    }

    // ---- real launch ordering and fallback (the process launches are intercepted) ----------------------------------------

    private sealed record Launch(string Exe, IReadOnlyList<string> Args);

    private (LocalAiProcessManager Manager, List<Launch> Launches) Prepare(bool vulkanPresent, GpuInventoryStatus inventory,
        Func<string, bool> succeeds)
    {
        string cpuDir = Path.Combine(LocalAiPaths.RuntimeRoot, "llama.cpp-b9999-cpu");
        Directory.CreateDirectory(cpuDir);
        File.WriteAllText(Path.Combine(cpuDir, LocalAiPaths.LlamaServerExecutable), "stub");
        if (vulkanPresent)
        {
            Directory.CreateDirectory(LocalAiPaths.RuntimeVulkanPath);
            File.WriteAllText(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable), "stub");
        }
        string models = ModelDownloadService.ModelsDirectory;
        Directory.CreateDirectory(models);
        byte[] gguf = new byte[128 * 1024];
        "GGUF"u8.CopyTo(gguf);
        File.WriteAllBytes(Path.Combine(models, "Starter-Q4_K_M.gguf"), gguf);
        File.WriteAllText(Path.Combine(models, ".corporate-default-model"), "Starter-Q4_K_M.gguf");

        var launches = new List<Launch>();
        var manager = new LocalAiProcessManager(0)
        {
            InventoryProbe = () => inventory,
            LaunchInterceptForTests = (exe, args) =>
            {
                launches.Add(new Launch(exe, args));
                bool vulkan = exe.Contains("-vulkan", StringComparison.OrdinalIgnoreCase);
                return succeeds(vulkan ? "vulkan" : "cpu")
                    ? new LocalAiProcessManager.RuntimeResult(true, "ready " + (vulkan ? "vulkan" : "cpu"))
                    : new LocalAiProcessManager.RuntimeResult(false, vulkan ? "failed to load model: out of memory (Vulkan0)" : "cpu failed");
            }
        };
        return (manager, launches);
    }

    private static bool IsVulkan(Launch l) => l.Exe.Contains("-vulkan", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Corporate_with_runtime_and_adapter_starts_on_vulkan_and_does_not_start_cpu()
    {
        var (manager, launches) = Prepare(true, GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia()), _ => true);
        var result = await manager.StartTextAsync(CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Launch only = Assert.Single(launches);
        Assert.True(IsVulkan(only));
        Assert.Equal("Vulkan0", only.Args[only.Args.ToList().IndexOf("--device") + 1]);
        Assert.Equal("all", only.Args[only.Args.ToList().IndexOf("--gpu-layers") + 1]);
        Assert.Contains("GPU offload", result.Message);
        Assert.Contains(manager.LastTextLaunchTrace, line => line.Contains("Vulkan0: ready"));
    }

    [Fact]
    public async Task A_failed_vulkan_start_falls_back_to_the_cpu_engine_observably_and_is_not_retried_at_once()
    {
        var (manager, launches) = Prepare(true, GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia()), kind => kind == "cpu");
        var result = await manager.StartTextAsync(CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal(2, launches.Count);
        Assert.True(IsVulkan(launches[0]));
        Assert.False(IsVulkan(launches[1]));
        Assert.DoesNotContain("--device", launches[1].Args);
        Assert.Equal(launches[0].Args[launches[0].Args.ToList().IndexOf("--port") + 1],
            launches[1].Args[launches[1].Args.ToList().IndexOf("--port") + 1]);
        Assert.Contains("GPU start failed", result.Message);
        Assert.Contains("CPU engine", result.Message);
        Assert.Contains(manager.LastTextLaunchTrace, line => line.Contains("Vulkan0: failed"));
        Assert.Contains(manager.LastTextLaunchTrace, line => line.Contains("Corporate CPU: ready"));

        // The next start does not wait for another GPU attempt: Vulkan stays off for this model for a bounded time.
        launches.Clear();
        var again = await manager.StartTextAsync(CancellationToken.None, intent: TextStartIntent.ForcedRestart);
        Assert.True(again.Success, again.Message);
        Assert.All(launches, l => Assert.False(IsVulkan(l)));
    }

    [Fact]
    public async Task If_both_engines_fail_the_failure_is_reported_honestly()
    {
        var (manager, launches) = Prepare(true, GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia()), _ => false);
        var result = await manager.StartTextAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(2, launches.Count);
        Assert.Contains("cpu failed", result.Message);
    }

    [Fact]
    public async Task The_IT_switch_keeps_corporate_on_the_cpu_engine_without_probing_vulkan()
    {
        SetEnvironment(CorporateGpuBackendPolicy.DisableEnvironmentVariable, "0");
        var (manager, launches) = Prepare(true, GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia()), _ => true);
        var result = await manager.StartTextAsync(CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.False(IsVulkan(Assert.Single(launches)));
        Assert.Contains(manager.LastTextLaunchTrace, line => line.Contains("PARUX_CORPORATE_GPU"));
    }

    [Fact]
    public async Task Without_an_adapter_or_without_the_bundled_runtime_corporate_uses_the_cpu_engine()
    {
        var (noAdapter, launchesA) = Prepare(true, GpuInventoryFixtures.Unavailable(), _ => true);
        Assert.True((await noAdapter.StartTextAsync(CancellationToken.None)).Success);
        Assert.False(IsVulkan(Assert.Single(launchesA)));

        Directory.Delete(LocalAiPaths.RuntimeVulkanPath, recursive: true); // the first scenario had created it
        var (noRuntime, launchesB) = Prepare(false, GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia()), _ => true);
        Assert.True((await noRuntime.StartTextAsync(CancellationToken.None)).Success);
        Assert.False(IsVulkan(Assert.Single(launchesB)));
    }

    [Fact]
    public async Task Runtime_status_lists_the_vulkan_backend_only_when_it_is_shipped_and_not_disabled()
    {
        var (manager, _) = Prepare(true, GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia()), _ => true);
        var service = new RuntimeManagerService(manager);
        var with = await service.GetStatusAsync(CancellationToken.None, ramSnapshot: null);
        Assert.Contains(with.Backends, b => b.Backend == "text-cpu" && b.Installed);
        Assert.Contains(with.Backends, b => b.Backend == "text-vulkan" && b.Installed);
        Assert.DoesNotContain(with.Backends, b => b.Backend is "text-cuda" or "media-comfyui");

        SetEnvironment(CorporateGpuBackendPolicy.DisableEnvironmentVariable, "0");
        var without = await service.GetStatusAsync(CancellationToken.None, ramSnapshot: null);
        Assert.DoesNotContain(without.Backends, b => b.Backend == "text-vulkan");
    }

    // ---- packaging: the pinned runtime is in the builder, the manifest and the verifier --------------------------------------

    [Fact]
    public void The_lock_pins_the_vulkan_runtime_the_core_resolves()
    {
        using JsonDocument lockDoc = JsonDocument.Parse(Read("packaging", "windows-baseline-runtime.lock.json"));
        JsonElement vulkan = lockDoc.RootElement.GetProperty("artifacts").EnumerateArray()
            .Single(a => a.GetProperty("id").GetString() == "llama.cpp-vulkan");
        Assert.Matches("^[0-9a-f]{64}$", vulkan.GetProperty("sha256").GetString()!);
        Assert.True(vulkan.GetProperty("sizeBytes").GetInt64() > 1_000_000);
        Assert.Equal(LocalAiPaths.DefaultVulkanBuildTag, vulkan.GetProperty("version").GetString());
        Assert.EndsWith("/llama.cpp-" + LocalAiPaths.DefaultVulkanBuildTag + "-vulkan", vulkan.GetProperty("destination").GetString());
    }

    [Fact]
    public void The_corporate_builder_stages_the_locked_vulkan_runtime_records_it_and_the_verifier_requires_it()
    {
        string script = Read("scripts", "build-corporate-lite.ps1");
        // pinned source, shared staging code, no ad-hoc download of the Vulkan archive
        Assert.Contains("stage-msi-runtime.ps1", script, StringComparison.Ordinal);
        Assert.Contains("\"llama.cpp-vulkan\"", script, StringComparison.Ordinal);
        Assert.Contains("windows-baseline-runtime.lock.json", script, StringComparison.Ordinal);
        Assert.DoesNotContain("win-vulkan-x64", script, StringComparison.Ordinal);
        Assert.Contains("differs from the locked inventory", script, StringComparison.Ordinal);
        // inventory/manifest
        foreach (string field in new[] { "vulkanRuntimeIncluded", "vulkanArchiveSha256", "vulkanNativeRuntimeSha256", "vulkanRuntimeSizeBytes", "vulkanRuntimeFileCount" })
            Assert.Contains(field, script, StringComparison.Ordinal);
        // finished-ZIP verification fails when the engine is missing or altered
        Assert.Contains("Bundled Vulkan runtime incomplete in ZIP", script, StringComparison.Ordinal);
        Assert.Contains("ggml-vulkan.dll", script, StringComparison.Ordinal);
        Assert.Contains("title = \"Vulkan runtime\"", script, StringComparison.Ordinal);
        Assert.Contains("Shipped Vulkan lock does not match", script, StringComparison.Ordinal);
        // README reports the size; IT launcher and variable are documented
        Assert.Contains("$vulkanSizeMb MB", script, StringComparison.Ordinal);
        Assert.Contains("Start PaRuX Corporate Lite (CPU only).cmd", script, StringComparison.Ordinal);
        Assert.Contains("PARUX_CORPORATE_GPU=0", script, StringComparison.Ordinal);
        Assert.Contains("The default launcher must not disable the GPU path", script, StringComparison.Ordinal);
        // the CPU shell is still built without the Standard Vulkan pack, and the CPU engine remains required
        Assert.Contains("-WithoutVulkan", script, StringComparison.Ordinal);
        Assert.Contains("Bundled CPU runtime executable missing from ZIP", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_core_never_downloads_or_terminates_anything_for_the_gpu_path_and_keeps_cpu_as_the_fallback()
    {
        string process = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        int start = process.IndexOf("TryStartCorporateVulkanAsync(", StringComparison.Ordinal);
        int end = process.IndexOf("private async Task<RuntimeResult> StartCorporateCpuTextAsync", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string gpuBlock = process[start..end];
        Assert.DoesNotContain("HttpClient", gpuBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Kill", gpuBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", gpuBlock, StringComparison.Ordinal);
        // the CPU launch follows the GPU attempt in the same method
        int cpuLaunch = process.IndexOf("BuildCorporateCpuArguments(model, TextPort", end, StringComparison.Ordinal);
        int gpuCall = process.IndexOf("await TryStartCorporateVulkanAsync(", end, StringComparison.Ordinal);
        Assert.True(gpuCall > 0 && cpuLaunch > gpuCall, "the GPU attempt must run before, and fall through to, the CPU launch");
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln"))) dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(path).ToArray())).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
