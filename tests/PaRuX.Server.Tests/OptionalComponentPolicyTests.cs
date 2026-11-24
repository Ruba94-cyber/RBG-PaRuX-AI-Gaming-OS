using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure policies behind the Optional components page: states, legal buttons, download pre-flight, removal and the
/// integrity/resume rules the Runtime Manager download relies on. No network, no UI.</summary>
public sealed class OptionalComponentPolicyTests
{
    private static readonly OptionalComponentsContext En = new(ExecutableDownloadsAllowed: true, Italian: false);
    private static readonly OptionalComponentsContext It = new(ExecutableDownloadsAllowed: true, Italian: true);

    private static ComponentStatus Managed(string id, bool installed, bool skipped = false, params string[] actions) => new()
    {
        Id = id, DisplayName = id, Ownership = ComponentOwnership.PARUX_MANAGED, Detected = installed, Skipped = skipped, Actions = actions
    };

    private static OptionalComponentCatalogEntry Entry(string id) => OptionalComponentPolicy.Catalog.Single(c => c.Id == id);

    [Fact]
    public void State_is_installed_not_installed_or_detected_on_this_pc()
    {
        Assert.Equal(OptionalComponentState.Installed, OptionalComponentPolicy.ResolveState(Managed("llama-server-vulkan", true)));
        Assert.Equal(OptionalComponentState.NotInstalled, OptionalComponentPolicy.ResolveState(Managed("llama-server-vulkan", false)));
        Assert.Equal(OptionalComponentState.DetectedOnThisPc, OptionalComponentPolicy.ResolveState(
            new ComponentStatus { Id = "git", Ownership = ComponentOwnership.SYSTEM_DETECTED, Detected = true }));
        Assert.Equal(OptionalComponentState.Installed, OptionalComponentPolicy.ResolveState(
            new ComponentStatus { Id = "git", Ownership = ComponentOwnership.USER_MANAGED, Detected = true }));
        Assert.Equal(OptionalComponentState.NotInstalled, OptionalComponentPolicy.ResolveState(
            new ComponentStatus { Id = "git", Ownership = ComponentOwnership.SYSTEM_DETECTED, Detected = false }));
    }

    [Fact]
    public void Missing_managed_engine_offers_download_and_skip_but_never_remove()
    {
        var buttons = OptionalComponentPolicy.ButtonsFor(Entry("llama-server-cuda"),
            Managed("llama-server-cuda", false, false, "install", "skip"), En);
        Assert.Equal(new[] { OptionalComponentButton.DownloadNow, OptionalComponentButton.Skip }, buttons);
    }

    [Fact]
    public void Skipped_component_offers_undo_skip_and_stays_downloadable()
    {
        var buttons = OptionalComponentPolicy.ButtonsFor(Entry("llama-server-vulkan"),
            Managed("llama-server-vulkan", false, true, "install", "unskip"), En);
        Assert.Contains(OptionalComponentButton.UndoSkip, buttons);
        Assert.Contains(OptionalComponentButton.DownloadNow, buttons);
        Assert.DoesNotContain(OptionalComponentButton.Skip, buttons);
    }

    [Fact]
    public void Store_edition_never_offers_download_and_explains_why()
    {
        var ctx = new OptionalComponentsContext(ExecutableDownloadsAllowed: false, Italian: false);
        var inventory = new[] { Managed("llama-server-cuda", false, false, "install", "skip") };
        Assert.DoesNotContain(OptionalComponentButton.DownloadNow, OptionalComponentPolicy.BuildRows(inventory, ctx).Single().Buttons);
        Assert.Equal(StoreEditionPolicy.ExecutableDownloadsReason, OptionalComponentPolicy.BuildRows(inventory, ctx).Single().Note);
    }

    [Fact]
    public void Remove_exists_only_for_an_installed_paRuX_managed_removable_component()
    {
        Assert.Contains(OptionalComponentButton.Remove, OptionalComponentPolicy.ButtonsFor(Entry("llama-server-vulkan"),
            Managed("llama-server-vulkan", true, false, "install", "rollback"), En));
        // Not installed: nothing to remove.
        Assert.DoesNotContain(OptionalComponentButton.Remove, OptionalComponentPolicy.ButtonsFor(Entry("llama-server-vulkan"),
            Managed("llama-server-vulkan", false, false, "install", "skip"), En));
        // ComfyUI holds user outputs: PaRuX does not offer deletion in this slice.
        Assert.DoesNotContain(OptionalComponentButton.Remove, OptionalComponentPolicy.ButtonsFor(Entry("comfyui"),
            Managed("comfyui", true, false, "relocate", "remove", "install"), En));
        // Software the owner chose or the system provides is never removable.
        foreach (ComponentOwnership owner in new[] { ComponentOwnership.USER_MANAGED, ComponentOwnership.SYSTEM_DETECTED })
        {
            var status = new ComponentStatus { Id = "git", Ownership = owner, Detected = true, Actions = new[] { "relocate", "remove", "locate" } };
            Assert.DoesNotContain(OptionalComponentButton.Remove, OptionalComponentPolicy.ButtonsFor(Entry("git"), status, En));
            Assert.DoesNotContain(OptionalComponentButton.DownloadNow, OptionalComponentPolicy.ButtonsFor(Entry("git"), status, En));
        }
    }

