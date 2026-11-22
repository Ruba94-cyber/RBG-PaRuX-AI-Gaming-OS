using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Playnite;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PlayniteLocatorTests
{
    // IsSafeHelperPath needs real files, so the folder fixtures below are real temp folders.
    private static string MakeFolder(bool desktop = true, bool fullscreen = true, bool portable = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-pl-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (desktop) File.WriteAllBytes(Path.Combine(dir, "Playnite.DesktopApp.exe"), new byte[] { 1 });
        if (fullscreen) File.WriteAllBytes(Path.Combine(dir, "Playnite.FullscreenApp.exe"), new byte[] { 1 });
        if (portable) File.WriteAllText(Path.Combine(dir, "portable.dat"), "");
        return dir;
    }

    private static FakeEnv EnvWith(string dir)
    {
        var env = new FakeEnv();
        foreach (string f in Directory.GetFiles(dir)) env.Files.Add(f);
        return env;
    }

    [Fact]
    public void Nothing_found_is_reported_as_not_detected_with_install_offered()
    {
        PlayniteComponentDto c = new PlayniteLocator(new FakeEnv()).Detect();
        Assert.False(c.Detected);
        Assert.Contains("install", c.Actions);
        Assert.Contains("locate", c.Actions);
        Assert.Contains("skip", c.Actions);
    }

    [Fact]
    public void Registry_install_is_found_with_version_and_both_executables()
    {
        string dir = MakeFolder();
        FakeEnv env = EnvWith(dir);
        env.Versions[Path.Combine(dir, "Playnite.DesktopApp.exe")] = "10.65";
        env.Uninstall.Add(new PlayniteUninstallEntry("Playnite", dir, "10.0", null));
        PlayniteComponentDto c = new PlayniteLocator(env).Detect();
        Assert.True(c.Detected);
        Assert.Equal("registry", c.Source);
        Assert.Equal("installed", c.InstallKind);
        Assert.Equal("10.65", c.Version);
        Assert.NotNull(c.DesktopExe);
        Assert.NotNull(c.FullscreenExe);
        Assert.Equal(ComponentOwnership.SYSTEM_DETECTED, c.Ownership);
    }

    [Fact]
    public void Registry_entry_without_install_location_uses_the_icon_folder_and_version_hint()
    {
        string dir = MakeFolder();
        FakeEnv env = EnvWith(dir);
        env.Uninstall.Add(new PlayniteUninstallEntry("Playnite", null, "9.9", "\"" + Path.Combine(dir, "Playnite.DesktopApp.exe") + "\",0"));
        PlayniteComponentDto c = new PlayniteLocator(env).Detect();
        Assert.True(c.Detected);
        Assert.Equal("9.9", c.Version);
    }

    [Fact]
    public void Local_app_data_install_is_found()
    {
        string dir = MakeFolder();
        FakeEnv env = EnvWith(dir);
        env.LocalAppData = Path.GetDirectoryName(dir)!;
        string target = Path.Combine(env.LocalAppData, "Playnite");
        Directory.CreateDirectory(target);
        try
        {
            File.Copy(Path.Combine(dir, "Playnite.DesktopApp.exe"), Path.Combine(target, "Playnite.DesktopApp.exe"), true);
            env.Files.Add(Path.Combine(target, "Playnite.DesktopApp.exe"));
            PlayniteComponentDto c = new PlayniteLocator(env).Detect();
            Assert.True(c.Detected);
            Assert.Equal("local-app-data", c.Source);
            Assert.Null(c.FullscreenExe);
        }
        finally { Directory.Delete(target, true); }
    }

    [Fact]
    public void Program_files_install_is_identified_with_the_correct_source()
    {
        string root = MakeFolder();
        string installed = Path.Combine(root, "Playnite");
        Directory.CreateDirectory(installed);
        try
        {
            string desktop = Path.Combine(installed, "Playnite.DesktopApp.exe");
            File.WriteAllBytes(desktop, new byte[] { 1 });
            var env = new FakeEnv { ProgramFiles = root };
            env.Files.Add(desktop);

            PlayniteComponentDto component = new PlayniteLocator(env).Detect();

            Assert.True(component.Detected);
            Assert.Equal("program-files", component.Source);
            Assert.Equal(installed, component.Folder);
            Assert.Equal(ComponentOwnership.SYSTEM_DETECTED, component.Ownership);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Portable_folder_with_portable_marker_is_classified_portable()
    {
        string dir = MakeFolder(portable: true);
        FakeEnv env = EnvWith(dir);
        var loc = new PlayniteLocator(env);
        PlayniteComponentDto c = loc.Detect(dir);
        Assert.True(c.Detected);
        Assert.Equal(ComponentOwnership.USER_MANAGED, c.Ownership);
    }

    [Fact]
    public void Running_process_is_enough_to_detect_when_nothing_else_is_known()
    {
        string dir = MakeFolder();
        FakeEnv env = EnvWith(dir);
        env.Running.Add(new PlayniteRunningProcess(10, Path.Combine(dir, "Playnite.DesktopApp.exe")));
        PlayniteComponentDto c = new PlayniteLocator(env).Detect();
        Assert.True(c.Detected);
        Assert.Equal("process", c.Source);
        Assert.True(c.Running);
    }

    [Fact]
    public void A_folder_without_the_official_executables_is_rejected()
    {
        string dir = MakeFolder(desktop: false, fullscreen: false);
        Assert.Null(new PlayniteLocator(EnvWith(dir)).FromFolder(dir, "located", "user-located"));
    }

    [Theory]
    [InlineData(@"\\server\share\Playnite")]
    [InlineData("relative\\path")]
    [InlineData("")]
    public void Network_relative_and_empty_paths_are_rejected(string path) =>
        Assert.Null(new PlayniteLocator(new FakeEnv()).FromFolder(path, "located", "user-located"));
}

public sealed class PlayniteLauncherTests
{
    private static (PlayniteLauncher L, FakeHost H, FakeEnv E, PlayniteComponentDto C, string Dir) Make(bool displayConnected = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-pl-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Playnite.DesktopApp.exe"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "Playnite.FullscreenApp.exe"), new byte[] { 1 });
        var env = new FakeEnv();
        var host = new FakeHost();
        foreach (string f in Directory.GetFiles(dir)) env.Files.Add(f);
        var comp = new PlayniteLocator(env).FromFolder(dir, "installed", "registry")!;
        return (new PlayniteLauncher(host, env, _ => displayConnected), host, env, comp, dir);
    }

    [Fact]
    public void Desktop_start_uses_the_install_folder_as_working_directory()
    {
        var (l, h, _, c, dir) = Make();
        var r = l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null);
        Assert.True(r.Ok);
        Assert.Equal("started", r.Outcome);
        Assert.EndsWith("Playnite.DesktopApp.exe", h.Starts.Single().Exe);
        Assert.Equal(dir, h.Starts.Single().Dir);
        Assert.True(l.PaRuXLaunched);
    }

    [Fact]
    public void Console_start_uses_the_fullscreen_executable()
    {
        var (l, h, _, c, _) = Make();
        var r = l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Console, null);
        Assert.Equal(PlayniteModes.Console, r.Mode);
        Assert.EndsWith("Playnite.FullscreenApp.exe", h.Starts.Single().Exe);
    }

    [Fact]
    public void Saved_start_mode_applies_when_no_mode_is_requested()
    {
        var (l, h, _, c, _) = Make();
        l.Launch(c, new PlayniteSettingsDto { StartMode = PlayniteModes.Console }, null, null);
        Assert.EndsWith("Playnite.FullscreenApp.exe", h.Starts.Single().Exe);
    }

    [Fact]
    public void Second_launch_focuses_the_running_instance_instead_of_starting_another()
    {
        var (l, h, e, c, dir) = Make();
        e.Running.Add(new PlayniteRunningProcess(321, Path.Combine(dir, "Playnite.DesktopApp.exe")));
        var r = l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null);
        Assert.True(r.Ok);
        Assert.Equal("already-running", r.Outcome);
        Assert.Empty(h.Starts);
        Assert.Equal(new[] { 321 }, h.Focused);
    }

    [Fact]
    public void A_just_started_owned_playnite_is_not_started_twice_when_discovery_lags()
    {
        var (l, h, e, c, _) = Make();
        Assert.True(l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Console, null).Ok);
        Assert.Empty(e.Running); // The Windows discovery snapshot has not caught up yet.

        PlayniteLaunchResultDto again = l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Console, null);

        Assert.True(again.Ok);
        Assert.Equal("already-running", again.Outcome);
        Assert.Equal(PlayniteModes.Console, again.Mode);
        Assert.Single(h.Starts);
        Assert.Equal(new[] { h.Last!.Id }, h.Focused);
    }

    [Fact]
    public void A_just_started_owned_playnite_refuses_the_other_mode_without_creating_an_instance()
    {
        var (l, h, e, c, _) = Make();
        Assert.True(l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null).Ok);
        Assert.Empty(e.Running);

        PlayniteLaunchResultDto result = l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Console, null);

        Assert.False(result.Ok);
        Assert.Equal("already-running", result.Outcome);
        Assert.Equal(PlayniteModes.Desktop, result.Mode);
        Assert.Single(h.Starts);
        Assert.Equal(0, h.Last!.CloseCalls);
    }

    [Fact]
    public void An_exited_owned_playnite_can_be_restarted_when_windows_discovery_is_empty()
    {
        var (l, h, e, c, _) = Make();
        Assert.True(l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Console, null).Ok);
        h.Last!.ExitNow();
        Assert.Empty(e.Running);

        PlayniteLaunchResultDto result = l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Outcome);
        Assert.Equal(2, h.Starts.Count);
        Assert.Equal(PlayniteModes.Desktop, l.LaunchedMode);
    }

    [Fact]
    public void Running_in_the_other_mode_is_not_duplicated_nor_claimed_as_success()
    {
        var (l, h, e, c, dir) = Make();
        e.Running.Add(new PlayniteRunningProcess(321, Path.Combine(dir, "Playnite.DesktopApp.exe")));
        var r = l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Console, null);
        Assert.False(r.Ok);
        Assert.Empty(h.Starts);
    }

    [Fact]
    public void Console_when_the_chosen_display_is_missing_falls_back_to_desktop()
    {
        var (l, h, _, c, _) = Make(displayConnected: false);
        var s = new PlayniteSettingsDto { ConsoleOnlyWhenTargetConnected = true, TargetDisplayId = "TV1" };
        var r = l.Launch(c, s, PlayniteModes.Console, null);
        Assert.Equal(PlayniteModes.Desktop, r.Mode);
        Assert.False(h.Starts.Single().Minimized);
        Assert.NotNull(r.Message);
    }

    [Fact]
    public void Console_when_the_display_is_missing_can_fall_back_to_minimized()
    {
        var (l, h, _, c, _) = Make(displayConnected: false);
        var s = new PlayniteSettingsDto { ConsoleOnlyWhenTargetConnected = true, TargetDisplayId = "TV1", FallbackWithoutDisplay = ConsoleStartupPolicy.FallbackMinimized };
        l.Launch(c, s, PlayniteModes.Console, null);
        Assert.True(h.Starts.Single().Minimized);
    }

    [Fact]
    public void Console_when_the_display_is_connected_starts_fullscreen()
    {
        var (l, h, _, c, _) = Make(displayConnected: true);
        var s = new PlayniteSettingsDto { ConsoleOnlyWhenTargetConnected = true, TargetDisplayId = "TV1" };
        l.Launch(c, s, PlayniteModes.Console, null);
        Assert.EndsWith("Playnite.FullscreenApp.exe", h.Starts.Single().Exe);
    }

    [Fact]
    public void Skipped_or_missing_playnite_is_refused_without_starting_anything()
    {
        var (l, h, _, c, _) = Make();
        Assert.Equal("refused", l.Launch(c, new PlayniteSettingsDto { Skipped = true }, null, null).Outcome);
        Assert.Equal("refused", l.Launch(new PlayniteComponentDto(), new PlayniteSettingsDto(), null, null).Outcome);
        Assert.Empty(h.Starts);
    }

    [Fact]
    public void Closing_a_playnite_that_paRuX_started_is_counted_for_the_return_to_paRuX()
    {
        var (l, h, _, c, _) = Make();
        l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null);
        long before = l.ClosedCounter;
        h.Last!.ExitNow();
        Assert.Equal(before + 1, l.ClosedCounter);
        Assert.False(l.PaRuXLaunched);
    }

    [Fact]
    public void Switch_closes_only_a_playnite_paRuX_started_and_starts_the_other_mode()
    {
        var (l, h, _, c, _) = Make();
        l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null);
        var r = l.Switch(c, new PlayniteSettingsDto(), gameRunning: false, null);
        Assert.True(r.Ok);
        Assert.Equal(PlayniteModes.Console, r.Mode);
        Assert.Equal(1, h.Starts.Count - 1);
    }

    [Fact]
    public void Switch_refuses_a_playnite_paRuX_did_not_start()
    {
        var (l, h, e, c, dir) = Make();
        e.Running.Add(new PlayniteRunningProcess(321, Path.Combine(dir, "Playnite.DesktopApp.exe")));
        var r = l.Switch(c, new PlayniteSettingsDto(), false, null);
        Assert.False(r.Ok);
        Assert.Empty(h.Starts);
    }

    [Fact]
    public void Switch_refuses_while_a_game_runs()
    {
        var (l, h, _, c, _) = Make();
        l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null);
        var r = l.Switch(c, new PlayniteSettingsDto(), gameRunning: true, null);
        Assert.False(r.Ok);
        Assert.Equal(0, h.Last!.CloseCalls);
    }

    [Fact]
    public void Switch_does_not_close_desktop_when_required_tv_is_disconnected()
    {
        var (l, h, _, c, _) = Make(displayConnected: false);
        var settings = new PlayniteSettingsDto { ConsoleOnlyWhenTargetConnected = true, TargetDisplayId = "TV1" };
        Assert.True(l.Launch(c, settings, PlayniteModes.Desktop, null).Ok);

        PlayniteLaunchResultDto result = l.Switch(c, settings, gameRunning: false, displayId: null);

        Assert.False(result.Ok);
        Assert.Equal("refused", result.Outcome);
        Assert.Contains("display", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Starts);
        Assert.Equal(0, h.Last!.CloseCalls);
        Assert.True(l.PaRuXLaunched);
        Assert.Equal(PlayniteModes.Desktop, l.LaunchedMode);
    }

    [Fact]
    public void Switch_does_not_close_desktop_when_fullscreen_executable_is_missing()
    {
        var (l, h, _, c, _) = Make();
        Assert.True(l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null).Ok);
        c.FullscreenExe = null;

        PlayniteLaunchResultDto result = l.Switch(c, new PlayniteSettingsDto(), gameRunning: false, displayId: null);

        Assert.False(result.Ok);
        Assert.Single(h.Starts);
        Assert.Equal(0, h.Last!.CloseCalls);
        Assert.True(l.PaRuXLaunched);
    }

    [Fact]
    public void Switch_does_not_close_desktop_when_playnite_was_skipped()
    {
        var (l, h, _, c, _) = Make();
        Assert.True(l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null).Ok);

        PlayniteLaunchResultDto result = l.Switch(c,
            new PlayniteSettingsDto { Skipped = true }, gameRunning: false, displayId: null);

        Assert.False(result.Ok);
        Assert.Single(h.Starts);
        Assert.Equal(0, h.Last!.CloseCalls);
        Assert.True(l.PaRuXLaunched);
    }

    [Fact]
    public void Switch_does_not_close_desktop_if_playnite_component_disappears()
    {
        var (l, h, _, c, _) = Make();
        Assert.True(l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null).Ok);
        c.Detected = false;

        PlayniteLaunchResultDto result = l.Switch(c, new PlayniteSettingsDto(), gameRunning: false, displayId: null);

        Assert.False(result.Ok);
        Assert.Single(h.Starts);
        Assert.Equal(0, h.Last!.CloseCalls);
        Assert.True(l.PaRuXLaunched);
    }

    [Fact]
    public void Switch_never_force_closes_a_playnite_that_ignores_the_close_request()
    {
        var (l, h, _, c, _) = Make();
        l.Launch(c, new PlayniteSettingsDto(), PlayniteModes.Desktop, null);
        h.Last!.ExitsOnClose = false;
        var r = l.Switch(c, new PlayniteSettingsDto(), false, null);
        Assert.False(r.Ok);
        Assert.Single(h.Starts);
    }
}

