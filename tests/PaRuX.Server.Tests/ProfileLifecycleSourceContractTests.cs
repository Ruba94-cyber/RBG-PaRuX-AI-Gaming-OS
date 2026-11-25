using System.Linq;
using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Cross-file contracts preventing hidden AI/Gaming pages from doing work.
 /// These checks are source-only; FlaUI and real Windows performance measurements
 /// remain required before release.</summary>
public sealed class ProfileLifecycleSourceContractTests
{
    [Fact]
    public void Model_hub_cancels_background_polls_before_hide_and_only_starts_one_initial_refresh()
    {
        string hub = Read("src", "PaRuX.App", "Views", "ModelHubView.cs");
        string shell = Read("src", "PaRuX.App", "MainWindow.Workspaces.cs");

        // Hiding stops background polls but keeps a user-started repository check; only a real Unload cancels it.
        Assert.Contains("public void OnHidden(bool keepExplicitDiscovery = true)", hub, StringComparison.Ordinal);
        Assert.Contains("Unloaded += (_, _) => OnHidden(keepExplicitDiscovery: false);", hub, StringComparison.Ordinal);
        Assert.Contains("_downloadPoll?.Stop()", hub, StringComparison.Ordinal);
        Assert.Contains("_candidateEvaluationPoll?.Stop()", hub, StringComparison.Ordinal);
        Assert.Contains("_startPoll?.Stop()", hub, StringComparison.Ordinal);
        Assert.Contains("!_refreshBusy && !_startHubLoaded", hub, StringComparison.Ordinal);
        Assert.Contains("visibleHub.OnHidden()", shell, StringComparison.Ordinal);
        Assert.Contains("if (CorporateMode.IsActive) advanced = false", hub, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_default_chat_recovers_missing_saved_role_only_after_text_model_validation()
    {
        string runtime = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        string roles = Read("src", "PaRuX.App", "Views", "RoleProfilePanel.cs");

        Assert.Contains("CorporateTextModelChoicePolicy.Resolve(", runtime, StringComparison.Ordinal);
        Assert.Contains("CorporateModelImportPolicy.IsAcceptableModel(", runtime, StringComparison.Ordinal);
        Assert.Contains("Saved Chat model was unavailable", runtime, StringComparison.Ordinal);
        Assert.Contains("CorporateModelImportPolicy.IsAcceptableModel(m.FileName, m.SizeBytes)", roles, StringComparison.Ordinal);
    }

    /// <summary>ManualGameLibraryView is a partial class split across several files; scan all of them.</summary>
    private static string ReadGameLibraryView()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string[] files = Directory.GetFiles(Path.Combine(root!.FullName, "src", "PaRuX.App", "Views"), "ManualGameLibraryView*.cs");
        Array.Sort(files, StringComparer.Ordinal);
        return string.Concat(files.Select(File.ReadAllText));
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln")))
            dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(path).ToArray()));
    }
}
