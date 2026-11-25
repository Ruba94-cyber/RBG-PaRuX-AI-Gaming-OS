using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source contracts for the slim Games tools page (GamesToolsView): it reuses existing dialogs/panels/services, never starts work
/// merely by being opened, shows Advanced-only tools only in Advanced, and is gated by the same setup profile as Games.
/// The real behaviour needs the WinUI app (tests/PaRuX.UITests GamesToolsPageUiTests) and was not run by these tests.
/// </summary>
public sealed class GamesToolsViewContractTests
{
    [Fact]
    public void Page_reuses_existing_entry_points_instead_of_new_services()
    {
        string view = Read("src", "PaRuX.App", "Views", "GamesToolsView.cs");
        foreach (string reused in new[]
        {
            "TemporaryGameModsDialog.ShowPendingAiProposalsAsync", "TemporaryGameModsDialog.ShowRestoreListAsync",
            "GameAssetModStudioDialog.ShowAsync", "GameModDropInDialog.ShowInstalledAsync", "MinecraftSkinStudioDialog.ShowAsync",
            "new QuickResumePanel()", "new PausedGameBanner()", "new GameDownloadsPanel()", "new GameSaveBackupPanel()",
            "new GamingDevicesPanel()", "LocalGameGuideFlow.RunAsync", "ShowPlayniteLauncherAsync", "ShowPadControllerSettings",
            "GamingSectionSignInApps", "GamingSectionPlaynite", "GamingSectionConsoleDisplays",
        })
            Assert.Contains(reused, view, StringComparison.Ordinal);
    }

    [Fact]
    public void Heavy_panels_exist_only_after_their_section_is_expanded_and_open_never_scans_or_launches()
    {
        string view = Read("src", "PaRuX.App", "Views", "GamesToolsView.cs");
        Assert.Contains("expander.Expanding += (_, _) => expander.Content ??= create();", view, StringComparison.Ordinal);
        int onShownAt = view.IndexOf("public void OnShown()", StringComparison.Ordinal);
        Assert.True(onShownAt > 0);
        foreach ((string id, string panel) in new[]
        {
            ("QuickResume", "new QuickResumePanel()"), ("Downloads", "new GameDownloadsPanel()"),
            ("SaveBackups", "new GameSaveBackupPanel()"), ("ControllerTools", "new GamingDevicesPanel()"),
        })
        {
            int lazy = view.IndexOf("Lazy(\"" + id + "\"", StringComparison.Ordinal);
            int created = view.IndexOf(panel, StringComparison.Ordinal);
            Assert.True(lazy > 0 && created > lazy, id + " panel must be created inside its Lazy factory");
            Assert.Equal(created, view.LastIndexOf(panel, StringComparison.Ordinal));
        }

        string onShown = view[onShownAt..view.IndexOf("// ---- building blocks", StringComparison.Ordinal)];
        foreach (string forbidden in new[] { "Start", "Install", "Download", "Scan", "Launch", "Import" })
            Assert.DoesNotContain(forbidden, onShown, StringComparison.Ordinal);
    }

    [Fact]
    public void Advanced_only_sections_are_gated_by_the_interface_mode()
    {
        string view = Read("src", "PaRuX.App", "Views", "GamesToolsView.cs");
        Assert.Contains("_advanced = new() { Spacing = 16, Visibility = Visibility.Collapsed }", view, StringComparison.Ordinal);
        Assert.Contains("SettingsStore.Current.Data.StudioModeEnabled", view, StringComparison.Ordinal);
        Assert.Contains("_advanced.Visibility = advanced", view, StringComparison.Ordinal);
        // Save backups and Controller tools / display are added to the Advanced container, everything else to the page root.
        Assert.Matches(new Regex(@"Card\(T\(""Save backups""[^;]*?_advanced\);", RegexOptions.Singleline), view);
        Assert.Matches(new Regex(@"Card\(T\(""Controller tools and display""[^;]*?_advanced\);", RegexOptions.Singleline), view);
    }

    [Fact]
    public void User_visible_text_is_localized_and_does_not_point_to_a_new_native_library()
    {
        string view = Read("src", "PaRuX.App", "Views", "GamesToolsView.cs");
        // No English-only literal assigned to a text property: everything goes through T(en, it) / UiText.Pick.
        Assert.DoesNotMatch(new Regex(@"\b(Text|Content|Header)\s*=\s*""[A-Za-z]{3,}[^""]*\s[A-Za-z]{2,}"""), view);
        Assert.DoesNotContain("native library", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("libreria nativa", view, StringComparison.OrdinalIgnoreCase);
        // Identity comes from the shared PRX Orbit theme styles (high-contrast / light / dark aware), no hard-coded colors.
        Assert.Contains("PaRuXCardStyle", view, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"Color\.FromArgb|Colors\.\w+|new SolidColorBrush"), view);
    }

