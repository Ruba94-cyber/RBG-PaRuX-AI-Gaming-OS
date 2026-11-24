using PaRuX.Server.Orchestration.Gaming;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>Opt-in probe: set PARUX_PLAYNITE_LIBRARY to a COPY of a Playnite library folder to see what the importer would do. Skipped otherwise.</summary>
public sealed class PlayniteRealEnvironmentProbe(ITestOutputHelper output)
{
    /// <summary>Opt-in READ-ONLY probe of the real Playnite on this PC (PARUX_PLAYNITE_REAL=1): detection and Aniki theme state. Starts and writes nothing.</summary>
    [Fact]
    public void Describe_the_real_playnite_and_theme_without_changing_anything()
    {
        if (Environment.GetEnvironmentVariable("PARUX_PLAYNITE_REAL") != "1") return;
        var env = new PaRuX.Server.Orchestration.Gaming.Playnite.WindowsPlayniteEnvironment();
        var c = new PaRuX.Server.Orchestration.Gaming.Playnite.PlayniteLocator(env).Detect(null);
        output.WriteLine($"PLAYNITE detected={c.Detected} kind={c.InstallKind} source={c.Source} ver={c.Version} fullscreen={c.FullscreenExe is not null} running={c.Running}");
        string temp = Path.Combine(Path.GetTempPath(), "parux-real-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PaRuX.Server.Orchestration.Gaming.Playnite.PlayniteThemeStore(Path.Combine(temp, "theme.json"));
            var svc = new PaRuX.Server.Orchestration.Gaming.Playnite.PlayniteThemeService(new HttpClient(), env, store, () => null, Path.Combine(temp, "w"), Path.Combine(temp, "b"));
            var t = svc.Detect();
            output.WriteLine($"THEME detected={t.Detected} folder={t.ThemeFolderName} ver={t.Version} selectedTheme={t.SelectedTheme} selected={t.Selected} configReadable={t.ConfigReadable} helperOnDisk={t.AnikiHelperFoundOnDisk} license={t.LicenseFile} ownership={t.Ownership}");
            Assert.True(c.Detected);
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
    }
}

public sealed class PlayniteRealLibraryProbe(ITestOutputHelper output)
{
    [Fact]
    public void Describe_a_real_library_copy_when_provided()
    {
        string? folder = Environment.GetEnvironmentVariable("PARUX_PLAYNITE_LIBRARY");
        if (string.IsNullOrWhiteSpace(folder)) return;
        foreach (PlayniteImportGame g in PlayniteLibraryReader.Read(folder))
            output.WriteLine($"{(g.Importable ? "IMPORT" : "skip  ")} | {g.Kind,-11} | {g.Name} | {g.EmulatorId} | rom={g.GameFilePath.Length > 0} cover={g.CoverPath.Length > 0} fav={g.Favorite} sec={g.PlaySeconds} | {g.SkipReason}");
    }
}
