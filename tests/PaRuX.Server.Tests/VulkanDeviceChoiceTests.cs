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
    public void Insufficient_free_vram_moves_to_the_device_that_fits_and_says_so_when_none_do()
    {
        var devices = VulkanDeviceSelector.ParseDevices(
            "  Vulkan0: AMD Radeon 780M Graphics (19359 MiB, 18391 MiB free)\r\n  Vulkan1: NVIDIA GeForce RTX 5080 (16193 MiB, 3000 MiB free)\r\n");
        // 3 GB free on the card (a game is open): a 6 GB model fits the iGPU's shared memory instead.
        Assert.Equal("Vulkan0", VulkanDeviceSelector.Select(devices, Laptop, requiredMib: 6000).Device);
        // A model that fits nowhere keeps the best device, and the reason says layers are fitted.
        var none = VulkanDeviceSelector.Select(devices, Laptop, requiredMib: 40000);
        Assert.Equal("Vulkan1", none.Device);
        Assert.Contains("no device reports enough free memory", none.Reason);
        // A model that fits the card keeps the card.
        Assert.Equal("Vulkan1", VulkanDeviceSelector.Select(devices, Laptop, requiredMib: 2000).Device);
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