public sealed class PlayniteSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "parux-pl-set-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }

    [Fact]
    public void Defaults_have_ai_yield_on_and_desktop_mode()
    {
        var s = new PlayniteSettingsStore(_path).Get();
        Assert.True(s.AiYieldEnabled);
        Assert.Equal(PlayniteModes.Desktop, s.StartMode);
        Assert.False(s.Skipped);
    }

    [Fact]
    public void Saved_settings_survive_a_restart_and_invalid_values_are_refused()
    {
        var store = new PlayniteSettingsStore(_path);
        Assert.NotNull(store.Save(new PlayniteSettingsDto { AiYieldEnabled = false, StartMode = PlayniteModes.Console }).Saved);
        Assert.Null(store.Save(new PlayniteSettingsDto { StartMode = "weird" }).Saved);
        Assert.Null(store.Save(new PlayniteSettingsDto { FallbackWithoutDisplay = "hide" }).Saved);
        var again = new PlayniteSettingsStore(_path).Get();
        Assert.False(again.AiYieldEnabled);
        Assert.Equal(PlayniteModes.Console, again.StartMode);
    }

    [Fact]
    public void A_damaged_file_means_defaults_and_is_not_overwritten_by_reading()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.True(new PlayniteSettingsStore(_path).Get().AiYieldEnabled);
        Assert.Equal("{ not json", File.ReadAllText(_path));
    }
}

