using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>CUDA 13.x driver/GPU floors and Blackwell (compute capability 12.0) handling in the backend advisor.
/// Floors come from NVIDIA's CUDA Toolkit release notes: 13.x minor-version compatibility is driver >= 580,
/// CUDA 12.8 on Windows needs >= 570.65 (Blackwell), CUDA 13 dropped pre-Turing GPUs.</summary>
public sealed class RuntimeBackendAdvisorCudaFloorTests
{
    private static readonly IReadOnlyDictionary<string, string> NoLatest = new Dictionary<string, string>();

    private static AdvisorGpu Gpu(string driver, double cc, string name = "NVIDIA GeForce RTX 5080") =>
        new(name, RuntimeBackendAdvisor.NvidiaVendor, 16L << 30, 0, driver, cc);

    private static List<RuntimeBackendSuggestion> Advise(AdvisorGpu gpu, params AdvisorRuntime[] installed) =>
        RuntimeBackendAdvisor.Advise(new AdvisorInput(true, new[] { gpu }, new AdvisorCpu(true, false), installed, NoLatest));

    private static AdvisorRuntime Cuda13() => new("text-cuda", "b11871", ComponentOwnership.PARUX_MANAGED, true, "13.4");

    [Fact]
    public void Installed_cuda13_build_on_a_driver_below_580_is_flagged_not_silently_ok()
    {
        // 32.0.15.6094 -> driver 560.94: fine for CUDA 12, below the CUDA 13.x floor of 580.
        RuntimeBackendSuggestion s = Assert.Single(Advise(Gpu("32.0.15.6094", 8.9), Cuda13()));

        Assert.Equal("none:cuda-driver-old", s.Id);
        Assert.Contains("CUDA 13", s.Why);
        Assert.Contains("580.00", s.Why);
        Assert.Contains(s.Evidence, e => e.Contains("13.4"));
    }

    [Fact]
    public void Installed_cuda13_build_on_the_real_rtx5080_driver_is_ok()
    {
        // 32.0.16.2124 -> 621.24, compute capability 12.0 (this PC).
        Assert.Empty(Advise(Gpu("32.0.16.2124", 12.0), Cuda13()));
    }

    [Fact]
    public void Installed_cuda13_build_on_a_pre_turing_gpu_is_flagged()
    {
        RuntimeBackendSuggestion s = Assert.Single(Advise(Gpu("32.0.16.2124", 6.1, "NVIDIA GeForce GTX 1080"), Cuda13()));

        Assert.Equal("none:cuda-unsupported-gpu", s.Id);
        Assert.Contains("7.5", s.Why);
    }

    [Fact]
    public void A_cuda12_build_still_accepts_a_pre_turing_gpu_and_the_old_driver_floor()
    {
        var cuda12 = new AdvisorRuntime("text-cuda", "b10549", ComponentOwnership.PARUX_MANAGED, true, "12.4");
        Assert.Empty(Advise(Gpu("32.0.15.6094", 6.1, "NVIDIA GeForce GTX 1080"), cuda12));
    }

    [Fact]
    public void Toolkit_is_read_from_the_build_folder_name_when_not_given_explicitly()
    {
        var byName = new AdvisorRuntime("text-cuda", "llama.cpp-b11871-cuda13.4", ComponentOwnership.PARUX_MANAGED, true);
        RuntimeBackendSuggestion s = Assert.Single(Advise(Gpu("32.0.15.6094", 8.9), byName));
        Assert.Equal("none:cuda-driver-old", s.Id);
    }

    [Fact]
    public void Blackwell_needs_the_570_65_release_driver_even_on_a_cuda12_build()
    {
        var cuda12 = new AdvisorRuntime("text-cuda", "b10549", ComponentOwnership.PARUX_MANAGED, true, "12.4");
        // 32.0.15.5612 -> 556.12, below Blackwell's 570.65.
        RuntimeBackendSuggestion s = Assert.Single(Advise(Gpu("32.0.15.5612", 12.0), cuda12));

        Assert.Equal("none:cuda-driver-old", s.Id);
        Assert.Contains("570.65", s.Why);
        Assert.Contains("Blackwell", s.Why);
    }

    [Fact]
    public void Install_suggestion_for_blackwell_with_driver_580_plus_targets_cuda13_floor_and_is_offered()
    {
        RuntimeBackendSuggestion s = Assert.Single(Advise(Gpu("32.0.16.2124", 12.0)));
        Assert.Equal("switch:text-cuda", s.Id);
        Assert.Contains(s.Evidence, e => e.Contains("compute capability: 12.0"));
    }

    [Fact]
    public void Blackwell_with_driver_between_570_65_and_580_gets_the_cuda12_build_offered()
    {
        // 32.0.15.7500 -> 575.00: below the CUDA 13 floor, above Blackwell's, so the CUDA 12 build (PTX JIT) is what is suggested.
        RuntimeBackendSuggestion s = Assert.Single(Advise(Gpu("32.0.15.7500", 12.0)));
        Assert.Equal("switch:text-cuda", s.Id);
    }

    [Theory]
    [InlineData("13.4", 13)]
    [InlineData("12.4", 12)]
    [InlineData(null, null)]
    [InlineData("x", null)]
    public void CudaMajorOf_parses_toolkits(string? toolkit, int? expected) =>
        Assert.Equal(expected, RuntimeBackendAdvisor.CudaMajorOf(toolkit));

    [Fact]
    public void Floors_match_the_documented_values()
    {
        Assert.Equal(580.0, RuntimeBackendAdvisor.MinimumDriverFor(13, 8.9));
        Assert.Equal(527.41, RuntimeBackendAdvisor.MinimumDriverFor(12, 8.9));
        Assert.Equal(570.65, RuntimeBackendAdvisor.MinimumDriverFor(12, 12.0));
        Assert.Equal(580.0, RuntimeBackendAdvisor.MinimumDriverFor(13, 12.0));
        Assert.Equal(7.5, RuntimeBackendAdvisor.MinimumComputeCapabilityFor(13));
        Assert.Equal(5.0, RuntimeBackendAdvisor.MinimumComputeCapabilityFor(12));
    }
}
