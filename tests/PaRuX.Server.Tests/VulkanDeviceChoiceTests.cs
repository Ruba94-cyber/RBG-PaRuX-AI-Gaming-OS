using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

[CollectionDefinition("VulkanDeviceStatic", DisableParallelization = true)]
public sealed class VulkanDeviceStaticCollection;

/// <summary>Device choice with fake device lists: iGPU-first ordering, NVIDIA plus AMD, insufficient VRAM, probe failure, override.</summary>
[Collection("VulkanDeviceStatic")]
public sealed class VulkanDeviceChoiceTests : IDisposable
{
    private const string IgpuFirst =
        "  Vulkan0: AMD Radeon 780M Graphics (19359 MiB, 18391 MiB free)\r\n" +
        "  Vulkan1: NVIDIA GeForce RTX 5080 (16193 MiB, 15424 MiB free)\r\n";

    public VulkanDeviceChoiceTests() => VulkanDeviceSelector.ResetForTests();
    public void Dispose() => VulkanDeviceSelector.ResetForTests();

    private static GpuInventoryStatus Inventory(params (string Name, long DedicatedBytes)[] adapters) => new()
    {
        Available = true,
        Adapters = adapters.Select((a, i) => new GpuAdapterStatus { Index = i, Name = a.Name, DedicatedVideoMemoryBytes = a.DedicatedBytes }).ToList()
    };

    private static readonly GpuInventoryStatus Laptop = Inventory(("AMD Radeon(TM) 780M Graphics", 512L << 20), ("NVIDIA GeForce RTX 5080", 16L << 30));

    [Fact]
    public void Igpu_listed_first_loses_to_the_discrete_card_and_the_reason_names_both()
    {
        var choice = VulkanDeviceSelector.Select(VulkanDeviceSelector.ParseDevices(IgpuFirst), Laptop);
        Assert.Equal("Vulkan1", choice.Device);
        Assert.False(choice.CpuFallback);
        Assert.Contains("RTX 5080", choice.Reason);
        Assert.Contains("780M", choice.Reason);
    }

    [Fact]
    public void Nvidia_plus_amd_discrete_picks_the_one_with_more_dedicated_memory()
    {
        var devices = VulkanDeviceSelector.ParseDevices(
            "  Vulkan0: AMD Radeon RX 6600 (8176 MiB, 8000 MiB free)\r\n  Vulkan1: NVIDIA GeForce RTX 4090 (24564 MiB, 24000 MiB free)\r\n");
        var inventory = Inventory(("AMD Radeon RX 6600", 8L << 30), ("NVIDIA GeForce RTX 4090", 24L << 30));
        Assert.Equal("Vulkan1", VulkanDeviceSelector.Select(devices, inventory).Device);
    }

    [Fact]
    public void A_useful_discrete_card_stays_chosen_even_when_the_model_does_not_fit_it()
    {
        var devices = VulkanDeviceSelector.ParseDevices(
            "  Vulkan0: AMD Radeon 780M Graphics (19359 MiB, 18391 MiB free)\r\n  Vulkan1: NVIDIA GeForce RTX 5080 (16193 MiB, 3000 MiB free)\r\n");
        // Partial offload to the 5080 beats running on the iGPU alone, whatever the free memory (a game is open, or the model is ~16 GB).
        Assert.Equal("Vulkan1", VulkanDeviceSelector.Select(devices, Laptop, requiredMib: 6000).Device);
        var big = VulkanDeviceSelector.Select(devices, Laptop, requiredMib: 17000);
        Assert.Equal("Vulkan1", big.Device);
        Assert.Contains("layers are fitted", big.Reason);
        Assert.Equal("Vulkan1", VulkanDeviceSelector.Select(devices, Laptop, requiredMib: 2000).Device);
    }

    [Fact]
    public void Fit_chooses_among_discrete_cards_and_decides_between_a_tiny_dgpu_and_the_igpu()
    {
        var two = VulkanDeviceSelector.ParseDevices(
            "  Vulkan0: AMD Radeon RX 7600 (8176 MiB, 7000 MiB free)\r\n  Vulkan1: NVIDIA GeForce RTX 4090 (24564 MiB, 2000 MiB free)\r\n");
        var inv = Inventory(("AMD Radeon RX 7600", 8L << 30), ("NVIDIA GeForce RTX 4090", 24L << 30));
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Select(two, inv, requiredMib: 6000).Device); // only the 7600 has room