public sealed class PlayniteReturnWatchPolicyTests
{
    [Fact]
    public void A_playnite_that_exits_before_first_poll_still_triggers_return()
    {
        var status = new PlayniteStatusDto
        {
            Settings = new PlayniteSettingsDto { ReturnToPaRuXOnClose = true },
            Session = new PlayniteSessionDto
            {
                PaRuXLaunchedPlaynite = false,
                PaRuXLaunchedClosedCounter = 8
            }
        };

        Assert.True(PlayniteReturnWatchPolicy.ShouldBringForward(7, status));
    }

    [Fact]
    public void A_mode_switch_does_not_return_while_the_replacement_playnite_is_running()
    {
        var status = new PlayniteStatusDto
        {
            Session = new PlayniteSessionDto
            {
                PaRuXLaunchedPlaynite = true,
                PaRuXLaunchedClosedCounter = 12
            }
        };

        Assert.False(PlayniteReturnWatchPolicy.ShouldBringForward(11, status));
        status.Session.PaRuXLaunchedPlaynite = false;
        status.Session.PaRuXLaunchedClosedCounter++;
        Assert.True(PlayniteReturnWatchPolicy.ShouldBringForward(12, status));
    }

    [Fact]
    public void A_game_surviving_playnite_close_defers_return_until_the_game_exits()
    {
        var status = new PlayniteStatusDto
        {
            Session = new PlayniteSessionDto
            {
                PaRuXLaunchedPlaynite = false,
                PaRuXLaunchedClosedCounter = 4,
                TrackedProcessCount = 1
            }
        };

        Assert.False(PlayniteReturnWatchPolicy.ShouldBringForward(3, status));
        Assert.True(PlayniteReturnWatchPolicy.KeepPendingClose(3, status));

        status.Session.TrackedProcessCount = 0;

        Assert.True(PlayniteReturnWatchPolicy.ShouldBringForward(3, status));
        Assert.False(PlayniteReturnWatchPolicy.KeepPendingClose(3, status));
    }

