using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaRuX.Services
{
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public string Hex => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");
    }

    /// <summary>One selectable accent. <see cref="Hex"/> is null for the built-in PRX Orbit identity, which is never overridden.</summary>
    public sealed record AccentPreset(string Id, string EnglishName, string ItalianName, string? Hex);

    /// <summary>The colours one accent produces in one theme. Every text/background pair is checked for WCAG AA (4.5:1).</summary>
    public sealed record AccentColors(Rgb Fill, Rgb FillHover, Rgb FillPressed, Rgb OnFill, Rgb TextOnSurface, Rgb Subtle, Rgb Border);

    /// <summary>
    /// Accent colour for the whole app. The default is the approved PRX Orbit identity (no override at all). A chosen accent is
    /// turned into a full set of fills and text colours whose contrast is verified here, not assumed: if a colour would be
    /// unreadable on its background it is nudged lighter or darker until it passes.
    /// </summary>
    public static class AccentPalette
    {
        public const string OrbitId = "orbit";
        public const string CustomId = "custom";
        public const double MinimumContrast = 4.5;

        public static readonly IReadOnlyList<AccentPreset> Presets = new[]
        {
            new AccentPreset(OrbitId, "PRX Orbit (default)", "PRX Orbit (predefinito)", null),
            new AccentPreset("azure", "Azure", "Azzurro", "#2563EB"),
            new AccentPreset("teal", "Teal", "Verde acqua", "#0F766E"),
            new AccentPreset("emerald", "Emerald", "Smeraldo", "#15803D"),
            new AccentPreset("amber", "Amber", "Ambra", "#B45309"),
            new AccentPreset("rose", "Rose", "Rosa", "#BE123C"),
            new AccentPreset("violet", "Violet", "Viola", "#6D28D9"),
        };

        /// <summary>Surfaces text sits on (light app/card, dark app/card), used for the readability check of accent-coloured text.</summary>
        public static readonly Rgb[] LightSurfaces = { new(0xFD, 0xFE, 0xFE), new(0xFF, 0xFF, 0xFF) };
        public static readonly Rgb[] DarkSurfaces = { new(0x07, 0x09, 0x0C), new(0x0D, 0x11, 0x16) };

        public static AccentPreset Find(string? id) =>
            Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

        /// <summary>The colour to apply for the saved choice, or null for Orbit. A bad custom value falls back to Orbit.</summary>
        public static string? Resolve(string? id, string? customHex)
        {
            if (string.Equals(id, CustomId, StringComparison.OrdinalIgnoreCase))
                return TryParse(customHex, out Rgb custom) ? custom.Hex : null;
            return Find(id).Hex;
        }

        public static bool TryParse(string? hex, out Rgb color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string text = hex.Trim().TrimStart('#');
            if (text.Length == 3) text = string.Concat(text.Select(c => new string(c, 2)));
            if (text.Length != 6 || !text.All(Uri.IsHexDigit)) return false;
            color = new Rgb(
                byte.Parse(text[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return true;
        }

        public static double Luminance(Rgb c)
        {
            static double Channel(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        /// <summary>WCAG contrast ratio, 1 to 21.</summary>
        public static double Contrast(Rgb a, Rgb b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static readonly Rgb White = new(255, 255, 255);
        private static readonly Rgb Black = new(0, 0, 0);

        /// <summary>The readable text colour on a fill: white or near-black, whichever contrasts more.</summary>
        public static Rgb OnColor(Rgb fill) => Contrast(fill, White) >= Contrast(fill, new Rgb(0x0B, 0x0D, 0x10)) ? White : new Rgb(0x0B, 0x0D, 0x10);

        public static AccentColors Derive(Rgb accent, bool dark)
        {
            Rgb fill = EnsureFillReadable(accent);
            Rgb on = OnColor(fill);   // one text colour serves every state of the button
            Rgb hover = EnsureReadableWith(dark ? Shift(fill, +0.07) : Shift(fill, -0.07), on);
            Rgb pressed = EnsureReadableWith(dark ? Shift(fill, -0.07) : Shift(fill, -0.14), on);
            Rgb[] surfaces = dark ? DarkSurfaces : LightSurfaces;
            Rgb text = EnsureTextReadable(accent, surfaces, dark);
            Rgb reference = surfaces[0];
            return new AccentColors(fill, hover, pressed, on, text, Mix(reference, accent, 0.14), Mix(reference, accent, 0.38));
        }

        /// <summary>Moves lightness until the best of white or black text reaches AA on the fill.</summary>
        private static Rgb EnsureFillReadable(Rgb fill)
        {
            Rgb current = fill;
            for (int i = 0; i < 40; i++)
            {
                Rgb on = OnColor(current);
                if (Contrast(current, on) >= MinimumContrast) return current;
                // Mid-tones are the problem; push away from the middle toward the nearer end.
                current = Shift(current, Luminance(current) < 0.18 ? +0.03 : -0.03);
            }
            return current;
        }

        /// <summary>Moves a state colour away from the text colour until the pair reaches AA.</summary>
        private static Rgb EnsureReadableWith(Rgb fill, Rgb on)
        {
            Rgb current = fill;
            double direction = Luminance(on) > 0.5 ? -0.02 : +0.02;   // light text: darken the fill; dark text: lighten it
            for (int i = 0; i < 60 && Contrast(current, on) < MinimumContrast; i++) current = Shift(current, direction);
            return current;
        }

        private static Rgb EnsureTextReadable(Rgb text, Rgb[] surfaces, bool dark)
        {
            Rgb current = text;
            for (int i = 0; i < 60; i++)
            {
                if (surfaces.All(s => Contrast(current, s) >= MinimumContrast)) return current;
                current = Shift(current, dark ? +0.03 : -0.03);
            }
            return current;
        }

        private static Rgb Mix(Rgb under, Rgb over, double amount) => new(
            (byte)Math.Round(under.R + (over.R - under.R) * amount),
            (byte)Math.Round(under.G + (over.G - under.G) * amount),
            (byte)Math.Round(under.B + (over.B - under.B) * amount));

        private static Rgb Shift(Rgb c, double lightnessDelta)
        {
            (double h, double s, double l) = ToHsl(c);
            return FromHsl(h, s, Math.Clamp(l + lightnessDelta, 0.0, 1.0));
        }

        private static (double H, double S, double L) ToHsl(Rgb c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2, h = 0, s = 0;
            double d = max - min;
            if (d > 1e-9)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            return (h, s, l);
        }

        private static Rgb FromHsl(double h, double s, double l)
        {
            if (s < 1e-9) { byte v = (byte)Math.Round(l * 255); return new Rgb(v, v, v); }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            static double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
            return new Rgb((byte)Math.Round(Hue(p, q, h + 1.0 / 3) * 255), (byte)Math.Round(Hue(p, q, h) * 255), (byte)Math.Round(Hue(p, q, h - 1.0 / 3) * 255));
        }
    }
}
