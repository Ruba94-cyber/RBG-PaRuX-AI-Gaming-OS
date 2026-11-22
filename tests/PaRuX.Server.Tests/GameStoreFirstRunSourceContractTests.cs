using System.Linq;
using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source wiring guards. Real Game-library UI, settings persistence and local-store
/// scans still require an exclusive Windows FlaUI/integration run.
/// </summary>
public sealed class GameStoreFirstRunSourceContractTests
{
    [Fact]
    public void First_run_game_scan_is_opt_in_and_has_persistent_skip()
    {
        string ui = ReadGameLibraryView();
        string settings = Read("src", "PaRuX.App", "Models", "AppSettings.cs");

        Assert.Contains("GameStoreDiscoveryPromptDismissed", settings, StringComparison.Ordinal);
        Assert.Contains("BuildStoreDiscoveryOffer(root);", ui, StringComparison.Ordinal);
        Assert.Contains("GameLibrary_FirstRunScan_Start", ui, StringComparison.Ordinal);
        Assert.Contains("GameLibrary_FirstRunScan_Dismiss", ui, StringComparison.Ordinal);
        Assert.Contains("_storeDiscoveryOfferScan.Click += async", ui, StringComparison.Ordinal);
        Assert.Contains("_storeDiscoveryOfferDismiss.Click +=", ui, StringComparison.Ordinal);
        Assert.Contains("SettingsStore.Current.Data.GameStoreDiscoveryPromptDismissed = true;", ui, StringComparison.Ordinal);
        Assert.Contains("SettingsStore.Current.Save();", ui, StringComparison.Ordinal);

        // Simply entering Games must NOT read Steam/Epic/GOG metadata implicitly.
        int start = ui.IndexOf("public async void OnShown()", StringComparison.Ordinal);
        int end = ui.IndexOf("private async Task<GamingStatusDto?> UpdateGamingStatusAsync()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The Games OnShown method moved; audit scan admission.");
        Assert.DoesNotContain("RefreshAllStoresAsync(", ui[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void Store_provider_outage_does_not_erase_successful_store_scan()
    {
        string ui = ReadGameLibraryView();
        Assert.Contains("TryDiscoverStoreAsync", ui, StringComparison.Ordinal);
        Assert.Contains("GamingCallResult<List<DiscoveredGameDto>>.Fail", ui, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(steam, epic, gog);", ui, StringComparison.Ordinal);
        Assert.Contains("results.Any(x => x.Result.Succeeded && x.Result.Value != null)", ui, StringComparison.Ordinal);
        Assert.Contains("A full outage", ui, StringComparison.Ordinal);
        Assert.Contains("if (_busy || !CanDiscoverStores()) return;", ui, StringComparison.Ordinal);
        Assert.Contains("GamingManager.Current.IsActive", ui, StringComparison.Ordinal);
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

    private static string Read(params string[] parts)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(parts).ToArray()));
    }
}
