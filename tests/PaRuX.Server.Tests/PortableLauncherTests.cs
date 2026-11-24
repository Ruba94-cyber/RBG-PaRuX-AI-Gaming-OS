using System.Diagnostics;
using PaRuX.Launcher;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The root PaRuX.exe of the layered portable packages. src/PaRuX.Launcher's LauncherCore and KillOnCloseJob are
/// compiled into this test assembly and exercised with a stand-in application (a copy of cmd.exe placed at
/// app\PaRuX.exe): path resolution, argument and exit-code propagation, working directory, the missing-app
/// message and the kill-on-close Job Object. The published single-file launcher itself is not started here.
/// </summary>
public sealed class PortableLauncherTests : IDisposable
{
    private readonly string _package = Path.Combine(Path.GetTempPath(), "parux-launcher-" + Guid.NewGuid().ToString("N"));

    public PortableLauncherTests() => Directory.CreateDirectory(_package);

    public void Dispose()
    {
        try { Directory.Delete(_package, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A package whose "application" is cmd.exe: it returns the exit code it is told to.</summary>
    private string PackageWithFakeApp()
    {
        string app = Path.Combine(_package, "app");
        Directory.CreateDirectory(app);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(app, "PaRuX.exe"));
        return _package;
    }

    [Fact]
    public void The_plan_points_at_app_PaRuX_exe_next_to_the_launcher_and_keeps_every_argument()
    {
        string[] args = { "--exit", "--flag=value", "path with spaces", "" };

        PaRuX.Launcher.LaunchPlan plan = LauncherCore.Plan(_package, args);

        Assert.Equal(Path.Combine(_package, "app", "PaRuX.exe"), plan.AppExecutable);
        Assert.Equal(Path.Combine(_package, "app"), plan.WorkingDirectory);
        Assert.Equal(args, plan.Arguments);
    }

    [Fact]
    public void A_missing_application_reports_the_expected_path_and_returns_a_nonzero_code_without_starting_anything()
    {
        var messages = new List<string>();

        int exit = LauncherCore.Run(_package, Array.Empty<string>(), messages.Add);

        Assert.NotEqual(0, exit);
        Assert.Equal(LauncherCore.ExitAppMissing, exit);
        string message = Assert.Single(messages);
        Assert.Contains(Path.Combine(_package, "app", "PaRuX.exe"), message, StringComparison.Ordinal);
        Assert.Contains("Extract the complete ZIP again", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launcher_that_sits_in_a_legacy_flat_folder_does_not_run_a_sibling_PaRuX_exe()
    {
        // PaRuX.exe beside the launcher is the launcher itself in a real package; only app\PaRuX.exe counts.
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(_package, "PaRuX.exe"));
        var messages = new List<string>();

        Assert.Equal(LauncherCore.ExitAppMissing, LauncherCore.Run(_package, new[] { "/c", "exit", "0" }, messages.Add));
        Assert.Single(messages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(2)]   // what "PaRuX.exe --exit" returns when no instance was running
    [InlineData(123)]
    public void The_exit_code_of_the_application_is_the_exit_code_of_the_launcher(int code)
    {
        string package = PackageWithFakeApp();
        var messages = new List<string>();

        int exit = LauncherCore.Run(package, new[] { "/c", "exit", code.ToString() }, messages.Add);

        Assert.Equal(code, exit);
        Assert.Empty(messages);
    }

    [Fact]
    public void Arguments_with_spaces_reach_the_application_as_one_argument()
    {
        string package = PackageWithFakeApp();

        // cmd.exe sees:  /c "exit 9"   (one quoted argument built by ProcessStartInfo.ArgumentList)
        int exit = LauncherCore.Run(package, new[] { "/c", "exit 9" }, _ => { });

        Assert.Equal(9, exit);
    }

    [Fact]
    public void The_application_starts_with_app_as_its_working_directory()
    {
        string package = PackageWithFakeApp();

        // Succeeds with 11 only when the working directory is app\ (where the stand-in PaRuX.exe sits).
        int exit = LauncherCore.Run(package, new[] { "/c", "if exist PaRuX.exe (exit 11) else (exit 12)" }, _ => { });

        Assert.Equal(11, exit);
    }

    [Fact]
    public void The_job_object_kills_the_child_when_the_last_handle_closes()
    {
        string ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        var info = new ProcessStartInfo(ping) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-n", "60", "127.0.0.1" }) info.ArgumentList.Add(a);
        using Process child = Process.Start(info)!;
        try
        {
            KillOnCloseJob? job = KillOnCloseJob.TryCreate();
            Assert.NotNull(job);
            Assert.True(job!.TryAssign(child), "The child could not be assigned to the job object.");
            Assert.False(child.HasExited);

            job.Dispose();   // the launcher dying has exactly this effect: the last job handle is closed

            Assert.True(child.WaitForExit(10_000), "The child survived the closing of its kill-on-close job.");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void The_launcher_sources_stay_offline_unelevated_and_without_a_console()
    {
        string root = RepoRoot();
        string project = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Launcher", "PaRuX.Launcher.csproj"));
        string manifest = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Launcher", "app.manifest"));
        string sources = string.Concat(Directory.GetFiles(Path.Combine(root, "src", "PaRuX.Launcher"), "*.cs").Select(File.ReadAllText));

        Assert.Contains("<OutputType>WinExe</OutputType>", project, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>PaRuX</AssemblyName>", project, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>true</SelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishTrimmed>true</PublishTrimmed>", project, StringComparison.Ordinal);
        Assert.Contains("<Version>$(PaRuXProductVersion)</Version>", project, StringComparison.Ordinal);
        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("requireAdministrator", manifest, StringComparison.Ordinal);

        foreach (string forbidden in new[] { "System.Net", "HttpClient", "Socket", "Verb = \"runas\"", "runas", "UseShellExecute = true" })
            Assert.DoesNotContain(forbidden, sources, StringComparison.Ordinal);
        Assert.Contains("0x2000", sources, StringComparison.Ordinal); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        Assert.Contains("PARUX_LAUNCHER_NO_UI", sources, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_version_contract_covers_the_launcher_project()
    {
        string root = RepoRoot();
        string versionTest = File.ReadAllText(Path.Combine(root, "tests", "PaRuX.Server.Tests", "ReleaseVersionSourceContractTests.cs"));
        Assert.Contains("PaRuX.Launcher", versionTest, StringComparison.Ordinal);
        Assert.Contains("PaRuX.Launcher", File.ReadAllText(Path.Combine(root, "PaRuX.sln")), StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