    [Fact]
    public void User_managed_location_can_be_forgotten_but_never_skipped_downloaded_or_deleted()
    {
        var status = new ComponentStatus { Id = "ffmpeg", Ownership = ComponentOwnership.USER_MANAGED, Detected = true, Actions = new[] { "relocate", "remove" } };
        var buttons = OptionalComponentPolicy.ButtonsFor(Entry("ffmpeg"), status, En);
        Assert.Equal(new[] { OptionalComponentButton.LocateExisting, OptionalComponentButton.ForgetLocation }, buttons);
    }

    [Fact]
    public void System_detected_tool_offers_locate_and_skip_only_when_missing()
    {
        var found = new ComponentStatus { Id = "git", Ownership = ComponentOwnership.SYSTEM_DETECTED, Detected = true, Actions = new[] { "locate", "skip" } };
        Assert.Equal(new[] { OptionalComponentButton.LocateExisting }, OptionalComponentPolicy.ButtonsFor(Entry("git"), found, En));
        var missing = new ComponentStatus { Id = "git", Ownership = ComponentOwnership.SYSTEM_DETECTED, Detected = false, Actions = new[] { "locate", "skip" } };
        Assert.Equal(new[] { OptionalComponentButton.LocateExisting, OptionalComponentButton.Skip }, OptionalComponentPolicy.ButtonsFor(Entry("git"), missing, En));
    }

    [Fact]
    public void On_demand_python_tools_have_no_buttons()
    {
        Assert.Empty(OptionalComponentPolicy.ButtonsFor(Entry("uv"), Managed("uv", false, false, "install", "skip"), En));
        Assert.Empty(OptionalComponentPolicy.ButtonsFor(Entry("python"), Managed("python", true, false, "remove"), En));
    }

    [Fact]
    public void Rows_follow_catalog_order_ignore_core_engines_and_have_unique_ids()
    {
        var inventory = new[]
        {
            new ComponentStatus { Id = "llama-server-cpu", Ownership = ComponentOwnership.PARUX_MANAGED, Detected = true },
            Managed("comfyui", false, false, "install", "locate", "skip"),
            Managed("llama-server-vulkan", false, false, "install", "skip")
        };
        var rows = OptionalComponentPolicy.BuildRows(inventory, En);
        Assert.Equal(new[] { "llama-server-vulkan", "comfyui" }, rows.Select(r => r.Id));
        Assert.Equal(OptionalComponentPolicy.Catalog.Count, OptionalComponentPolicy.Catalog.Select(c => c.Id).Distinct().Count());
        Assert.Empty(OptionalComponentPolicy.BuildRows(null, En));
    }

