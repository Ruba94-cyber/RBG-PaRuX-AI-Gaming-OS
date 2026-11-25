using System.Diagnostics;
using System.Text;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The layered portable layout contract: the ZIP root folder holds ONLY PaRuX.exe plus app, PaRuXData, docs
/// and launchers. The rule lives in scripts/portable-layout.ps1 and is used by BOTH builders to verify the
/// finished archive, so it is executed here for real (Windows PowerShell), not just read as text.
/// </summary>
public sealed class PortablePackageLayoutTests
{
    private static readonly string[] ValidListing =
    {
        "PaRuX.exe",
        "app/PaRuX.exe", "app/PaRuX.Server/PaRuX.Server.exe", "app/parux.corporate", "app/Assets/parux.ico", "app/de-DE/PaRuX.resources.dll",
        "PaRuXData/models/model.gguf",
        "docs/README-corporate-lite.txt", "docs/LICENSE.txt", "docs/MANIFEST.json",
        "launchers/Start PaRuX Corporate Lite.cmd",
    };

    [Fact]
    public void A_package_with_only_the_launcher_and_the_four_folders_is_valid()
    {
        Assert.Empty(Violations(ValidListing));
    }

    [Theory]
    [InlineData("PaRuX.pri")]
    [InlineData("README-portable.txt")]
    [InlineData("parux.portable")]
    [InlineData("parux.corporate")]
    [InlineData("hostfxr.dll")]
    [InlineData("MANIFEST.json")]
    [InlineData("Start PaRuX Corporate Lite.cmd")]
    public void Any_extra_file_at_the_root_is_rejected(string file)
    {
        string[] problems = Violations(ValidListing.Append(file).ToArray());
        Assert.Contains(problems, p => p.Contains("unexpected file at the package root", StringComparison.Ordinal) && p.Contains(file, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PaRuX.Server/PaRuX.Server.exe")]
    [InlineData("Assets/logo.png")]
    [InlineData("de-DE/PaRuX.resources.dll")]
    [InlineData("Cli/parux.exe")]
    [InlineData("runtime-baseline/x.dll")]
    [InlineData("App/PaRuX.exe")]       // wrong case: the folder name is exactly "app"
    public void A_folder_other_than_the_four_allowed_ones_is_rejected(string entry)
    {
        string[] problems = Violations(ValidListing.Append(entry).ToArray());
        Assert.Contains(problems, p => p.Contains("unexpected folder at the package root", StringComparison.Ordinal));
    }

    [Fact]
    public void The_real_application_must_exist_inside_app()
    {
        string[] withoutApp = ValidListing.Where(n => n != "app/PaRuX.exe").ToArray();
        Assert.Contains(Violations(withoutApp), p => p.Contains("app/PaRuX.exe is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void The_launcher_must_exist_at_the_root()
    {
        string[] withoutLauncher = ValidListing.Where(n => n != "PaRuX.exe").ToArray();
        Assert.Contains(Violations(withoutLauncher), p => p.Contains("launcher PaRuX.exe is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void The_old_flat_zip_shape_is_rejected()
    {
        // What the packages looked like before: the application and ~376 loose files beside PaRuX.exe.
        string[] flat =
        {
            "PaRuX.exe", "PaRuX.pri", "App.xbf", "coreclr.dll", "LICENSE.txt", "README-portable.txt", "parux.portable",
            "PaRuX.Server/PaRuX.Server.exe", "Assets/parux.ico", "de-DE/PaRuX.resources.dll", "PaRuXData/.portable-data",
        };
        string[] problems = Violations(flat);
        Assert.Contains(problems, p => p.Contains("app/PaRuX.exe is missing", StringComparison.Ordinal));
        Assert.True(problems.Length >= 6, string.Join(" | ", problems));
    }

    [Fact]
    public void Launcher_name_is_case_sensitive_so_a_renamed_file_cannot_pass()
    {
        string[] renamed = ValidListing.Select(n => n == "PaRuX.exe" ? "parux.exe" : n).ToArray();
        string[] problems = Violations(renamed);
        Assert.Contains(problems, p => p.Contains("unexpected file at the package root: parux.exe", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("launcher PaRuX.exe is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Assert_throws_with_every_problem_named()
    {
        string output = RunPowerShell("Assert-PaRuXPackageLayout -Names @('PaRuX.exe','x.dll','Assets/a') -Label 'Test ZIP'", expectFailure: true);
        Assert.Contains("Test ZIP violates the layered layout", output, StringComparison.Ordinal);
        Assert.Contains("x.dll", output, StringComparison.Ordinal);
        Assert.Contains("Assets/", output, StringComparison.Ordinal);
    }

    // ---- both builders and the harnesses really use the rule ---------------------------------------------------------

    [Fact]
    public void Corporate_builder_and_internal_shell_publish_the_launcher_stage_the_app_in_app_and_fail_on_a_dirty_root()
    {
        string shell = Read("scripts", "build-corporate-app-shell.ps1");
        string corporate = Read("scripts", "build-corporate-lite.ps1");

        // The Standard Portable distribution is retired: neither its builder nor its verifier exists any more.
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "scripts", "build-portable.ps1")));
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "tests", "portable", "verify-standard-portable.ps1")));

        foreach (string script in new[] { shell, corporate })
            Assert.Contains("portable-layout.ps1", script, StringComparison.Ordinal);

        // Shell: the application is published into app\, the launcher is the only root file, the stage layout is verified.
        Assert.Contains("Publish-PaRuXLauncher -RepoRoot", shell, StringComparison.Ordinal);
        Assert.Contains("$appDir = Join-Path $stageDir \"app\"", shell, StringComparison.Ordinal);
        Assert.Contains("-o $appDir $common", shell, StringComparison.Ordinal);
        Assert.Contains("-o $coreDir $common", shell, StringComparison.Ordinal);
        Assert.Contains("Assert-PaRuXPackageLayout -Names $names", shell, StringComparison.Ordinal);
        Assert.Contains("\"app/PaRuX.exe\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path $appDir \"parux.portable\"", shell, StringComparison.Ordinal);

        // Corporate: same rule over the finished archive, marker in app\, three .cmd in launchers\ resolving ..\PaRuXData.
        Assert.Contains("Assert-PaRuXPackageLayout -Names $rel -Label \"Corporate Lite ZIP\"", corporate, StringComparison.Ordinal);
        Assert.Contains("Join-Path $appStage \"parux.corporate\"", corporate, StringComparison.Ordinal);
        Assert.Contains("Join-Path $launchersStage \"Start PaRuX Corporate Lite.cmd\"", corporate, StringComparison.Ordinal);
        Assert.Contains("Join-Path $launchersStage \"Start PaRuX Corporate Model Downloads.cmd\"", corporate, StringComparison.Ordinal);
        Assert.Contains("Join-Path $launchersStage \"Start PaRuX Corporate Lite (CPU only).cmd\"", corporate, StringComparison.Ordinal);
        Assert.Contains("Join-Path $docsStage \"MANIFEST.json\"", corporate, StringComparison.Ordinal);
        Assert.Contains("%PARUX_PACKAGE%", corporate, StringComparison.Ordinal);
        Assert.Contains("%~dp0..", corporate, StringComparison.Ordinal);
        Assert.DoesNotContain("%~dp0PaRuXData", corporate, StringComparison.Ordinal);
    }

    [Fact]
    public void Msi_and_msix_builders_keep_their_own_layout_and_never_touch_the_launcher()
    {
        foreach (string script in new[] { "build-msi.ps1", "build-msix.ps1" })
        {
            string text = Read("scripts", script);
            Assert.DoesNotContain("PaRuX.Launcher", text, StringComparison.Ordinal);
            Assert.DoesNotContain("portable-layout", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Publish-PaRuXLauncher", text, StringComparison.Ordinal);
        }
        // MSIX still refuses every portable artefact in its payload.
        string msix = Read("scripts", "build-msix.ps1");
        Assert.Contains("\"parux.portable\", \"PaRuXData\"", msix, StringComparison.Ordinal);
        // The installer project does not reference the launcher.
        Assert.DoesNotContain("PaRuX.Launcher", Read("installer", "PaRuX.Setup", "PaRuX.Setup.wixproj"), StringComparison.Ordinal);
    }

    [Fact]
    public void Harnesses_target_the_launcher_and_the_real_app_explicitly()
    {
        string measure = Read("tests", "corporate", "measure-corporate-lite.ps1");
        Assert.Contains("function Get-PackageAppProcess", measure, StringComparison.Ordinal);
        Assert.Contains("launchers\\Start PaRuX Corporate Lite.cmd", measure, StringComparison.Ordinal);
        Assert.Contains("docs\\MANIFEST.json", measure, StringComparison.Ordinal);
        Assert.Contains("processes_5s_after_launcher_killed", measure, StringComparison.Ordinal);
        Assert.Contains("PaRuX-launcher", measure, StringComparison.Ordinal);
        // CloseMainWindow must never be sent to the (windowless) launcher.
        Assert.Contains("$realApp.CloseMainWindow()", measure, StringComparison.Ordinal);
        Assert.DoesNotContain("$appProc.CloseMainWindow()", measure, StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static string[] Violations(string[] names)
    {
        string list = string.Join(",", names.Select(n => "'" + n.Replace("'", "''") + "'"));
        string output = RunPowerShell("Get-PaRuXPackageLayoutViolations -Names @(" + list + ") | ForEach-Object { $_ }", expectFailure: false);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string RunPowerShell(string command, bool expectFailure)
    {
        string layout = Path.Combine(RepoRoot(), "scripts", "portable-layout.ps1");
        string script = ". '" + layout.Replace("'", "''") + "'\r\n$ErrorActionPreference = 'Stop'\r\ntry { " + command + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 7 }\r\n";
        string file = Path.Combine(Path.GetTempPath(), "parux-layout-test-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }) info.ArgumentList.Add(a);
            using Process process = Process.Start(info)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(60_000), "PowerShell did not finish.");
            string combined = stdout.Result + stderr.Result;
            if (expectFailure) Assert.NotEqual(0, process.ExitCode);
            else Assert.True(process.ExitCode == 0, "PowerShell failed: " + combined);
            return expectFailure ? combined : stdout.Result;
        }
        finally { File.Delete(file); }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string Read(params string[] relative) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