    [Fact]
    public void Shell_registers_the_page_as_a_lazy_Games_sub_workspace_without_a_new_sidebar_item_or_icon()
    {
        string lazy = Read("src", "PaRuX.App", "MainWindow.LazyPages.cs");
        Assert.Contains("[StudioWorkspace.GamesTools] = new(\"Workspace_GamesTools_Page\"", lazy, StringComparison.Ordinal);
        Assert.Contains("gamesTools.GameGuideChatRequested += OnGameGuideChatRequested", lazy, StringComparison.Ordinal);

        string ws = Read("src", "PaRuX.App", "MainWindow.Workspaces.cs");
        Assert.Contains("workspace is StudioWorkspace.Games or StudioWorkspace.GamesTools", ws, StringComparison.Ordinal);
        Assert.Contains("StudioWorkspace.Games or StudioWorkspace.GamesTools => GameLibraryNavItem", ws, StringComparison.Ordinal);

        // The sidebar and the app icon are untouched: no sidebar item for the sub-page.
        string xaml = Read("src", "PaRuX.App", "MainWindow.xaml");
        Assert.DoesNotContain("GamesTools", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkspaceNavigationOrder.GamesToolsWorkspaceTag,
            WorkspaceNavigationOrder.Entries.Select(e => e.Tag));
    }

    [Theory]
    [InlineData(SetupProfile.Both, false, true)]
    [InlineData(SetupProfile.GamingOnly, false, true)]
    [InlineData(SetupProfile.AiOnly, false, false)]
    [InlineData(SetupProfile.Both, true, false)]       // Corporate Lite: only Chat/Documents/Model Hub
    [InlineData(SetupProfile.GamingOnly, true, false)]
    public void Games_tools_deep_link_follows_the_same_setup_profile_gate_as_Games(SetupProfile profile, bool corporate, bool expected)
    {
        Assert.Equal(expected, WorkspaceNavigationOrder.CanOpenWorkspace(WorkspaceNavigationOrder.GamesToolsWorkspaceTag, profile, corporate));
        Assert.Equal(expected, WorkspaceNavigationOrder.CanOpenWorkspace(WorkspaceNavigationOrder.GamesWorkspaceTag, profile, corporate));
    }

    [Fact]
    public void Games_item_mode_tab_and_controller_open_the_slim_tools_page_not_the_native_library()
    {
        // Step 2 of the gaming-removal map: "__games__" (sidebar, Ctrl+2, Gaming tab, old deep links) and "__gamestools__" share one route.
        string ws = Read("src", "PaRuX.App", "MainWindow.Workspaces.cs");
        Assert.Matches(new Regex(@"case WorkspaceNavigationOrder\.GamesWorkspaceTag:\s*case WorkspaceNavigationOrder\.GamesToolsWorkspaceTag:\s*ShowGamesHome\(\);"), ws);
        Assert.Contains("ShowStudioWorkspace(StudioWorkspace.GamesTools);", ws, StringComparison.Ordinal);

        string pad = Read("src", "PaRuX.App", "MainWindow.PadActions.cs");
        Assert.DoesNotContain("OpenToolsFromController", pad, StringComparison.Ordinal);
        Assert.Contains("ShowGamesHomeFromController()", pad, StringComparison.Ordinal);

        // The install/import sub-page keeps downloads, add & import and status, but no Library / Tools / Emulators sections.
        string library = Read("src", "PaRuX.App", "Views", "ManualGameLibraryView.cs");
        foreach (string gone in new[] { "(\"Library\",", "(\"Tools\",", "(\"Emulators\"," })
            Assert.DoesNotContain(gone, library, StringComparison.Ordinal);
        foreach (string kept in new[] { "(\"Downloads\",", "(\"Add\",", "(\"Devices\"," })
            Assert.Contains(kept, library, StringComparison.Ordinal);
    }