    [Fact]
    public void Every_catalog_entry_has_italian_text_and_every_button_has_label_and_automation_suffix()
    {
        foreach (OptionalComponentCatalogEntry entry in OptionalComponentPolicy.Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.NameIt));
            Assert.False(string.IsNullOrWhiteSpace(entry.PurposeEn));
            Assert.NotEqual(entry.PurposeEn, entry.PurposeIt);
        }
        foreach (OptionalComponentButton b in Enum.GetValues<OptionalComponentButton>())
        {
            Assert.NotEqual(OptionalComponentPolicy.ButtonLabel(b, false), OptionalComponentPolicy.ButtonLabel(b, true));
            Assert.EndsWith("Button", OptionalComponentPolicy.AutomationSuffix(b));
        }
        var row = OptionalComponentPolicy.BuildRows(new[] { Managed("comfyui", false, true, "install", "unskip") }, It).Single();
        Assert.Equal("Non installato (saltato)", row.StateLabel);
        Assert.StartsWith("Circa ", row.SizeLabel);
    }

    [Fact]
    public void Size_label_is_honest_when_unknown()
    {
        Assert.Equal("Size shown before download", OptionalComponentPolicy.FormatSize(0, false));
        Assert.Equal("About 33 MB", OptionalComponentPolicy.FormatSize(34_936_498, false));
        Assert.Equal("About 1.9 GB", OptionalComponentPolicy.FormatSize(2_000_000_000, false));
    }

    [Fact]
    public void Download_gate_requires_permission_known_free_space_and_room_for_archive_plus_extract()
    {
        Assert.False(OptionalComponentDownloadGate.Evaluate(100, long.MaxValue, executableDownloadsAllowed: false).Allowed);
        Assert.False(OptionalComponentDownloadGate.Evaluate(100, -1, true).Allowed);
        long size = 500L * 1024 * 1024;
        long needed = size * 2 + OptionalComponentDownloadGate.HeadroomBytes;
        Assert.False(OptionalComponentDownloadGate.Evaluate(size, needed - 1, true).Allowed);
        Assert.True(OptionalComponentDownloadGate.Evaluate(size, needed, true).Allowed);
        Assert.Contains("MB", OptionalComponentDownloadGate.Evaluate(size, 1, true).ReasonIt);
    }

    [Theory]
    [InlineData("llama-server-vulkan", ComponentOwnership.PARUX_MANAGED, true, false, true)]
    [InlineData("llama-server-cuda", ComponentOwnership.PARUX_MANAGED, true, false, true)]
    [InlineData("llama-server-vulkan", ComponentOwnership.USER_MANAGED, true, false, false)]
    [InlineData("llama-server-vulkan", ComponentOwnership.SYSTEM_DETECTED, true, false, false)]
    [InlineData("llama-server-vulkan", ComponentOwnership.PARUX_MANAGED, false, false, false)]
    [InlineData("llama-server-vulkan", ComponentOwnership.PARUX_MANAGED, true, true, false)]
    [InlineData("comfyui", ComponentOwnership.PARUX_MANAGED, true, false, false)]
    [InlineData("git", ComponentOwnership.PARUX_MANAGED, true, false, false)]
    public void Removal_policy_allows_only_a_stopped_installed_paRuX_managed_engine(string id, ComponentOwnership owner, bool installed, bool running, bool allowed)
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-runtime-root");
        Assert.Equal(allowed, ManagedComponentRemovalPolicy.Evaluate(id, owner, installed, running, Path.Combine(root, "llama-b1-vulkan"), root).Allowed);
    }

    [Fact]
    public void Removal_policy_refuses_the_root_itself_traversal_and_bundled_copies()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-runtime-root");
        string other = Path.Combine(Path.GetTempPath(), "parux-runtime-root-evil", "llama");
        Assert.False(ManagedComponentRemovalPolicy.Evaluate("llama-server-vulkan", ComponentOwnership.PARUX_MANAGED, true, false, root, root).Allowed);
        Assert.False(ManagedComponentRemovalPolicy.Evaluate("llama-server-vulkan", ComponentOwnership.PARUX_MANAGED, true, false, other, root).Allowed);
        Assert.False(ManagedComponentRemovalPolicy.Evaluate("llama-server-vulkan", ComponentOwnership.PARUX_MANAGED, true, false,
            Path.Combine(root, "..", "elsewhere"), root).Allowed);
        Assert.False(ManagedComponentRemovalPolicy.Evaluate("llama-server-vulkan", ComponentOwnership.PARUX_MANAGED, true, false, null, root).Allowed);
    }

    [Fact]
    public void Hash_mismatch_is_a_failure_and_a_missing_digest_is_not_a_success()
    {
        string file = Path.Combine(Path.GetTempPath(), "parux-opt-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            string good = "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant();
            Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Verified, RuntimeAssetIntegrity.VerifySha256(file, good));
            Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Failed, RuntimeAssetIntegrity.VerifySha256(file, "sha256:" + new string('0', 64)));
            Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Failed, RuntimeAssetIntegrity.VerifySha256(file, "md5:abcd"));
            Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Unavailable, RuntimeAssetIntegrity.VerifySha256(file, null));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Msi_runtime_profile_defaults_to_full_and_lean_stages_only_the_vulkan_engine()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string wixproj = File.ReadAllText(Path.Combine(root!.FullName, "installer", "PaRuX.Setup", "PaRuX.Setup.wixproj"));
        Assert.Contains("<PaRuXMsiRuntimeProfile Condition=\"'$(PaRuXMsiRuntimeProfile)' == ''\">full</PaRuXMsiRuntimeProfile>", wixproj);
        Assert.Contains("'$(PaRuXMsiRuntimeProfile)' == 'lean'\">-ArtifactIds llama.cpp-vulkan<", wixproj);
        Assert.Contains("$(PaRuXMsiStageArtifactArgs)", wixproj);
        string lockJson = File.ReadAllText(Path.Combine(root.FullName, "packaging", "windows-baseline-runtime.lock.json"));
        Assert.Contains("\"id\": \"llama.cpp-vulkan\"", lockJson);
    }

    [Fact]
    public void Resume_continues_only_a_partial_file_shorter_than_the_asset()
    {
        Assert.False(RuntimeDownloadResume.PlanFor(0, 1000).Resumes);
        Assert.False(RuntimeDownloadResume.PlanFor(1000, 1000).Resumes);
        Assert.False(RuntimeDownloadResume.PlanFor(2000, 1000).Resumes);
        RuntimeDownloadResume.Plan plan = RuntimeDownloadResume.PlanFor(400, 1000);
        Assert.True(plan.Resumes);
        Assert.Equal(400, plan.RangeFrom);
        Assert.Equal("bytes 400-999/1000", RuntimeDownloadResume.ExpectedContentRange(400, 1000));
    }
}
