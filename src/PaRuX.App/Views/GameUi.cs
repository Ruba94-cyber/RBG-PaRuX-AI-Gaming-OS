using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>Shared text and theme helpers of the game dialogs and panels (IT/EN strings, semantic brushes, title helpers).
/// Moved out of the removed native tile grid (gaming-removal step 4); behaviour unchanged.</summary>
internal static class GameUi
{
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    internal static string T(string en, string it) => Italian ? it : en;

    /// <summary>
    /// Semantic PaRuX brush for the element's actual theme (light, dark or Windows high contrast), looked up in the
    /// application's theme dictionaries so code-built views follow the same palette as the XAML views.
    /// </summary>
    internal static Brush ThemeBrush(FrameworkElement? owner, string key)
    {
        string theme = ActiveTheme(owner);
        // Realizing a tile asks for about fifteen brushes; resolving each through the theme dictionaries (and building an
        // AccessibilitySettings object) while scrolling a 2,000-title grid is wasted work. Brushes are shared resources
        // anyway, so the resolved instance is remembered per theme + key and dropped when the high-contrast state changes.
        string cacheKey = theme + "|" + key;
        if (BrushCache.TryGetValue(cacheKey, out Brush? cached)) return cached;
        if (TryFind(Application.Current.Resources, key, theme, 0, out object? value) && value is Brush brush)
        {
            BrushCache[cacheKey] = brush;
            return brush;
        }
        return new SolidColorBrush(Colors.Gray);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> BrushCache = new(StringComparer.Ordinal);
    private static Windows.UI.ViewManagement.AccessibilitySettings? _accessibility;

    /// <summary>
    /// Test hook: PARUX_GAMES_FORCE_HIGH_CONTRAST=1 makes the Games views take their high-contrast code path without changing any
    /// Windows setting. It exercises the app's own branches (no scrim, no glow, solid borders); it does NOT recolour WinUI's own
    /// system controls the way a real Windows high-contrast theme does.
    /// </summary>
    internal static readonly bool ForcedHighContrast =
        Environment.GetEnvironmentVariable("PARUX_GAMES_FORCE_HIGH_CONTRAST") == "1";

    private static string ActiveTheme(FrameworkElement? owner)
    {
        bool highContrast = ForcedHighContrast;
        try
        {
            if (_accessibility is null)
            {
                _accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
                _accessibility.HighContrastChanged += (_, _) => BrushCache.Clear();
            }
            highContrast |= _accessibility.HighContrast;
        }
        catch (Exception) { /* Fall back to the element's own light/dark theme. */ }
        return highContrast ? "HighContrast" : ThemeName(owner);
    }

    /// <summary>Drops the remembered brushes (an element's theme changed, so the same key may now resolve differently).</summary>
    internal static void ResetBrushCache() => BrushCache.Clear();

    internal static bool IsHighContrast(FrameworkElement owner) => ActiveTheme(owner) == "HighContrast";

    private static string ThemeName(FrameworkElement? owner) =>
        (owner?.ActualTheme ?? (Application.Current.RequestedTheme == ApplicationTheme.Light ? ElementTheme.Light : ElementTheme.Dark))
        == ElementTheme.Light ? "Light" : "Dark";

    private static bool TryFind(ResourceDictionary dictionary, string key, string theme, int depth, out object? value)
    {
        value = null;
        if (depth > 6) return false;
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out object? themed) && themed is ResourceDictionary themedDictionary
            && TryFind(themedDictionary, key, theme, depth + 1, out value)) return true;
        if (dictionary.TryGetValue(key, out value)) return true;
        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
            if (TryFind(merged, key, theme, depth + 1, out value)) return true;
        value = null;
        return false;
    }

    internal static Style? AccentButtonStyle =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) ? style as Style : null;

    /// <summary>Up to two letters for the cover fallback tile.</summary>
    internal static string Initials(string name)
    {
        var words = (name ?? string.Empty)
            .Split(new[] { ' ', '-', '_', ':', '.', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.FirstOrDefault(char.IsLetterOrDigit))
            .Where(c => c != default)
            .Take(2).ToArray();
        return words.Length == 0 ? "?" : new string(words).ToUpper(CultureInfo.CurrentCulture);
    }

    // The same safe selection is shared by tile Play and the detail panel.
    // It never rewrites the user's saved preferred installation.
    internal static GameInstallationDto? ChooseInstallation(GameTitleDto title) =>
        GameTitleLaunchPolicy.ChooseForQuickPlay(title);

    internal static bool CanStart(GameInstallationDto? installation) =>
        GameTitleLaunchPolicy.CanStart(installation);

    internal static string PlayLabel(GameInstallationDto? installation) =>
        installation?.Kind == ManualEntryKinds.Application ? T("Start", "Avvia") : T("Play", "Gioca");

    internal static string SourceLabel(GameInstallationDto i) => i.Source switch
    {
        GameInstallationSources.Steam => "Steam",
        GameInstallationSources.Epic => "Epic Games",
        GameInstallationSources.Gog => "GOG",
        GameInstallationSources.Ea => "EA app",
        GameInstallationSources.BattleNet => "Battle.net",
        GameInstallationSources.Ubisoft => "Ubisoft Connect",
        GameInstallationSources.Xbox => "Xbox",
        GameInstallationSources.Playnite => "Playnite",
        GameInstallationSources.Heroic => "Heroic",
        GameInstallationSources.Lutris => "Lutris",
        GameInstallationSources.Emulator => string.IsNullOrWhiteSpace(i.EmulatorName)
            ? T("Emulator", "Emulatore")
            : T("Emulator · ", "Emulatore · ") + i.EmulatorName,
        _ => i.Kind == ManualEntryKinds.Application ? T("Application on this PC", "Applicazione su questo PC") : T("Copy on this PC", "Copia su questo PC"),
    };

    /// <summary>Install state plus the store's pending-update note ("Installed · update available").</summary>
    internal static string StateLabel(GameInstallationDto installation) =>
        StateLabel(installation.InstallState) + (installation.UpdatePending ? " · " + T("update available", "aggiornamento disponibile") : string.Empty);

    internal static string StateLabel(string state) => state switch
    {
        GameInstallStates.Installed => T("Installed", "Installato"),
        GameInstallStates.Updating => T("Updating", "In aggiornamento"),
        GameInstallStates.Incomplete => T("Incomplete", "Incompleto"),
        GameInstallStates.MetadataOnly => T("Imported title only · not installed", "Solo titolo importato · non installato"),
        _ => T("State unknown", "Stato sconosciuto"),
    };

    internal static string ShortPlayTime(long seconds) =>
        seconds < 60 ? T("Not played yet", "Mai giocato")
        : seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60} min"
        : $"{seconds / 60} min";

    internal static string PlaySummary(GameTitleDto title)
    {
        string played = title.PlaySeconds < 60
            ? T("Not played yet", "Mai giocato")
            : T("Played ", "Giocato ") + ShortPlayTime(title.PlaySeconds);
        return title.LastPlayedUtc is { } last
            ? played + " · " + T("last ", "ultima volta ") + last.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)
            : played;
    }

    /// <summary>"Steam · PC", used as the quiet second line of a tile.</summary>
    internal static string SourceSummary(GameTitleDto title)
    {
        var sources = title.Installations.Select(SourceLabel).Distinct().Take(2).ToList();
        string platform = title.Installations.Select(i => i.Platform).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? string.Empty;
        string text = string.Join(", ", sources);
        return platform.Length == 0 ? text : text + " · " + platform;
    }

    internal static string StatusLabel(string? status) => (status ?? string.Empty) switch
    {
        GameCompletionStatuses.Backlog => T("Plan to play", "Da giocare"),
        GameCompletionStatuses.Playing => T("Playing", "In corso"),
        GameCompletionStatuses.Completed => T("Completed", "Completato"),
        GameCompletionStatuses.OnHold => T("On hold", "In pausa"),
        GameCompletionStatuses.Dropped => T("Dropped", "Abbandonato"),
        _ => T("No status", "Nessuno stato"),
    };

    internal static GameTitleDto WithState(GameTitleDto title, bool? favorite = null, bool? hidden = null,
        IReadOnlyList<string>? tags = null, IReadOnlyList<string>? collections = null,
        string? status = null, int? score = null) =>
        title with
        {
            Status = status ?? title.Status,
            Score = score ?? title.Score,
            Favorite = favorite ?? title.Favorite,
            Hidden = hidden ?? title.Hidden,
            Tags = tags ?? title.Tags,
            Collections = collections ?? title.Collections,
        };
}
