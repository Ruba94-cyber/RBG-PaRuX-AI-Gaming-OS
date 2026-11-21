using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PaRuX.Services
{
    /// <summary>
    /// Applies the chosen accent to the live brushes, so open windows change colour at once. PRX Orbit (the default) is the
    /// untouched shipped palette: the original colours are remembered on first use and put back when Orbit is chosen again.
    /// Windows High Contrast is never overridden.
    /// </summary>
    public static class AccentApplier
    {
        private static readonly Dictionary<(string Theme, string Key), Color> Originals = new();

        public static void Apply(string? accentId, string? customHex)
        {
            try
            {
                string? hex = AccentPalette.Resolve(accentId, customHex);
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    ResourceDictionary? dictionary = FindThemeDictionary(Application.Current.Resources, theme);
                    if (dictionary == null) continue;
                    if (hex == null) { Restore(theme, dictionary); continue; }
                    if (!AccentPalette.TryParse(hex, out Rgb accent)) { Restore(theme, dictionary); continue; }
                    AccentColors c = AccentPalette.Derive(accent, dark: theme == "Dark");
                    Set(theme, dictionary, "AccentFillColorDefaultBrush", c.Fill);
                    Set(theme, dictionary, "AccentFillColorSecondaryBrush", c.Fill, 0xE5);
                    Set(theme, dictionary, "AccentFillColorTertiaryBrush", c.Fill, 0xCC);
                    foreach (string key in new[] { "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush", "PaRuXBrandTextBrush" })
                        Set(theme, dictionary, key, c.TextOnSurface);
                    Set(theme, dictionary, "AccentButtonBackground", c.Fill);
                    Set(theme, dictionary, "AccentButtonBackgroundPointerOver", c.FillHover);
                    Set(theme, dictionary, "AccentButtonBackgroundPressed", c.FillPressed);
                    foreach (string key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "PaRuXOnBrandBrush" })
                        Set(theme, dictionary, key, c.OnFill);
                    Set(theme, dictionary, "PaRuXBrandBrush", c.Fill);
                    Set(theme, dictionary, "PaRuXBrandSubtleBrush", c.Subtle);
                    Set(theme, dictionary, "PaRuXBrandBorderBrush", c.Border);
                }
            }
            catch (Exception ex)
            {
                Log.Write("AccentApplier failed: " + ex.Message);
            }
        }

        private static void Set(string theme, ResourceDictionary dictionary, string key, Rgb color, byte alpha = 0xFF)
        {
            if (!dictionary.TryGetValue(key, out object? value) || value is not SolidColorBrush brush) return;
            Originals.TryAdd((theme, key), brush.Color);
            brush.Color = Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static void Restore(string theme, ResourceDictionary dictionary)
        {
            foreach (((string t, string key), Color original) in Originals)
                if (t == theme && dictionary.TryGetValue(key, out object? value) && value is SolidColorBrush brush)
                    brush.Color = original;
        }

        private static ResourceDictionary? FindThemeDictionary(ResourceDictionary root, string theme)
        {
            if (root.ThemeDictionaries.TryGetValue(theme, out object? found) && found is ResourceDictionary direct) return direct;
            foreach (ResourceDictionary merged in root.MergedDictionaries)
            {
                ResourceDictionary? inner = FindThemeDictionary(merged, theme);
                if (inner != null) return inner;
            }
            return null;
        }
    }
}