    [Fact]
    public void Return_is_refused_when_disabled_or_counter_unknown_or_unchanged()
    {
        var status = new PlayniteStatusDto
        {
            Settings = new PlayniteSettingsDto { ReturnToPaRuXOnClose = false },
            Session = new PlayniteSessionDto { PaRuXLaunchedClosedCounter = 3 }
        };

        Assert.False(PlayniteReturnWatchPolicy.ShouldBringForward(2, status));
        status.Settings.ReturnToPaRuXOnClose = true;
        Assert.False(PlayniteReturnWatchPolicy.ShouldBringForward(null, status));
        Assert.False(PlayniteReturnWatchPolicy.ShouldBringForward(3, status));
        Assert.False(PlayniteReturnWatchPolicy.ShouldBringForward(4, status));
        Assert.False(PlayniteReturnWatchPolicy.ShouldBringForward(2, null));
    }
}

public sealed class PlayniteInstallerTests : IDisposable
{
    private const string Url = "https://github.com/JosefNemec/Playnite/releases/download/10.65/Playnite1065.exe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-pl-inst-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string ReleaseJson(string? digest, string tag = "10.65", string url = Url, bool prerelease = false, string name = "Playnite1065.exe", int size = 5) =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + (prerelease ? "true" : "false") + ",\"draft\":false,\"assets\":[" +
        "{\"name\":\"10.65.7z\",\"size\":9,\"browser_download_url\":\"https://github.com/JosefNemec/Playnite/releases/download/10.65/10.65.7z\"}," +
        "{\"name\":\"" + name + "\",\"size\":" + size + ",\"digest\":" + (digest is null ? "null" : "\"" + digest + "\"") +
        ",\"browser_download_url\":\"" + url + "\"}]}";

