using System;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The library theme of an enabled game extension, applied through the existing accent system: its accent replaces the chosen
    /// accent while it is active (the saved accent choice is untouched, so clearing the theme restores it), and its optional
    /// background tints the game library only when the app theme matches the palette's appearance (so text stays readable).
    /// Pure state: no UI types, tested without a window.
    /// </summary>
    public static class LibraryThemeState
    {
        private static GameExtensionTheme? _active;

        public static GameExtensionTheme? Active => _active;

        /// <summary>Raised after <see cref="Set"/>; the library view repaints its background and the app re-applies the accent.</summary>
        public static event Action? Changed;

        public static void Set(GameExtensionTheme? theme)
        {
            _active = theme is not null && GameExtensionManifestValidator.TryParseHex(theme.Accent, out _) ? theme : null;
            Changed?.Invoke();
        }

        /// <summary>The accent to apply: the active theme's accent (as a custom colour) or the owner's own choice unchanged.</summary>
        public static (string? Id, string? Hex) EffectiveAccent(string? savedId, string? savedHex)
        {
            GameExtensionTheme? t = _active;
            return t is not null ? (AccentPalette.CustomId, t.Accent) : (savedId, savedHex);
        }

        /// <summary>#RRGGBB for the library background, or null when there is no theme, no background or the app theme does not match the palette.</summary>
        public static string? BackgroundHex(bool appIsDark)
        {
            GameExtensionTheme? t = _active;
            if (t is null || string.IsNullOrEmpty(t.Background)) return null;
            if (!GameExtensionManifestValidator.TryParseHex(t.Background, out _)) return null;
            bool paletteDark = string.Equals(t.Appearance, "dark", StringComparison.Ordinal);
            return paletteDark == appIsDark ? t.Background : null;
        }
    }
}
