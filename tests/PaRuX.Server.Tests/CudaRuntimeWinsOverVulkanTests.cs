using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// An NVIDIA machine with a CUDA runtime installed must launch CUDA even when runtime-build-config.json only records
/// text-vulkan (measured on a portable copy after an automatic Vulkan runtime update: it launched Vulkan, about 20% slower decode).
/// </summary>
public sealed class CudaRuntimeWinsOverVulkanTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-cuda-wins-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _state;
    private readonly IDisposable _root;

    public CudaRuntimeWinsOverVulkanTests()
    {
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "root"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    private static string Install(string backend, string tag, string? toolkit = null)
    {
        string dir = Path.Combine(LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName(backend, tag, toolkit));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, LocalAiPaths.LlamaServerExecutable), "x");
        return dir;
    }

    [Fact]
    public void Only_text_vulkan_configured_but_a_cuda_runtime_installed_resolves_to_cuda()
    {
        string cuda = Install("text-cuda", "b12233", "13.5");
        string vulkan = Install("text-vulkan", "b12631");
        LocalAiPaths.SetActiveRuntimeBuildTag("text-vulkan", "b12631");
        Assert.Null(LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda"));

        Assert.Equal(cuda, LocalAiPaths.RuntimeCudaPath);
        Assert.Equal(cuda, LocalAiPaths.ResolveGpuTextRuntimePath(preferCuda: true));
        Assert.Equal(vulkan, LocalAiPaths.RuntimeVulkanPath);
        Assert.Equal("b12233", LocalAiPaths.EffectiveCudaBuildTag());
    }

    [Fact]
    public void The_newest_installed_cuda_build_is_chosen_when_none_is_configured()
    {
        Install("text-cuda", "b12233", "13.5");
        string newer = Install("text-cuda", "b12600", "13.5");
        Install("text-vulkan", "b12631");
        Assert.Equal(newer, LocalAiPaths.RuntimeCudaPath);
        Assert.Equal("b12600", LocalAiPaths.EffectiveCudaBuildTag());
    }

    [Fact]
    public void A_configured_cuda_tag_still_wins_over_a_newer_installed_one()
    {
        Install("text-cuda", "b12233", "13.5");
        Install("text-cuda", "b12600", "13.5");
        LocalAiPaths.SetCudaToolkitHint("13.5");
        LocalAiPaths.SetActiveRuntimeBuildTag("text-cuda", "b12233", "13.5");
        Assert.EndsWith("llama.cpp-b12233-cuda13.5", LocalAiPaths.RuntimeCudaPath);
        Assert.Equal("b12233", LocalAiPaths.EffectiveCudaBuildTag());
    }

    [Fact]
    public void Nothing_installed_keeps_naming_the_default_cuda_folder()
    {
        Assert.EndsWith("llama.cpp-" + LocalAiPaths.DefaultCudaBuildTag + "-cuda12.4", LocalAiPaths.RuntimeCudaPath);
        Assert.Equal(LocalAiPaths.DefaultCudaBuildTag, LocalAiPaths.EffectiveCudaBuildTag());
    }

    [Fact]
    public async Task The_first_install_of_a_cuda_runtime_writes_the_cuda_tag_through_the_install_guard()
    {
        // The install flow (RuntimeInstallService) hands a freshly extracted build to this guard; with no previous CUDA build
        // installed the guard must activate it. The default-tag folder does not exist here, which is the reproduction case.
        Install("text-vulkan", "b12631");
        LocalAiPaths.SetActiveRuntimeBuildTag("text-vulkan", "b12631");
        Install("text-cuda", "b12233", "13.5");
        LocalAiPaths.SetCudaToolkitHint("13.5");

        var guard = new RuntimeUpdateRegressionGuard(
            new LocalAiProcessManager(0),
            new ThrowingProbe(),
            new RuntimeRegressionEvidenceStore(Path.Combine(_work, "state", "evidence.json")));
        RuntimeRegressionEvidenceDto evidence = await guard.EvaluateAsync(
            "text-cuda", LocalAiPaths.DefaultCudaBuildTag, "b12233", CancellationToken.None);

        Assert.Equal("first-install", evidence.Decision);
        Assert.Equal("b12233", LocalAiPaths.GetActiveRuntimeBuildTag("text-cuda"));
        Assert.EndsWith("llama.cpp-b12233-cuda13.5", LocalAiPaths.RuntimeCudaPath);
        Assert.Equal(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.ResolveGpuTextRuntimePath(preferCuda: true));
    }

    private sealed class ThrowingProbe : ILlamaPerformanceProbe
    {
        public Task<LlamaPerformanceProbeMeasurement> RunAsync(
            string modelPath, LlamaPerformanceProfileDto profile, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A first install must not run a comparison probe.");
    }
}