    private sealed class Handler : HttpMessageHandler
    {
        public string Release = "";
        public byte[] Payload = Array.Empty<byte>();
        public int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release), RequestMessage = request });
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload), RequestMessage = request });
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private (PlayniteInstaller I, Handler H, List<string[]> Runs) Make(byte[] payload, string? digest, bool makeInstallerCreateFiles = true)
    {
        var h = new Handler { Payload = payload, Release = ReleaseJson(digest, size: payload.Length) };
        var runs = new List<string[]>();
        string target = Path.Combine(_root, "Playnite");
        var installer = new PlayniteInstaller(new HttpClient(h), target, Path.Combine(_root, "work"), (exe, args, ct) =>
        {
            runs.Add(args);
            if (makeInstallerCreateFiles) { Directory.CreateDirectory(target); File.WriteAllBytes(Path.Combine(target, "Playnite.DesktopApp.exe"), new byte[] { 1 }); }
            return Task.FromResult(0);
        });
        return (installer, h, runs);
    }

    [Fact]
    public void Parser_picks_the_installer_with_its_published_checksum()
    {
        PlayniteInstallPlanDto p = PlayniteReleaseParser.Parse(ReleaseJson("sha256:" + new string('a', 64)), @"C:\t");
        Assert.True(p.Available);
        Assert.Equal("10.65", p.Version);
        Assert.Equal("Playnite1065.exe", p.AssetName);
        Assert.Equal(new string('a', 64), p.Sha256);
    }

    [Fact]
    public void Parser_reports_a_release_without_checksum_as_unverifiable()
    {
        PlayniteInstallPlanDto p = PlayniteReleaseParser.Parse(ReleaseJson(null), @"C:\t");
        Assert.True(p.Available);
        Assert.Null(p.Sha256);
        Assert.NotNull(p.Message);
    }

    [Fact]
    public void Parser_refuses_prereleases_untrusted_hosts_and_missing_installers()
    {
        Assert.False(PlayniteReleaseParser.Parse(ReleaseJson(null, prerelease: true), "x").Available);
        Assert.False(PlayniteReleaseParser.Parse(ReleaseJson(null, url: "https://evil.example/Playnite1065.exe"), "x").Available);
        Assert.False(PlayniteReleaseParser.Parse(ReleaseJson(null, url: "https://github.com/other/repo/releases/download/1/Playnite1065.exe"), "x").Available);
        Assert.False(PlayniteReleaseParser.Parse(ReleaseJson(null, url: "http://github.com/JosefNemec/Playnite/releases/download/10.65/Playnite1065.exe"), "x").Available);
        Assert.False(PlayniteReleaseParser.Parse(ReleaseJson(null, name: "Other.exe"), "x").Available);
        Assert.False(PlayniteReleaseParser.Parse("{ nope", "x").Available);
    }

    [Fact]
    public async Task Verified_download_runs_the_per_user_installer_once()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        var (i, h, runs) = Make(payload, "sha256:" + Sha(payload));
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.65", Sha256 = Sha(payload) }, CancellationToken.None);
        Assert.Equal("completed", i.Status.Stage);
        string[] args = Assert.Single(runs);
        Assert.Contains("/CURRENTUSER", args);
        Assert.Contains(args, a => a.StartsWith("/DIR=", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_root, "work", "Playnite1065.exe")));
    }

    [Fact]
    public async Task Checksum_mismatch_deletes_the_file_and_never_runs_the_installer()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        var (i, h, runs) = Make(payload, "sha256:" + new string('b', 64));
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.65", Sha256 = new string('b', 64) }, CancellationToken.None);
        Assert.Equal("failed", i.Status.Stage);
        Assert.Empty(runs);
        Assert.False(File.Exists(Path.Combine(_root, "work", "Playnite1065.exe")));
    }

    [Fact]
    public async Task A_checksum_the_owner_did_not_confirm_is_refused_before_downloading()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        var (i, h, runs) = Make(payload, "sha256:" + Sha(payload));
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.65", Sha256 = new string('c', 64) }, CancellationToken.None);
        Assert.Equal("failed", i.Status.Stage);
        Assert.Equal(0, h.Downloads);
        Assert.Empty(runs);
    }

    [Fact]
    public async Task Release_without_checksum_needs_explicit_unverified_acceptance()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        var (i, h, runs) = Make(payload, null);
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.65" }, CancellationToken.None);
        Assert.Equal("failed", i.Status.Stage);
        Assert.Equal(0, h.Downloads);
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.65", AcceptUnverified = true }, CancellationToken.None);
        Assert.Equal("completed", i.Status.Stage);
        Assert.Single(runs);
    }

    [Fact]
    public async Task A_version_that_changed_since_the_owner_confirmed_is_refused()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        var (i, h, runs) = Make(payload, "sha256:" + Sha(payload));
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.60", Sha256 = Sha(payload) }, CancellationToken.None);
        Assert.Equal("failed", i.Status.Stage);
        Assert.Empty(runs);
    }

    [Fact]
    public async Task An_existing_playnite_folder_is_never_overwritten()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        var (i, h, runs) = Make(payload, "sha256:" + Sha(payload));
        Directory.CreateDirectory(Path.Combine(_root, "Playnite"));
        File.WriteAllText(Path.Combine(_root, "Playnite", "mine.txt"), "x");
        PlayniteInstallPlanDto plan = await i.ResolveAsync(CancellationToken.None);
        Assert.False(plan.Available);
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.65", Sha256 = Sha(payload) }, CancellationToken.None);
        Assert.Equal("failed", i.Status.Stage);
        Assert.Empty(runs);
        Assert.Equal(0, h.Downloads);
    }

    [Fact]
    public void Begin_without_consent_is_refused()
    {
        var (i, _, runs) = Make(new byte[] { 1 }, null);
        Assert.NotNull(i.Begin(new PlayniteInstallRequest { Consent = false, Version = "10.65" }));
        Assert.Empty(runs);
    }

    [Fact]
    public async Task Installer_that_fails_or_leaves_nothing_is_reported_as_failed()
    {
        byte[] payload = Encoding.ASCII.GetBytes("hello");
        var (i, _, runs) = Make(payload, "sha256:" + Sha(payload), makeInstallerCreateFiles: false);
        await i.InstallAsync(new PlayniteInstallRequest { Consent = true, Version = "10.65", Sha256 = Sha(payload) }, CancellationToken.None);
        Assert.Equal("failed", i.Status.Stage);
        Assert.Single(runs);
    }
}
