using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameExtensionManifestTests
{
    private static byte[] B(string json) => Encoding.UTF8.GetBytes(json);

    private const string Valid = """
    {"schema":1,"id":"demo.theme","name":"Demo","version":"1.0.0","author":"Tester",
     "theme":{"appearance":"dark","accent":"#8B6CFF","background":"#14111F"}}
    """;

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void The_shipped_sample_extension_is_valid_and_complete()
    {
        string dir = Path.Combine(RepoRoot(), "docs", "extensions", "sample.midnight-library");
        GameExtensionValidationResult r = GameExtensionManifestValidator.Validate(File.ReadAllBytes(Path.Combine(dir, "extension.json")));
        Assert.True(r.Valid, string.Join("; ", r.Issues.Select(i => i.Path + ": " + i.Message)));
        Assert.Equal("sample.midnight-library", r.Manifest!.Id);
        Assert.NotNull(r.Manifest.Theme);
        Assert.Single(r.Manifest.MetadataSources);
        Assert.Single(r.Manifest.LaunchPresets);
        // The sample folder holds only data files, so the store accepts it as is.
        Assert.All(Directory.GetFileSystemEntries(dir), e => Assert.Contains(Path.GetFileName(e), new[] { "extension.json", "README.md" }));
    }

    [Fact]
    public void A_minimal_theme_manifest_is_valid()
    {
        GameExtensionValidationResult r = GameExtensionManifestValidator.Validate(B(Valid));
        Assert.True(r.Valid);
        Assert.Equal("#8B6CFF", r.Manifest!.Theme!.Accent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"schema":2,"id":"demo.theme","name":"D","version":"1.0.0","author":"A","theme":{"appearance":"dark","accent":"#112233"}}""")]
    [InlineData("""{"schema":1,"id":"demo.theme","name":"D","version":"1.0","author":"A","theme":{"appearance":"dark","accent":"#112233"}}""")]
    [InlineData("""{"schema":1,"id":"demo.theme","name":"D","version":"1.0.0","author":"A"}""")]
    public void Structurally_wrong_manifests_are_refused(string json) =>
        Assert.False(GameExtensionManifestValidator.Validate(B(json)).Valid);

    [Fact]
    public void Unknown_and_duplicate_fields_are_refused()
    {
        Assert.Contains(GameExtensionManifestValidator.Validate(B(Valid.Replace("\"author\":\"Tester\",", "\"author\":\"Tester\",\"run\":\"calc.exe\","))).Issues,
            i => i.Message.Contains("Unknown field"));
        Assert.Contains(GameExtensionManifestValidator.Validate(B(Valid.Replace("\"author\":\"Tester\",", "\"author\":\"Tester\",\"author\":\"Other\","))).Issues,
            i => i.Message.Contains("Duplicate field"));
        Assert.Contains(GameExtensionManifestValidator.Validate(B(Valid.Replace("\"accent\"", "\"script\":\"x\",\"accent\""))).Issues,
            i => i.Message.Contains("Unknown field"));
    }

    [Fact]
    public void Oversize_and_deeply_nested_input_is_refused_without_parsing_it_all()
    {
        byte[] big = new byte[GameExtensionManifestValidator.MaxManifestBytes + 1];
        Array.Fill(big, (byte)' ');
        Assert.Contains("larger", GameExtensionManifestValidator.Validate(big).Issues[0].Message);
        string deep = new string('[', 200) + new string(']', 200);
        Assert.False(GameExtensionManifestValidator.Validate(B(deep)).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Valid.Replace("\"Demo\"", "\"" + new string('x', 5000) + "\""))).Valid);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("Upper.Case")]
    [InlineData("a..b")]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("trailing.")]
    [InlineData("-leading")]
    [InlineData("con:ads")]
    public void Ids_that_could_escape_the_extensions_folder_are_refused(string id)
    {
        Assert.False(GameExtensionManifestValidator.IsValidId(id));
        string json = Valid.Replace("demo.theme", id.Replace("\\", "\\\\"));
        Assert.False(GameExtensionManifestValidator.Validate(B(json)).Valid);
    }

    [Theory]
    [InlineData("\"<script>alert(1)</script>\"")]
    [InlineData("\"evil\\u202Ename\"")]
    [InlineData("\"line\\nbreak\"")]
    [InlineData("\"nul\\u0000char\"")]
    public void Display_text_with_markup_or_control_characters_is_refused(string name) =>
        Assert.False(GameExtensionManifestValidator.Validate(B(Valid.Replace("\"Demo\"", name))).Valid);

    [Theory]
    [InlineData("#GGGGGG")]
    [InlineData("8B6CFF")]
    [InlineData("#8B6CF")]
    [InlineData("red")]
    [InlineData("javascript:alert(1)")]
    public void Bad_colours_are_refused(string accent) =>
        Assert.False(GameExtensionManifestValidator.Validate(B(Valid.Replace("#8B6CFF", accent))).Valid);

    [Fact]
    public void A_background_must_keep_text_readable_for_its_appearance()
    {
        // Mid grey is below 4.5:1 against white text.
        Assert.False(GameExtensionManifestValidator.Validate(B(Valid.Replace("#14111F", "#808080"))).Valid);
        // A light background in a dark palette is unreadable too.
        Assert.False(GameExtensionManifestValidator.Validate(B(Valid.Replace("#14111F", "#FFFFFF"))).Valid);
        // A light palette with a light background is fine.
        Assert.True(GameExtensionManifestValidator.Validate(B(Valid.Replace("\"dark\"", "\"light\"").Replace("#14111F", "#F4F1FF"))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Valid.Replace("\"dark\"", "\"neon\""))).Valid);
    }

    [Theory]
    [InlineData("https://wiki.example.org/search?q={title}", true)]
    [InlineData("https://wiki.example.org/{platform}/{title}", true)]
    [InlineData("http://wiki.example.org/search?q={title}", false)]
    [InlineData("https://127.0.0.1/search?q={title}", false)]
    [InlineData("https://localhost/search?q={title}", false)]
    [InlineData("https://printer.local/search?q={title}", false)]
    [InlineData("https://user@wiki.example.org/search?q={title}", false)]
    [InlineData("https://wiki.example.org:8443/search?q={title}", false)]
    [InlineData("https://{title}.example.org/search", false)]
    [InlineData("https://wiki.example.org/search?q={query}", false)]
    [InlineData("https://wiki.example.org/search", false)]
    [InlineData("https://wiki.example.org", false)]
    [InlineData("https://wiki.example.org/../admin?q={title}", false)]
    [InlineData("https://wiki.example.org/search?q={title} x", false)]
    [InlineData("file:///c:/windows/system32/calc.exe?{title}", false)]
    [InlineData("javascript:alert({title})", false)]
    [InlineData("https://[::1]/search?q={title}", false)]
    public void Lookup_url_templates_are_https_public_hosts_with_only_known_placeholders(string template, bool ok) =>
        Assert.Equal(ok, GameExtensionManifestValidator.ValidateUrlTemplate(template) is null);

    [Fact]
    public void A_lookup_url_escapes_the_title_and_never_changes_the_host()
    {
        var src = new GameExtensionMetadataSource { Id = "abc", Name = "n", UrlTemplate = "https://wiki.example.org/search?q={title}&p={platform}" };
        string url = GameExtensionManifestValidator.BuildLookupUrl(src, "Evil/../?x=1#y @host.com", "PlayStation 2")!;
        Assert.StartsWith("https://wiki.example.org/search?q=", url);
        Assert.Equal("wiki.example.org", new Uri(url).Host);
        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("#y", url);
        Assert.Null(GameExtensionManifestValidator.BuildLookupUrl(src, "  ", null));
        Assert.Null(GameExtensionManifestValidator.BuildLookupUrl(new GameExtensionMetadataSource { UrlTemplate = "http://x.example.org/{title}" }, "t", null));
    }

    [Theory]
    [InlineData("-windowed", true)]
    [InlineData("--fullscreen=0", true)]
    [InlineData("&calc.exe", false)]
    [InlineData("a|b", false)]
    [InlineData("$env", false)]
    [InlineData("%PATH%", false)]
    [InlineData("--x=..\\..\\boot", false)]
    [InlineData("C:\\tools\\evil.exe", false)]
    [InlineData("\\\\server\\share\\a.exe", false)]
    [InlineData("{width}", false)]
    [InlineData("two words", false)]
    [InlineData("", false)]
    public void Launch_arguments_follow_the_launch_profile_safety_rules(string arg, bool ok) =>
        Assert.Equal(ok, GameExtensionManifestValidator.ValidateLaunchArg(arg) is null);

    [Fact]
    public void Launch_presets_validate_their_enums_counts_and_arguments()
    {
        const string preset = """{"id":"p-one","name":"One","controllerPreset":"xbox","performanceProfile":"quiet","fullscreen":true,"launchArgs":["-a","-b"]}""";
        string Wrap(string p) => "{\"schema\":1,\"id\":\"demo.preset\",\"name\":\"D\",\"version\":\"1.0.0\",\"author\":\"A\",\"launchPresets\":[" + p + "]}";
        Assert.True(GameExtensionManifestValidator.Validate(B(Wrap(preset))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Wrap(preset.Replace("xbox", "hacker")))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Wrap(preset.Replace("quiet", "turbo")))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Wrap(preset.Replace("\"-a\"", "\"&calc\"")))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Wrap(preset.Replace("true", "\"yes\"")))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Wrap(preset.Replace("[\"-a\",\"-b\"]", "[" + string.Join(",", Enumerable.Repeat("\"-a\"", 9)) + "]")))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Wrap(string.Join(",", Enumerable.Repeat(preset, 11))))).Valid);
        Assert.False(GameExtensionManifestValidator.Validate(B(Wrap(preset + "," + preset))).Valid);   // duplicate id
    }

    [Fact]
    public void Too_many_metadata_sources_are_refused()
    {
        string one(int i) => "{\"id\":\"src-" + i + "\",\"name\":\"S\",\"urlTemplate\":\"https://wiki.example.org/{title}\"}";
        string json = "{\"schema\":1,\"id\":\"demo.src\",\"name\":\"D\",\"version\":\"1.0.0\",\"author\":\"A\",\"metadataSources\":[" + string.Join(",", Enumerable.Range(0, 6).Select(one)) + "]}";
        Assert.False(GameExtensionManifestValidator.Validate(B(json)).Valid);
    }
}

public sealed class GameExtensionStoreTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "parux-ext-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _state;
    public GameExtensionStoreTests() { _root = Path.Combine(_base, "Extensions"); _state = Path.Combine(_base, "state"); Directory.CreateDirectory(_root); }
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private GameExtensionStore Store() => new(_root, _state);

    private static string Manifest(string id, string name = "Demo") =>
        "{\"schema\":1,\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"version\":\"1.0.0\",\"author\":\"Tester\"," +
        "\"theme\":{\"appearance\":\"dark\",\"accent\":\"#8B6CFF\",\"background\":\"#14111F\"}}";

    private string Install(string id, string? manifest = null, string? folder = null)
    {
        string dir = Path.Combine(_root, folder ?? id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extension.json"), manifest ?? Manifest(id));
        return dir;
    }

    [Fact]
    public void An_empty_or_missing_root_lists_nothing()
    {
        Assert.Empty(Store().List());
        Directory.Delete(_root, true);
        Assert.Empty(Store().List());
    }

    [Fact]
    public void A_valid_extension_is_listed_with_its_hash_and_starts_disabled()
    {
        string dir = Install("demo.theme");
        GameExtensionInfo info = Assert.Single(Store().List());
        Assert.True(info.Valid);
        Assert.False(info.Enabled);
        Assert.Equal("Demo", info.Name);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "extension.json")))).ToLowerInvariant(), info.Sha256);
        Assert.Equal("none", info.PublisherHash);
    }

    [Fact]
    public void Enable_and_disable_persist_and_never_touch_the_files()
    {
        string dir = Install("demo.theme");
        string before = File.ReadAllText(Path.Combine(dir, "extension.json"));
        Assert.Null(Store().SetEnabled("demo.theme", true));
        Assert.True(Assert.Single(new GameExtensionStore(_root, _state).List()).Enabled);   // a fresh store reads the saved choice
        Assert.Null(Store().SetEnabled("demo.theme", false));
        Assert.False(Assert.Single(Store().List()).Enabled);
        Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "extension.json")));
    }

    [Fact]
    public void A_manifest_changed_after_enabling_switches_itself_off_until_enabled_again()
    {
        string dir = Install("demo.theme");
        Store().SetEnabled("demo.theme", true);
        File.WriteAllText(Path.Combine(dir, "extension.json"), Manifest("demo.theme", "Changed"));
        GameExtensionInfo changed = Assert.Single(Store().List());
        Assert.False(changed.Enabled);
        Assert.True(changed.ChangedSinceEnabled);
        Assert.Null(Store().SetEnabled("demo.theme", true));
        Assert.True(Assert.Single(Store().List()).Enabled);
    }

    [Fact]
    public void An_extension_with_a_program_or_a_subfolder_is_invalid_and_cannot_be_enabled()
    {
        string dir = Install("demo.theme");
        File.WriteAllText(Path.Combine(dir, "helper.exe"), "MZ");
        GameExtensionInfo info = Assert.Single(Store().List());
        Assert.False(info.Valid);
        Assert.Contains(info.Issues, i => i.Contains("helper.exe"));
        Assert.NotNull(Store().SetEnabled("demo.theme", true));

        File.Delete(Path.Combine(dir, "helper.exe"));
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        Assert.Contains(Assert.Single(Store().List()).Issues, i => i.Contains("plugins"));
    }

    [Fact]
    public void A_folder_whose_name_differs_from_the_id_is_invalid()
    {
        Install("demo.theme", folder: "other-name");
        GameExtensionInfo info = Assert.Single(Store().List());
        Assert.False(info.Valid);
        Assert.Contains(info.Issues, i => i.Contains("folder name"));
    }

    [Fact]
    public void A_missing_oversize_or_malicious_manifest_is_reported_not_thrown()
    {
        Directory.CreateDirectory(Path.Combine(_root, "no-manifest"));
        string big = Install("big-one");
        File.WriteAllBytes(Path.Combine(big, "extension.json"), new byte[GameExtensionManifestValidator.MaxManifestBytes + 10]);
        Install("bad-json", "{ not json");
        Install("hostile", Manifest("hostile").Replace("\"author\"", "\"exec\":\"cmd /c calc\",\"author\""));
        IReadOnlyList<GameExtensionInfo> all = Store().List();
        Assert.Equal(4, all.Count);
        Assert.All(all, i => Assert.False(i.Valid));
        Assert.Contains(all.Single(i => i.FolderName == "no-manifest").Issues, m => m.Contains("missing"));
        Assert.Contains(all.Single(i => i.FolderName == "hostile").Issues, m => m.Contains("Unknown field"));
    }

    [Fact]
    public void A_publisher_hash_is_compared_and_a_mismatch_blocks_enabling()
    {
        string dir = Install("demo.theme");
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "extension.json")))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(dir, "extension.sha256"), sha.ToUpperInvariant() + "  extension.json\n");
        GameExtensionInfo ok = Assert.Single(Store().List());
        Assert.Equal("match", ok.PublisherHash);
        Assert.True(ok.Valid);

        File.WriteAllText(Path.Combine(dir, "extension.sha256"), new string('0', 64));
        GameExtensionInfo bad = Assert.Single(Store().List());
        Assert.Equal("mismatch", bad.PublisherHash);
        Assert.False(bad.Valid);
        Assert.NotNull(Store().SetEnabled("demo.theme", true));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("UPPER")]
    public void Ids_that_are_not_plain_names_are_refused_for_every_action(string id)
    {
        Assert.NotNull(Store().SetEnabled(id, true));
        Assert.NotNull(Store().Remove(id));
    }

    [Fact]
    public void Remove_deletes_only_the_data_files_and_the_folder_and_forgets_the_choice()
    {
        string dir = Install("demo.theme");
        File.WriteAllText(Path.Combine(dir, "README.md"), "hello");
        Store().SetEnabled("demo.theme", true);
        string outside = Path.Combine(_base, "keep.txt");
        File.WriteAllText(outside, "keep");

        Assert.Null(Store().Remove("demo.theme"));
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(outside));
        Assert.Empty(Store().List());
        Install("demo.theme");
        Assert.False(Assert.Single(Store().List()).Enabled);   // the earlier enable did not survive the removal
        Assert.NotNull(Store().Remove("not-installed"));
    }

    [Fact]
    public void Remove_refuses_a_folder_that_holds_anything_but_extension_data()
    {
        string dir = Install("demo.theme");
        string exe = Path.Combine(dir, "run.exe");
        File.WriteAllText(exe, "MZ");
        string? reason = Store().Remove("demo.theme");
        Assert.NotNull(reason);
        Assert.Contains("run.exe", reason);
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(Path.Combine(dir, "extension.json")));   // nothing was deleted
    }

    [Fact]
    public void Enabling_an_extension_that_is_not_installed_is_refused()
    {
        Assert.Contains("not installed", Store().SetEnabled("ghost-ext", true));
    }

    [Fact]
    public void A_corrupt_state_file_falls_back_to_everything_disabled()
    {
        Install("demo.theme");
        Store().SetEnabled("demo.theme", true);
        File.WriteAllText(Path.Combine(_state, "game-extensions.json"), "{ corrupt");
        Assert.False(Assert.Single(Store().List()).Enabled);
    }
}

[Collection("LibraryThemeState")]
public sealed class LibraryThemeStateTests
{
    private static GameExtensionTheme Dark() => new() { Appearance = "dark", Accent = "#8B6CFF", Background = "#14111F" };

    [Fact]
    public void Without_a_theme_the_owners_accent_is_untouched_and_there_is_no_background()
    {
        LibraryThemeState.Set(null);
        Assert.Equal(("orbit", (string?)null), LibraryThemeState.EffectiveAccent("orbit", null));
        Assert.Equal(("ocean", (string?)"#123456"), LibraryThemeState.EffectiveAccent("ocean", "#123456"));
        Assert.Null(LibraryThemeState.BackgroundHex(true));
    }

    [Fact]
    public void An_active_theme_replaces_the_accent_through_the_custom_slot_and_clearing_restores_the_choice()
    {
        try
        {
            LibraryThemeState.Set(Dark());
            Assert.Equal((AccentPalette.CustomId, (string?)"#8B6CFF"), LibraryThemeState.EffectiveAccent("ocean", null));
            // The resolved accent is a normal custom colour, so the existing palette keeps text readable on it.
            Assert.Equal("#8B6CFF", AccentPalette.Resolve(AccentPalette.CustomId, "#8B6CFF"));
        }
        finally { LibraryThemeState.Set(null); }
        Assert.Equal(("ocean", (string?)null), LibraryThemeState.EffectiveAccent("ocean", null));
    }

    [Fact]
    public void The_background_only_applies_when_the_app_theme_matches_the_palette()
    {
        try
        {
            LibraryThemeState.Set(Dark());
            Assert.Equal("#14111F", LibraryThemeState.BackgroundHex(appIsDark: true));
            Assert.Null(LibraryThemeState.BackgroundHex(appIsDark: false));
        }
        finally { LibraryThemeState.Set(null); }
    }

    [Fact]
    public void A_theme_with_an_unparsable_accent_is_ignored_and_changes_are_announced()
    {
        int raised = 0;
        void Handler() => raised++;
        LibraryThemeState.Changed += Handler;
        try
        {
            LibraryThemeState.Set(new GameExtensionTheme { Appearance = "dark", Accent = "not-a-colour" });
            Assert.Null(LibraryThemeState.Active);
            LibraryThemeState.Set(Dark());
            Assert.NotNull(LibraryThemeState.Active);
            Assert.Equal(2, raised);
        }
        finally { LibraryThemeState.Changed -= Handler; LibraryThemeState.Set(null); }
    }

    [Fact]
    public void Every_extension_accent_stays_readable_through_the_existing_derivation()
    {
        foreach (string accent in new[] { "#8B6CFF", "#FFFF00", "#000000", "#FFFFFF", "#808080" })
        {
            Assert.True(AccentPalette.TryParse(accent, out Rgb rgb));
            foreach (bool dark in new[] { true, false })
            {
                AccentColors c = AccentPalette.Derive(rgb, dark);
                Rgb[] surfaces = dark ? AccentPalette.DarkSurfaces : AccentPalette.LightSurfaces;
                Assert.All(surfaces, s => Assert.True(AccentPalette.Contrast(c.TextOnSurface, s) >= AccentPalette.MinimumContrast, accent + " dark=" + dark));
                Assert.True(AccentPalette.Contrast(c.Fill, c.OnFill) >= AccentPalette.MinimumContrast, accent + " fill");
            }
        }
    }
}
