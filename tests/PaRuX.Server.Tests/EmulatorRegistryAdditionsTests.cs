using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>New emulator recipes (shadPS4, mGBA, ScummVM, Xenia, Snes9x) and the static validation applied to every recipe.</summary>
public sealed class EmulatorRegistryAdditionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-emu-add-" + Guid.NewGuid().ToString("N"));

    public EmulatorRegistryAdditionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly string[] NewIds = { "shadps4", "mgba", "scummvm", "xenia", "snes9x" };

    private static string Rom(string name) => Path.Combine(Path.GetTempPath(), "games", name);

    // ------------------------------------------------------------------ every recipe is statically valid

    [Fact]
    public void Every_registered_recipe_passes_the_static_checks()
    {
        string[] problems = EmulatorRegistry.Builtin.SelectMany(EmulatorRecipeValidator.Problems).ToArray();
        Assert.True(problems.Length == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Ids_are_unique_and_the_new_emulators_are_registered_hand_tuned()
    {
        Assert.Equal(EmulatorRegistry.Builtin.Count, EmulatorRegistry.Builtin.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string id in NewIds)
        {
            EmulatorDefinition? d = EmulatorRegistry.Find(id);
            Assert.NotNull(d);
            Assert.Equal("unverified", d!.TemplateStatus);   // honest until tried against a real build
        }
    }

    [Fact]
    public void The_validator_catches_each_kind_of_broken_recipe()
    {
        EmulatorDefinition Good() => EmulatorRegistry.Find("mgba")!;
        static string Joined(EmulatorDefinition d) => string.Join("|", EmulatorRecipeValidator.Problems(d));

        Assert.Empty(EmulatorRecipeValidator.Problems(Good()));
        Assert.Contains("unknown placeholder", Joined(Good() with { Template = new[] { "{rom}", "{bogus}" } }));
        Assert.Contains("never passes the game", Joined(Good() with { Template = new[] { "{extra}" } }));
        Assert.Contains("no {fullscreen}", Joined(Good() with { Template = new[] { "{rom}" } }));
        Assert.Contains("needs a core", Joined(Good() with { RequiresCore = true }));
        Assert.Contains("RequiresCore is false", Joined(Good() with { Template = new[] { "-L", "{core}", "{rom}" } }));
        Assert.Contains("plain .exe", Joined(Good() with { ExecutableNames = new[] { @"..\evil.bat" } }));
        Assert.Contains("lowercase", Joined(Good() with { Id = "Bad Id" }));
        Assert.Contains("must look like .iso", Joined(Good() with { GameExtensions = new[] { "gba" } }));
        Assert.Contains("own", Joined(Good() with { Template = new[] { "--x={extra}", "{rom}" } }));
        Assert.Contains("literal", Joined(Good() with { FullscreenArguments = new[] { "{rom}" } }));
        Assert.Contains("more than once", Joined(Good() with { Template = new[] { "{fullscreen}", "{fullscreen}", "{rom}" } }));
    }

    // ------------------------------------------------------------------ exact launch arguments

    [Theory]
    [InlineData("shadps4", "-g C:\\x\\game\\eboot.bin", "-f true -g C:\\x\\game\\eboot.bin", "-f false -g C:\\x\\game\\eboot.bin", "C:\\x\\game\\eboot.bin")]
    [InlineData("mgba", "C:\\x\\a.gba", "-f C:\\x\\a.gba", null, "C:\\x\\a.gba")]
    [InlineData("xenia", "C:\\x\\a.iso", "--fullscreen C:\\x\\a.iso", "--fullscreen=false C:\\x\\a.iso", "C:\\x\\a.iso")]
    [InlineData("snes9x", "C:\\x\\a.sfc", "-fullscreen C:\\x\\a.sfc", null, "C:\\x\\a.sfc")]
    public void Recipes_expand_to_exactly_the_documented_arguments(string id, string normal, string fullscreen, string? windowed, string rom)
    {
        EmulatorDefinition d = EmulatorRegistry.Find(id)!;
        Assert.Equal(normal, EmulatorCommandBuilder.Build(d, rom, false, null, null).Arguments);
        Assert.Equal(fullscreen, EmulatorCommandBuilder.Build(d, rom, true, null, null).Arguments);
        if (windowed != null)
            Assert.Equal(windowed, EmulatorCommandBuilder.Build(d, rom, false, null, null, EmulatorLaunchModes.Windowed).Arguments);
        else
            Assert.NotNull(EmulatorCommandBuilder.Build(d, rom, false, null, null, EmulatorLaunchModes.Windowed).Error);   // no documented switch: refused, not guessed
    }

    [Fact]
    public void ScummVM_starts_the_game_found_in_the_chosen_folder()
    {
        EmulatorDefinition d = EmulatorRegistry.Find("scummvm")!;
        EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(d, @"C:\Adventures\Monkey Island\monkey.scummvm", true, null, null);

        Assert.Null(plan.Error);
        Assert.Equal(new[] { "--fullscreen", @"--path=C:\Adventures\Monkey Island", "--auto-detect" }, plan.ArgumentList);
        Assert.Equal("--fullscreen \"--path=C:\\Adventures\\Monkey Island\" --auto-detect", plan.Arguments);   // one quoted argument, not two
    }

    [Fact]
    public void Extra_arguments_go_first_and_unsafe_input_is_refused()
    {
        EmulatorDefinition d = EmulatorRegistry.Find("shadps4")!;
        Assert.Equal("--log-level debug -g C:\\x\\eboot.bin", EmulatorCommandBuilder.Build(d, "C:\\x\\eboot.bin", false, "--log-level debug", null).Arguments);
        Assert.NotNull(EmulatorCommandBuilder.Build(d, "C:\\x\\eboot.bin", false, "a\nb", null).Error);
        Assert.NotNull(EmulatorCommandBuilder.Build(d, "relative\\eboot.bin", false, null, null).Error);
        Assert.NotNull(EmulatorCommandBuilder.Build(d, "C:\\x\\ebo\0ot.bin", false, null, null).Error);
        Assert.Contains("\"C:\\My Games\\eboot.bin\"", EmulatorCommandBuilder.Build(d, "C:\\My Games\\eboot.bin", false, null, null).Arguments);
    }

    // ------------------------------------------------------------------ detection and ROM-folder matching

    [Fact]
    public void Installed_emulators_are_detected_by_their_executable_names()
    {
        string tools = Path.Combine(_root, "tools");
        foreach ((string folder, string exe) in new[] { ("shad", "shadPS4.exe"), ("gba", "mGBA.exe"), ("scumm", "scummvm.exe"), ("xe", "xenia_canary.exe"), ("snes", "snes9x-x64.exe") })
        {
            Directory.CreateDirectory(Path.Combine(tools, folder));
            File.WriteAllBytes(Path.Combine(tools, folder, exe), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        }

        string[] found = EmulatorDiscovery.Detect(new[] { tools }).Select(e => e.EmulatorId).ToArray();

        foreach (string id in NewIds) Assert.Contains(id, found);
        Assert.Contains(EmulatorDiscovery.Detect(new[] { tools }), e => e.EmulatorId == "xenia" && e.ExecutablePath.EndsWith("xenia_canary.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_folder_of_games_is_matched_per_emulator_and_other_files_are_ignored()
    {
        string library = Path.Combine(_root, "library");
        Directory.CreateDirectory(Path.Combine(library, "CUSA01234"));
        File.WriteAllText(Path.Combine(library, "CUSA01234", "eboot.bin"), "x");
        File.WriteAllText(Path.Combine(library, "CUSA01234", "param.sfo"), "x");
        File.WriteAllText(Path.Combine(library, "Pokemon.GBA"), "x");
        File.WriteAllText(Path.Combine(library, "Mario.sfc"), "x");
        File.WriteAllText(Path.Combine(library, "Halo.iso"), "x");
        Directory.CreateDirectory(Path.Combine(library, "Monkey Island"));
        File.WriteAllText(Path.Combine(library, "Monkey Island", "monkey.scummvm"), "x");
        File.WriteAllText(Path.Combine(library, "readme.txt"), "x");
        File.WriteAllText(Path.Combine(library, "notes.docx"), "x");

        string[] Titles(string id) => EmulatorDiscovery.Scan(EmulatorRegistry.Find(id)!, library).Games.Select(g => Path.GetFileName(g.Path)).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "eboot.bin" }, Titles("shadps4"));
        Assert.Equal(new[] { "Pokemon.GBA" }, Titles("mgba"));              // extension match ignores case
        Assert.Equal(new[] { "monkey.scummvm" }, Titles("scummvm"));
        Assert.Equal(new[] { "Mario.sfc" }, Titles("snes9x"));
        Assert.Equal(new[] { "Halo.iso" }, Titles("xenia"));
        Assert.Empty(EmulatorDiscovery.Scan(EmulatorRegistry.Find("mgba")!, library).Games.Where(g => g.Path.EndsWith(".txt")));
    }

    [Fact]
    public void The_game_file_rule_matches_what_scanning_finds()
    {
        EmulatorDefinition d = EmulatorRegistry.Find("shadps4")!;
        Assert.True(EmulatorRegistry.IsGameFile(d, @"C:\g\CUSA1\EBOOT.BIN"));
        Assert.False(EmulatorRegistry.IsGameFile(d, @"C:\g\CUSA1\param.sfo"));
        Assert.True(EmulatorRegistry.IsGameFile(EmulatorRegistry.Find("mgba")!, @"C:\g\X.GBC"));
    }
}
