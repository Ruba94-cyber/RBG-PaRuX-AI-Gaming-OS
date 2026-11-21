using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Eden 2 builds ship as lichii.exe and use .nup packages (the owner's Mario Kart World setup).</summary>
public sealed class EmulatorEden2Tests
{
    [Fact]
    public void Eden_accepts_the_lichii_executable_and_nup_packages_with_the_same_command_line()
    {
        EmulatorDefinition eden = EmulatorRegistry.Find("eden")!;
        Assert.Contains("lichii.exe", eden.ExecutableNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".nup", eden.GameExtensions, StringComparer.OrdinalIgnoreCase);
        Assert.True(EmulatorRegistry.IsGameFile(eden, @"C:\Games\Mario Kart World\games\MKW.nup"));

        EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(eden, @"C:\Games\Mario Kart World\games\MKW.nup", fullscreen: true,
            extraArguments: null, corePath: null, launchMode: null, inputProfile: null, pcsx2BigPicture: false);
        Assert.Null(plan.Error);
        Assert.Contains("-g", plan.ArgumentList);
        Assert.Contains(@"C:\Games\Mario Kart World\games\MKW.nup", plan.ArgumentList);
    }

    [Fact]
    public void Every_registered_recipe_still_passes_the_static_checks()
    {
        string[] problems = EmulatorRegistry.Builtin.SelectMany(EmulatorRecipeValidator.Problems).ToArray();
        Assert.True(problems.Length == 0, string.Join("\n", problems));
    }
}
