using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-only gates for the retirement of the Standard Portable ZIP. PaRuX has two distributions: the MSI and
/// Corporate Lite. The layered data-root detection stays (Corporate Lite uses it) and the retired
/// <c>parux.portable</c> marker is still recognised read-only so an old extracted package keeps its PaRuXData.
/// A green assertion is NOT a substitute for Windows publish or clean-PC acceptance.
/// </summary>
public sealed class PortableStandaloneSourceContractTests
{
    [Fact]
    public void Layered_zip_detects_a_colocated_root_without_a_batch_launcher()
    {
        string data = Read("src", "PaRuX.Contracts", "PaRuXDataRoot.cs");
        string state = Read("src", "PaRuX.Contracts", "PaRuXLocalAiStateRoot.cs");
        string desktop = Read("src", "PaRuX.App", "Services", "AppPaths.cs");
        string core = Read("src", "PaRuX.Server", "LocalAiPaths.cs");
        Assert.Contains("public const string LegacyStandardPortableMarkerFileName", data, StringComparison.Ordinal);
        Assert.Contains("AppContext.BaseDirectory", data, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(baseDirectory)", data, StringComparison.Ordinal);
        Assert.Contains("CorporateMode.MarkerFileName", data, StringComparison.Ordinal);
        Assert.Contains("Resolve(AppContext.BaseDirectory", state, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(baseDirectory)", state, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(AppContext.BaseDirectory)", desktop, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(AppContext.BaseDirectory)", core, StringComparison.Ordinal);
        Assert.Contains("return Path.Combine(portableData, \"LocalAI\")", core, StringComparison.Ordinal);
        Assert.Contains("return Path.Combine(portableData, \"LocalAI\")", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Standard_Portable_builder_verifier_and_asset_resolver_are_gone()
    {
        Assert.False(File.Exists(RepoPath("scripts", "build-portable.ps1")));
        Assert.False(File.Exists(RepoPath("tests", "portable", "verify-standard-portable.ps1")));
        string assets = Read("scripts", "portable-offline-assets.ps1");
        Assert.DoesNotContain("Resolve-PaRuXPortableOfflineAssets", assets, StringComparison.Ordinal);
        Assert.Contains("Copy-PaRuXVcRuntime", assets, StringComparison.Ordinal);
        // The release gate and the packaging scripts never look for the retired asset.
        foreach (string file in new[] { "tools/release-gate.ps1", "tools/check-direct-third-party-coverage.ps1",
                     "scripts/verify-release-assets.ps1", "scripts/build-msi.ps1", "scripts/build-corporate-lite.ps1" })
        {
            string text = File.ReadAllText(Path.Combine(RepoPath(), file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("portable-win-x64", text, StringComparison.Ordinal);
            Assert.DoesNotContain("build-portable.ps1", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Corporate_packager_uses_the_internal_CPU_shell_stage()
    {
        string corporate = Read("scripts", "build-corporate-lite.ps1");
        string shell = Read("scripts", "build-corporate-app-shell.ps1");
        Assert.Contains("build-corporate-app-shell.ps1", corporate, StringComparison.Ordinal);
        Assert.Contains("app-shell-stage", corporate, StringComparison.Ordinal);
        Assert.Contains("app-shell-stage", shell, StringComparison.Ordinal);
        Assert.Contains(".parux-publish-source", shell, StringComparison.Ordinal);
        Assert.Contains(".parux-publish-source", corporate, StringComparison.Ordinal);
        Assert.Contains("|cpu-shell", shell, StringComparison.Ordinal);
        Assert.Contains("|cpu-shell", corporate, StringComparison.Ordinal);
        Assert.Contains("compiled from a different commit or edition", corporate, StringComparison.Ordinal);
        Assert.Contains("staged source commit/edition differs", shell, StringComparison.Ordinal);
        // The shell never creates an edition marker or a data folder; Corporate Lite adds its own.
        Assert.DoesNotContain("Set-Content -LiteralPath (Join-Path $appDir \"parux.portable\"", shell, StringComparison.Ordinal);
        Assert.Contains("parux.corporate", corporate, StringComparison.Ordinal);
    }

    [Fact]
    public void Retired_marker_is_still_recognised_read_only_and_the_update_channel_is_closed()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-legacy-portable-" + Guid.NewGuid().ToString("N"));
        try
        {
            string app = Path.Combine(root, "app");
            Directory.CreateDirectory(app);
            Directory.CreateDirectory(Path.Combine(root, "PaRuXData"));
            File.WriteAllText(Path.Combine(app, PaRuXDataRoot.LegacyStandardPortableMarkerFileName), "legacy");
            // Existing data stays reachable (no data loss) ...
            Assert.Equal(Path.Combine(root, "PaRuXData"), PaRuXDataRoot.FindPortableDataRoot(app));
            // ... and the copy is identified so the in-app updater explains that the channel is closed.
            Assert.True(PaRuXDataRoot.IsLegacyStandardPortable(app));

            File.WriteAllText(Path.Combine(app, CorporateMode.MarkerFileName), "corporate");
            Assert.False(PaRuXDataRoot.IsLegacyStandardPortable(app));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }

        string boundary = Read("src", "PaRuX.App", "Services", "AppUpdateInstallBoundary.cs");
        string client = Read("src", "PaRuX.App", "Services", "AppUpdateClient.cs");
        Assert.Contains("StandardPortableClosedMessage", boundary, StringComparison.Ordinal);
        Assert.Contains("PaRuXData", boundary, StringComparison.Ordinal);
        Assert.Contains("StandardPortableClosedMessage(AppContext.BaseDirectory)", client, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_copy_still_uses_native_CPU_fallback_when_Vulkan_or_GPU_is_unusable()
    {
        string ai = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        string paths = Read("src", "PaRuX.Server", "LocalAiPaths.cs");
        Assert.Contains("cpuOnly && selfContainedPortable", ai, StringComparison.Ordinal);
        Assert.Contains("FindBundledPortableModel()", ai, StringComparison.Ordinal);
        Assert.Contains("FindBundledPortableModel()", paths, StringComparison.Ordinal);
        Assert.Contains("GetPortableOwnedRoleModel(\"standard\")", ai, StringComparison.Ordinal);
        Assert.Contains("GetPortableOwnedRoleModel", paths, StringComparison.Ordinal);
    }

    [Fact]
    public void First_run_detects_included_CPU_and_starter_without_suggesting_another_download()
    {
        string onboard = Read("src", "PaRuX.App", "Views", "OnboardingView.xaml.cs");
        string firstModel = Read("src", "PaRuX.App", "Views", "OnboardingView.FirstModel.cs");
        string runtime = Read("src", "PaRuX.Server", "RuntimeManagerService.cs");
        string components = Read("src", "PaRuX.Server", "ComponentInventoryService.cs");
        Assert.Contains("components, corporate, standalonePortable", onboard, StringComparison.Ordinal);
        Assert.Contains("runtimeStatus.Backends, corporate, standalonePortable", onboard, StringComparison.Ordinal);
        Assert.Contains("standardPortableMode", Read("src", "PaRuX.App", "Services",
            "OnboardingTextReadinessPolicy.cs"), StringComparison.Ordinal);
        Assert.Contains("HasBundledStandardPortableStarter", firstModel, StringComparison.Ordinal);
        Assert.Contains("No model download is required", firstModel, StringComparison.Ordinal);
        Assert.Contains("text-cpu", runtime, StringComparison.Ordinal);
        Assert.Contains("Bundled Portable CPU runtime", components, StringComparison.Ordinal);
    }

    private static string RepoPath(params string[] relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(new[] { root!.FullName }.Concat(relative).ToArray());
    }

    private static string Read(params string[] relative) => File.ReadAllText(RepoPath(relative));
}