    [Fact]
    public void Console_mode_is_Playnite_Fullscreen_and_keeps_its_Game_Bar_power_and_support_app_hooks()
    {
        // Gaming-removal step 3: no native PaRuX Console/TV presenter. Console = Playnite Fullscreen (owner's theme untouched).
        string start = Read("src", "PaRuX.App", "MainWindow.PlayniteStartup.cs");
        int handoff = start.IndexOf("internal async Task<ConsolePlayniteStartResult> TryStartPlayniteConsoleAsync", StringComparison.Ordinal);
        Assert.True(handoff > 0);
        string body = start[handoff..start.IndexOf("internal void MinimizeForPlayniteConsole()", handoff, StringComparison.Ordinal)];
        foreach (string hook in new[] { "PlayniteModes.Console", "ApplyConsolePowerProfileAsync()", "ApplyConsoleGameBarBlockAsync()", "StartConsoleSupportAppsAsync()" })
            Assert.Contains(hook, body, StringComparison.Ordinal);
        // ...and every exit path restores them.
        Assert.Contains("RestoreConsolePowerProfileAsync(); _ = RestoreConsoleGameBarBlockAsync(); _ = StopConsoleSupportAppsAsync();", start, StringComparison.Ordinal);

        // Reachable from the Games tools page, the Gaming-tab footer button, the command palette, sign-in start and the pad.
        Assert.Contains("OpenPlayniteAsync(PlayniteLauncherAction.Console)", Read("src", "PaRuX.App", "Views", "GamesToolsView.cs"), StringComparison.Ordinal);
        string pad = Read("src", "PaRuX.App", "MainWindow.PadNavigation.cs");
        Assert.Contains("ShowPlayniteLauncherAsync(PlayniteLauncherAction.Console", pad, StringComparison.Ordinal);
        Assert.Contains("MainWindow_PlayniteConsole_Button", Read("src", "PaRuX.App", "MainWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("PlayniteLauncherAction.Console", Read("src", "PaRuX.App", "MainWindow.CommandPalette.cs"), StringComparison.Ordinal);
        Assert.Contains("TryStartPlayniteConsoleAsync(explicitRequest: playniteOnly)", Read("src", "PaRuX.App", "MainWindow.LogonStartup.cs"), StringComparison.Ordinal);
        Assert.Contains("PadSwitchPlayniteAsync", start, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_library_views_are_gone_and_every_kept_game_function_stays_reachable()
    {
        // Gaming-removal step 4: the native library UI is deleted (not just hidden).
        foreach (string gone in new[]
        {
            "GamingHomeView", "GameDetailPanel", "GameTileGrid", "GameShelfFeed", "GameHeroCard", "GroupedGameLibrary",
            "GameSideFilterPanel", "GamingWishlistDialog", "IGameTileSurface", "GameMotion", "GameShortcutsSection",
            "GameLaunchProfileSection", "GameHooksSection", "GameControllerProfileSection", "SteamGridDbArtworkDialog", "UniversalLibraryPanel",
        })
            Assert.False(File.Exists(Path.Combine(Root(), "src", "PaRuX.App", "Views", gone + ".cs")), gone + " must be removed");

        // Games tools page (the Games home): mods (AI-driven, change-sets/restore, texture, drop-in, skins), Quick Resume and paused
        // games, downloads, installer staging, handoff, local guide, save backups, controller tools/display, streaming, sign-in apps,
        // Game Bar / Console settings and controller actions.
        string tools = Read("src", "PaRuX.App", "Views", "GamesToolsView.cs");
        foreach (string entry in new[]
        {
            "ShowPendingAiProposalsAsync", "ShowRestoreListAsync", "GameAssetModStudioDialog.ShowAsync", "GameModDropInDialog.ShowInstalledAsync",
            "MinecraftSkinStudioDialog.ShowAsync", "new QuickResumePanel()", "new PausedGameBanner()", "new GameDownloadsPanel()",
            "OpenLibrarySection(\"Add\")", "UpdateHandoffStatus", "LocalGameGuideFlow.RunAsync", "new GameSaveBackupPanel()", "new GamingDevicesPanel()",
            "GamingSectionPlaynite", "GamingSectionSignInApps", "GamingSectionConsoleDisplays", "ShowPadControllerSettings",
        })
            Assert.Contains(entry, tools, StringComparison.Ordinal);

        // The install/import sub-page keeps installers, stores, manual entries, Playnite import, downloads and the Gaming status.
        string library = string.Concat(Directory.GetFiles(Path.Combine(Root(), "src", "PaRuX.App", "Views"), "ManualGameLibraryView*.cs").Select(File.ReadAllText));
        foreach (string kept in new[]
        {
            "GameLibrary_Installer_Inspect_Button", "GameLibrary_Installer_Stage_Button", "GameLibrary_AllStores_Refresh_Button",
            "GameLibrary_Add_Button", "new PlayniteImportPanel(", "_downloads", "GameLibrary_ReturnToAi_Button", "new GameSaveBackupPanel()",
            "PlayRequestedByShortcutAsync",
        })
            Assert.Contains(kept, library, StringComparison.Ordinal);
        Assert.DoesNotContain("UniversalLibraryPanel", library, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_game_guide_is_one_shared_flow_with_unchanged_automation_ids()
    {
        string flow = Read("src", "PaRuX.App", "Views", "LocalGameGuideFlow.cs");
        string home = Read("src", "PaRuX.App", "Views", "GamesToolsView.cs");
        foreach (string id in new[] { "GameHome_LocalGuide_Game", "GameHome_LocalGuide_Question", "GameHome_LocalGuide_SourceFilter", "GameHome_LocalGuide_Sources" })
            Assert.Contains(id, flow, StringComparison.Ordinal);
        Assert.Contains("LocalGameGuideFlow.RunAsync(XamlRoot", home, StringComparison.Ordinal);
        // Still only prepares a Chat draft: the flow never sends.
        Assert.DoesNotContain("SendAsync", flow, StringComparison.Ordinal);
        Assert.Contains("PrepareLocalGameGuideAsync", flow, StringComparison.Ordinal);
    }

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PARUX_EXECUTION_PLAN.md"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));
}
