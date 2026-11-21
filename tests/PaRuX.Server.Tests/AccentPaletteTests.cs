using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Accent colour: Orbit stays the default, every accent is readable in light and dark, and bad input never breaks the look.</summary>
public sealed class AccentPaletteTests
{
    public static IEnumerable<object[]> AccentPresets() =>
        AccentPalette.Presets.Where(p => p.Hex != null).Select(p => new object[] { p.Id });

    [Fact]
    public void Orbit_is_the_default_and_is_never_overridden()
    {
        Assert.Equal(AccentPalette.OrbitId, AccentPalette.Presets[0].Id);
        Assert.Null(AccentPalette.Presets[0].Hex);
        Assert.Null(AccentPalette.Resolve(null, null));
        Assert.Null(AccentPalette.Resolve("orbit", "#FF0000"));
        Assert.Null(AccentPalette.Resolve("not-a-preset", null));
    }

    [Fact]
    public void Presets_have_unique_ids_and_names_in_both_languages()
    {
        Assert.Equal(AccentPalette.Presets.Count, AccentPalette.Presets.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (AccentPreset p in AccentPalette.Presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.EnglishName));
            Assert.False(string.IsNullOrWhiteSpace(p.ItalianName));
            Assert.NotEqual(AccentPalette.CustomId, p.Id);
        }
        Assert.NotEqual("PRX Orbit (default)", AccentPalette.Presets[0].ItalianName);
    }

    [Theory]
    [InlineData("#2563EB", true)]
    [InlineData("2563eb", true)]
    [InlineData("#abc", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("#12345", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("rgb(1,2,3)", false)]
    [InlineData("#1234567", false)]
    public void Hex_parsing_is_strict(string? text, bool valid)
    {
        Assert.Equal(valid, AccentPalette.TryParse(text, out _));
    }

    [Fact]
    public void A_custom_accent_uses_its_color_and_a_broken_one_falls_back_to_orbit()
    {
        Assert.Equal("#2563EB", AccentPalette.Resolve("custom", "2563eb"));
        Assert.Null(AccentPalette.Resolve("custom", "nonsense"));
        Assert.Null(AccentPalette.Resolve("custom", null));
        Assert.Equal("#0F766E", AccentPalette.Resolve("teal", null));
    }

    [Fact]
    public void Contrast_matches_the_wcag_reference_values()
    {
        Assert.Equal(21.0, AccentPalette.Contrast(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 2);
        Assert.Equal(1.0, AccentPalette.Contrast(new Rgb(120, 40, 200), new Rgb(120, 40, 200)), 5);
        Assert.True(AccentPalette.Contrast(new Rgb(0x76, 0x76, 0x76), new Rgb(255, 255, 255)) >= 4.5);
    }

    [Theory]
    [MemberData(nameof(AccentPresets))]
    public void Every_preset_is_readable_in_light_and_dark(string id)
    {
        AccentPreset preset = AccentPalette.Find(id);
        Assert.True(AccentPalette.TryParse(preset.Hex, out Rgb accent));
        foreach (bool dark in new[] { false, true })
        {
            AccentColors c = AccentPalette.Derive(accent, dark);
            foreach ((string name, Rgb fill) in new[] { ("fill", c.Fill), ("hover", c.FillHover), ("pressed", c.FillPressed) })
                Assert.True(AccentPalette.Contrast(fill, c.OnFill) >= AccentPalette.MinimumContrast,
                    id + " " + (dark ? "dark" : "light") + " " + name + " text on fill: " + AccentPalette.Contrast(fill, c.OnFill));
            foreach (Rgb surface in dark ? AccentPalette.DarkSurfaces : AccentPalette.LightSurfaces)
                Assert.True(AccentPalette.Contrast(c.TextOnSurface, surface) >= AccentPalette.MinimumContrast,
                    id + " " + (dark ? "dark" : "light") + " accent text on surface " + surface.Hex);
        }
    }

    [Fact]
    public void Even_awkward_colors_are_nudged_until_they_pass()
    {
        // Mid-tone, pure and near-grey colours are the hard cases for white-or-black text.
        foreach (string hex in new[] { "#808080", "#FFFF00", "#00FF00", "#FF00FF", "#777777", "#0000FF", "#FFFFFF", "#000000", "#7F7F00", "#FF8800" })
        {
            Assert.True(AccentPalette.TryParse(hex, out Rgb accent));
            foreach (bool dark in new[] { false, true })
            {
                AccentColors c = AccentPalette.Derive(accent, dark);
                Assert.True(AccentPalette.Contrast(c.Fill, c.OnFill) >= AccentPalette.MinimumContrast, hex + " fill " + dark);
                Assert.True(AccentPalette.Contrast(c.FillHover, c.OnFill) >= AccentPalette.MinimumContrast, hex + " hover " + dark);
                Assert.True(AccentPalette.Contrast(c.FillPressed, c.OnFill) >= AccentPalette.MinimumContrast, hex + " pressed " + dark);
                foreach (Rgb surface in dark ? AccentPalette.DarkSurfaces : AccentPalette.LightSurfaces)
                    Assert.True(AccentPalette.Contrast(c.TextOnSurface, surface) >= AccentPalette.MinimumContrast, hex + " text " + dark);
            }
        }
    }

    [Fact]
    public void A_derived_accent_stays_recognisably_the_chosen_hue()
    {
        AccentPalette.TryParse("#2563EB", out Rgb azure);
        AccentColors c = AccentPalette.Derive(azure, dark: false);
        Assert.True(c.Fill.B > c.Fill.R && c.Fill.B > c.Fill.G);
        Assert.True(c.TextOnSurface.B > c.TextOnSurface.R);
        // Hover and pressed are distinguishable steps, not the same colour.
        Assert.NotEqual(c.Fill, c.FillHover);
        Assert.NotEqual(c.FillHover, c.FillPressed);
    }

    [Fact]
    public void The_hex_round_trips()
    {
        Assert.True(AccentPalette.TryParse("#0f766e", out Rgb c));
        Assert.Equal("#0F766E", c.Hex);
    }
}
