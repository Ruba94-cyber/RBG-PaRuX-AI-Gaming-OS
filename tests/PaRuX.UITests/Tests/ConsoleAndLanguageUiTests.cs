using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using System.Text.Json;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Persistence of the Gaming display role, the TV start preference and the Route-to-TV preference. (The native Console / TV
/// presenter tests were removed with the presenter in gaming-removal step 3; Console mode is Playnite Fullscreen.) No TV is required: no game is started, no display setting is changed and no
/// display is ever chosen as the Console target.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ConsoleAndLanguageUiTests
{
    private const string GamesNavId = "Navigation_GameLibrary_Item";
    private const string RouteToggleId = "Settings_ConsoleDisplays_RouteGames_Toggle";
    private const string DisplayRefreshId = "Settings_ConsoleDisplays_Refresh_Button";
    private const string GamingCategory = "Gaming & AI Sharing";

    private static readonly string[] WatchedSettingKeys =
    [
        "Theme", "StudioModeEnabled", "RouteVerifiedWindowedGamesToTv", "AutoShowConsoleAfterVerifiedGameExit",
        "ConsoleTargetDisplayId", "ConsoleDisplays"
    ];

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Settings"), Trait("Category", "Persistence")]
    public void Route_to_TV_toggle_persists_across_restart_without_any_TV_connected()
    {
        const string testName = nameof(Route_to_TV_toggle_persists_across_restart_without_any_TV_connected);
        using var initial = new UiTestApplication(testName);
        OpenGamingDisplaySettings(initial);

        var route = UiWait.ById(initial.MainWindow, RouteToggleId, ControlType.Button);
        UiWait.For(() => ToggleOf(route) == ToggleState.On, "Route-to-TV defaults to on");
        route.Patterns.Toggle.Pattern.Toggle();
        UiWait.For(() => ToggleOf(route) == ToggleState.Off, "Route-to-TV toggled off in the UI");
        UiWait.For(() => ReadBool(initial.SettingsPath, "RouteVerifiedWindowedGamesToTv") == false,
            "RouteVerifiedWindowedGamesToTv=false persisted before restart");

        using var restarted = initial.Restart("route-to-tv-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            Assert.Equal(false, ReadBool(restarted.SettingsPath, "RouteVerifiedWindowedGamesToTv"));

            OpenGamingDisplaySettings(restarted);
            var routeAfter = UiWait.ById(restarted.MainWindow, RouteToggleId, ControlType.Button);
            UiWait.For(() => ToggleOf(routeAfter) == ToggleState.Off,
                "Route-to-TV toggle restored to off after restart");

            // The opt-in auto-return switch is independent and stays at its default.
            var autoReturn = UiWait.ById(restarted.MainWindow, "Settings_ConsoleDisplays_AutoReturn_Toggle", ControlType.Button);
            Assert.Equal(ToggleState.Off, ToggleOf(autoReturn));

            routeAfter.Patterns.Toggle.Pattern.Toggle();
            UiWait.For(() => ReadBool(restarted.SettingsPath, "RouteVerifiedWindowedGamesToTv") == true,
                "RouteVerifiedWindowedGamesToTv=true persisted when turned back on");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("route-to-tv-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Settings"), Trait("Category", "Persistence")]
    public void Tv_start_preference_and_its_fallback_persist_across_restart()
    {
        const string testName = nameof(Tv_start_preference_and_its_fallback_persist_across_restart);
        using var initial = new UiTestApplication(testName);
        OpenGamingDisplaySettings(initial);

        var startOnTv = UiWait.ById(initial.MainWindow, "Settings_ConsoleDisplays_StartOnTv_Toggle", ControlType.Button);
        UiWait.For(() => ToggleOf(startOnTv) == ToggleState.Off, "start-in-TV-mode defaults to off");
        Assert.NotEqual(true, ReadBool(initial.SettingsPath, "ConsoleStartOnTvWhenPresent"));
        startOnTv.Patterns.Toggle.Pattern.Toggle();
        UiWait.For(() => ReadBool(initial.SettingsPath, "ConsoleStartOnTvWhenPresent") == true,
            "ConsoleStartOnTvWhenPresent=true persisted");

        var fallback = UiWait.ById(initial.MainWindow, "Settings_ConsoleDisplays_StartWithoutTv_Combo", ControlType.ComboBox);
        Assert.Contains("Normal window", fallback.AsComboBox().SelectedItem?.Name ?? string.Empty, StringComparison.Ordinal);   // the default
        fallback.AsComboBox().Select(1);   // Minimized
        UiWait.For(() => ReadString(initial.SettingsPath, "StartupWithoutTv") == "minimized", "StartupWithoutTv=minimized persisted");

        using var restarted = initial.Restart("tv-start-preference-restart");
        try
        {
            Assert.Equal(true, ReadBool(restarted.SettingsPath, "ConsoleStartOnTvWhenPresent"));
            Assert.Equal("minimized", ReadString(restarted.SettingsPath, "StartupWithoutTv"));
            // The launch behaviour (Playnite Fullscreen on the TV) needs Playnite and a TV; here the values themselves must survive.
            Assert.Equal(true, ReadBool(restarted.SettingsPath, "ConsoleStartOnTvWhenPresent"));
            Assert.Equal("minimized", ReadString(restarted.SettingsPath, "StartupWithoutTv"));
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("tv-start-preference-restart", ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI"), Trait("Category", "Settings"), Trait("Category", "Persistence")]
    public void Gaming_display_role_persists_across_restart_in_the_tv_list()
    {
        const string testName = nameof(Gaming_display_role_persists_across_restart_in_the_tv_list);
        using var initial = new UiTestApplication(testName);
        OpenGamingDisplaySettings(initial);

        AutomationElement? roleCombo = null;
        try
        {
            UiWait.For(() =>
            {
                roleCombo = initial.MainWindow.FindFirstDescendant(cf =>
                    cf.ByAutomationId("Settings_ConsoleDisplay_0_Role").And(cf.ByControlType(ControlType.ComboBox)));
                return roleCombo != null;
            }, "first detected display role selector", TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            // The local Core reported no usable display topology: there is nothing to classify. The honest
            // outcome is that no assignment or target was written.
            Assert.Null(ReadProperty(initial.SettingsPath, "ConsoleDisplays"));
            Assert.Equal(string.Empty, ReadString(initial.SettingsPath, "ConsoleTargetDisplayId") ?? string.Empty);
            return;
        }

        try
        {
            AutomationElement? tvItem = null;
            UiWait.For(() =>
            {
                // Display detection may repopulate the list; re-open the drop-down on every attempt.
                try { roleCombo!.Patterns.ExpandCollapse.Pattern.Expand(); } catch (Exception) { }
                tvItem = initial.FindRawDescendant(initial.MainWindow, element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.ListItem
                    && string.Equals(element.Properties.Name.ValueOrDefault, "TV", StringComparison.Ordinal));
                return tvItem != null;
            }, "TV option in the display type ComboBox");
            tvItem!.Patterns.SelectionItem.Pattern.Select();

            UiWait.For(() => HasTvAssignment(initial.SettingsPath), "TV display role persisted to settings.json");
            // The TV choice is remembered by monitor identity in the persistent TV list as well.
            UiWait.For(() => ReadProperty(initial.SettingsPath, "ConsoleTvs") is { } tvs && tvs.ToString().Contains("\"Selected\": true", StringComparison.Ordinal),
                "TV selection persisted in the monitor history");
        }
        catch (Exception ex)
        {
            initial.CaptureFailure(testName, ex);
            throw;
        }

        using var restarted = initial.Restart("display-role-restart");
        try
        {
            Assert.Equal(initial.DataRoot, restarted.DataRoot);
            Assert.True(HasTvAssignment(restarted.SettingsPath), "TV display role must survive restart.");

            OpenGamingDisplaySettings(restarted);
            var comboAfter = UiWait.ById(restarted.MainWindow, "Settings_ConsoleDisplay_0_Role", ControlType.ComboBox);
            // Read the selection directly: display detection may still be repopulating the drop-down list.
            UiWait.For(() =>
            {
                var current = UiWait.ById(restarted.MainWindow, "Settings_ConsoleDisplay_0_Role", ControlType.ComboBox);
                var selected = current.Patterns.Selection.PatternOrDefault?.Selection.ValueOrDefault;
                return selected != null && selected.Length > 0
                    && string.Equals(selected[0].Properties.Name.ValueOrDefault, "TV", StringComparison.Ordinal);
            }, "the persisted TV role is selected for the display after restart");
        }
        catch (Exception ex)
        {
            restarted.CaptureFailure("display-role-restart", ex);
            throw;
        }
    }

    private static void OpenGamingDisplaySettings(UiTestApplication app)
    {
        new MainWindowPage(app.MainWindow).OpenSettings();
        new SettingsCategoriesPage(app.MainWindow).SelectCategory(GamingCategory);
        var refresh = UiWait.ById(app.MainWindow, DisplayRefreshId, ControlType.Button);
        // Refresh initializes the persisted toggles from settings before user edits are written back.
        UiWait.For(() => refresh.Properties.IsEnabled.ValueOrDefault, "display refresh enabled");
        refresh.Patterns.Invoke.Pattern.Invoke();
        UiWait.For(() => UiWait.ById(app.MainWindow, DisplayRefreshId, ControlType.Button).IsEnabled,
            "display detection finished");
    }

    private static ToggleState? ToggleOf(AutomationElement element) =>
        element.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault;

    private static string? FocusedAutomationId(KeyboardTraversalPage keys) =>
        keys.GetFocusedElementSafe()?.Properties.AutomationId.ValueOrDefault;

    /// <summary>Identity of the focused element that also works for controls without an AutomationId.</summary>
    private static string? FocusSignature(KeyboardTraversalPage keys)
    {
        var element = keys.GetFocusedElementSafe();
        if (element == null) return null;
        var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
        return $"{element.Properties.AutomationId.ValueOrDefault}|{element.Properties.Name.ValueOrDefault}|" +
               $"{element.Properties.ControlType.ValueOrDefault}|{bounds.X:0},{bounds.Y:0}";
    }

    private static JsonDocument OpenSettings(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private static string? ReadProperty(string path, string name)
    {
        using var settings = OpenSettings(path);
        return settings.RootElement.TryGetProperty(name, out var value) ? value.GetRawText() : null;
    }

    private static bool? ReadBool(string path, string name)
    {
        using var settings = OpenSettings(path);
        if (!settings.RootElement.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadString(string path, string name)
    {
        using var settings = OpenSettings(path);
        return settings.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    private static Dictionary<string, string?> ReadWatchedSettings(string path)
    {
        var snapshot = new Dictionary<string, string?>();
        foreach (string key in WatchedSettingKeys)
            snapshot[key] = ReadProperty(path, key);
        return snapshot;
    }

    private static bool HasTvAssignment(string path)
    {
        using var settings = OpenSettings(path);
        if (!settings.RootElement.TryGetProperty("ConsoleDisplays", out var list)
            || list.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.TryGetProperty("Role", out var role)
                && string.Equals(role.GetString(), "tv", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