        var tiny = VulkanDeviceSelector.ParseDevices(
            "  Vulkan0: AMD Radeon 780M Graphics (19359 MiB, 18391 MiB free)\r\n  Vulkan1: NVIDIA GeForce MX450 (2048 MiB, 1900 MiB free)\r\n");
        var tinyInv = Inventory(("AMD Radeon(TM) 780M Graphics", 512L << 20), ("NVIDIA GeForce MX450", 2L << 30));
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Select(tiny, tinyInv, requiredMib: 6000).Device); // the 2 GB card cannot hold it
    }

    [Fact]
    public async Task Free_memory_is_re_read_after_the_listing_goes_stale_and_the_cache_follows_the_runtime()
    {
        int listings = 0;
        VulkanDeviceSelector.ListingFreshness = TimeSpan.Zero;
        VulkanDeviceSelector.ListDevicesOverrideForTests = (_, _) =>
        {
            listings++;
            return Task.FromResult("  Vulkan0: AMD Radeon 780M Graphics (19359 MiB, 18391 MiB free)\r\n  Vulkan1: NVIDIA GeForce RTX 5080 (16193 MiB, " + (listings == 1 ? 15000 : 500) + " MiB free)\r\n");
        };
        await VulkanDeviceSelector.ChooseAsync(Laptop, 8000, null, CancellationToken.None);
        await Task.Delay(20);
        await VulkanDeviceSelector.ChooseAsync(Laptop, 8000, null, CancellationToken.None);
        Assert.Equal(2, listings); // a stale listing is not reused for the fit decision
    }

    [Fact]
    public void Only_fast_device_failures_blacklist_a_device_and_a_healthy_launch_clears_it()
    {
        Assert.True(VulkanDeviceSelector.IsDeviceFailure("ggml_vulkan: out of memory while allocating"));
        Assert.True(VulkanDeviceSelector.IsDeviceFailure("llama-server exited before it became healthy"));
        Assert.False(VulkanDeviceSelector.IsDeviceFailure("The runtime did not become healthy: timed out after 5 minutes"));
        Assert.False(VulkanDeviceSelector.IsDeviceFailure("Launch cancelled"));
        Assert.False(VulkanDeviceSelector.IsDeviceFailure("A game is currently active"));
        Assert.False(VulkanDeviceSelector.IsDeviceFailure("model too large for this device"));
        Assert.False(VulkanDeviceSelector.IsDeviceFailure(null));

        var devices = VulkanDeviceSelector.ParseDevices(IgpuFirst);
        VulkanDeviceSelector.MarkFailed("Vulkan1");
        VulkanDeviceSelector.ListDevicesOverrideForTests = (_, _) => Task.FromResult(IgpuFirst);
        Assert.Equal("Vulkan0", VulkanDeviceSelector.ChooseAsync(Laptop, 0, null, CancellationToken.None).Result.Device);
        VulkanDeviceSelector.ClearFailed();
        Assert.Equal("Vulkan1", VulkanDeviceSelector.ChooseAsync(Laptop, 0, null, CancellationToken.None).Result.Device);
        Assert.NotEmpty(devices);
    }

    [Fact]
    public void When_every_device_is_blacklisted_the_argument_builders_fall_back_to_cpu_arguments()
    {
        VulkanDeviceSelector.ListDevicesOverrideForTests = (_, _) => Task.FromResult(IgpuFirst);
        VulkanDeviceSelector.InventoryOverrideForTests = () => Laptop;
        VulkanDeviceSelector.MarkFailed("Vulkan0");
        VulkanDeviceSelector.MarkFailed("Vulkan1");

        var profile = new LlamaPerformanceProfileDto { GpuLayers = -1, ContextSize = 8192 };
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: false);
        Assert.DoesNotContain("--device", args);
        Assert.DoesNotContain("Vulkan0", args);

        var ladder = TextLaunchLadder.ArgumentsFor(
            new TextLaunchLadder.Rung(TextLaunchLadder.Step.VulkanKeepFlash, TextLaunchLadder.Engine.Vulkan, "Vulkan", ""),
            ["--model", "m.gguf", "--gpu-layers", "all", "--device", "CUDA0"]);
        Assert.DoesNotContain("--device", ladder);
        Assert.DoesNotContain("Vulkan0", ladder);
    }

    [Fact]
    public void A_device_that_fails_the_health_probe_is_never_chosen()
    {
        var devices = VulkanDeviceSelector.ParseDevices(IgpuFirst);
        var choice = VulkanDeviceSelector.Select(devices, Laptop, isHealthy: d => d.Index != 1);
        Assert.Equal("Vulkan0", choice.Device);
        Assert.Contains("failed the health probe", choice.Reason);
    }

    [Fact]
    public void When_every_device_fails_the_probe_the_result_is_a_cpu_fallback_not_a_bad_device()
    {
        var choice = VulkanDeviceSelector.Select(VulkanDeviceSelector.ParseDevices(IgpuFirst), Laptop, isHealthy: _ => false);
        Assert.Null(choice.Device);
        Assert.True(choice.CpuFallback);
        Assert.Contains("CPU", choice.Reason);
    }

    [Fact]
    public void An_explicit_user_device_always_wins_even_over_the_probe_and_the_ranking()
    {
        var choice = VulkanDeviceSelector.Select(VulkanDeviceSelector.ParseDevices(IgpuFirst), Laptop, userDevice: " Vulkan0 ", isHealthy: _ => false);
        Assert.Equal("Vulkan0", choice.Device);
        Assert.Contains("explicitly", choice.Reason);
    }

    [Fact]
    public void Single_gpu_machines_are_unchanged_and_need_no_probe()
    {
        var one = VulkanDeviceSelector.ParseDevices("  Vulkan0: NVIDIA GeForce RTX 5080 (16193 MiB, 15424 MiB free)");
        var choice = VulkanDeviceSelector.Select(one, Laptop, requiredMib: 99999, isHealthy: _ => false);
        Assert.Equal("Vulkan0", choice.Device);
        Assert.False(choice.CpuFallback);
    }

    [Fact]
    public void An_integrated_only_machine_keeps_the_first_device()
    {
        var devices = VulkanDeviceSelector.ParseDevices("  Vulkan0: AMD Radeon 780M Graphics (19359 MiB, 18391 MiB free)\r\n  Vulkan1: Intel(R) UHD Graphics (8000 MiB, 7000 MiB free)\r\n");
        var inventory = Inventory(("AMD Radeon(TM) 780M Graphics", 512L << 20), ("Intel(R) UHD Graphics", 128L << 20));
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Select(devices, inventory).Device);
    }

    [Fact]
    public async Task ChooseAsync_uses_the_listing_marks_failures_and_honours_the_environment_override()
    {
        int listings = 0;
        VulkanDeviceSelector.ListDevicesOverrideForTests = (_, _) => { listings++; return Task.FromResult(IgpuFirst); };
        Assert.Equal("Vulkan1", (await VulkanDeviceSelector.ChooseAsync(Laptop, 0, null, CancellationToken.None)).Device);
        Assert.Equal("Vulkan1", (await VulkanDeviceSelector.ChooseAsync(Laptop, 0, null, CancellationToken.None)).Device);
        Assert.Equal(1, listings); // cached

        VulkanDeviceSelector.MarkFailed("Vulkan1");
        Assert.Equal("Vulkan0", (await VulkanDeviceSelector.ChooseAsync(Laptop, 0, null, CancellationToken.None)).Device);

        Assert.Equal("Vulkan1", (await VulkanDeviceSelector.ChooseAsync(Laptop, 0, "Vulkan1", CancellationToken.None)).Device);
        string? previous = Environment.GetEnvironmentVariable(VulkanDeviceSelector.UserDeviceEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(VulkanDeviceSelector.UserDeviceEnvironmentVariable, "Vulkan0");
            VulkanDeviceSelector.ResetForTests();
            Assert.Equal("Vulkan0", (await VulkanDeviceSelector.ChooseAsync(Laptop, 0, null, CancellationToken.None)).Device);
        }
        finally { Environment.SetEnvironmentVariable(VulkanDeviceSelector.UserDeviceEnvironmentVariable, previous); }
    }

    [Fact]
    public async Task A_failing_listing_keeps_vulkan0_with_an_honest_reason()
    {
        VulkanDeviceSelector.ListDevicesOverrideForTests = (_, _) => throw new InvalidOperationException("boom");
        var choice = await VulkanDeviceSelector.ChooseAsync(Laptop, 0, null, CancellationToken.None);
        Assert.Equal("Vulkan0", choice.Device);
        Assert.True(choice.UsedFallback);
        Assert.Contains("unavailable", choice.Reason);
    }

    [Fact]
    public async Task The_adaptive_vulkan_profile_gets_the_discrete_device_and_the_reason_for_a_dgpu_plus_igpu_laptop()
    {
        VulkanDeviceSelector.ListDevicesOverrideForTests = (_, _) => Task.FromResult(IgpuFirst);
        string model = Path.Combine(Path.GetTempPath(), "parux-device-test-" + Guid.NewGuid().ToString("N") + ".gguf");
        File.WriteAllBytes(model, new byte[1024]);
        try
        {
            RuntimeLaunchOptimization result = await RuntimeLaunchOptimizer.ResolveAsync(
                model, "chat", hasCuda: false, 512, 128, "q8_0", CancellationToken.None, Laptop, planLaunch: false);
            Assert.Equal("Vulkan1", result.Profile.Device);
            Assert.Contains("RTX 5080", result.DeviceReason);
            List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(result.Profile, hasCuda: false);
            Assert.Equal("Vulkan1", args[args.IndexOf("--device") + 1]);
            Assert.Single(args, a => a == "--device");
        }
        finally { File.Delete(model); }
    }
}
