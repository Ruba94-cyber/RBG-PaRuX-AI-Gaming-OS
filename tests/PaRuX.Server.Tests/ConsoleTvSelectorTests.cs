using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// Console TV selector: persistent monitor history, multi-selection, stable identity, "use current monitor as TV", Console that starts
/// with or without a TV, and the test seam that declares the current primary monitor a TV. Synthetic monitors only; nothing here reads
/// or changes a real display (except the one read-only topology test, which asserts structure and never a name).
/// </summary>
public sealed class ConsoleTvSelectorTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;
    public ConsoleTvSelectorTests(ITestOutputHelper output) => _output = output;

    private static GamingDisplayModeDto Monitor(string displayId, bool primary, string? stable, string name, string? code = "TST0001",
        int x = 0, int w = 1920, int h = 1080) =>
        new(displayId, primary, x, 0, w, h, 60) { FriendlyName = name, StableId = stable, MonitorCode = code };

    private static GamingDisplaySnapshotDto Snapshot(params GamingDisplayModeDto[] displays) => new(true, "synthetic", displays);

    private static byte[] Edid(string vendor3, ushort product, uint serial, string? model, string? textSerial = null)
    {
        var e = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(e, 0);
        int v = ((vendor3[0] - 64) << 10) | ((vendor3[1] - 64) << 5) | (vendor3[2] - 64);
        e[8] = (byte)(v >> 8); e[9] = (byte)v;
        e[10] = (byte)product; e[11] = (byte)(product >> 8);
        e[12] = (byte)serial; e[13] = (byte)(serial >> 8); e[14] = (byte)(serial >> 16); e[15] = (byte)(serial >> 24);
        void Descriptor(int offset, byte tag, string text)
        {
            e[offset + 3] = tag;
            var bytes = System.Text.Encoding.ASCII.GetBytes(text + "\n");
            Array.Copy(bytes, 0, e, offset + 5, Math.Min(13, bytes.Length));
        }
        if (model is not null) Descriptor(54, 0xFC, model);
        if (textSerial is not null) Descriptor(72, 0xFF, textSerial);
        return e;
    }

    // ---- identity --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Edid_gives_a_readable_name_a_model_code_and_a_stable_hashed_identifier()
    {
        var info = ConsoleMonitorIdentity.FromEdid(Edid("TST", 0x1234, 0xDEADBEEF, "Test TV 55"));
        Assert.Equal("TST1234", info.Code);
        Assert.Equal("Test TV 55", info.ModelName);
        Assert.StartsWith("TST1234-", info.StableId);
        Assert.DoesNotContain("DEADBEEF", info.StableId!, StringComparison.OrdinalIgnoreCase);   // never the raw serial
        Assert.DoesNotContain(3735928559u.ToString(), info.StableId!);
        Assert.Equal(info.StableId, ConsoleMonitorIdentity.FromEdid(Edid("TST", 0x1234, 0xDEADBEEF, "Other label")).StableId);
        Assert.NotEqual(info.StableId, ConsoleMonitorIdentity.FromEdid(Edid("TST", 0x1234, 0xDEADBEF0, "Test TV 55")).StableId);
        Assert.True(ConsoleMonitorIdentity.ValidStableId(info.StableId));
    }

    [Fact]
    public void Two_identical_models_without_any_serial_stay_apart_through_the_windows_instance()
    {
        byte[] noSerial = Edid("TST", 0x0042, 0, "Same model");
        string a = ConsoleMonitorIdentity.FromEdid(noSerial, "5&aaaa&0&UID1").StableId!;
        string b = ConsoleMonitorIdentity.FromEdid(noSerial, "5&bbbb&0&UID2").StableId!;
        Assert.NotEqual(a, b);
        Assert.Equal(a, ConsoleMonitorIdentity.FromEdid(noSerial, "5&aaaa&0&UID1").StableId);
    }

    [Fact]
    public void Garbage_edid_yields_no_identity_instead_of_throwing()
    {
        Assert.Null(ConsoleMonitorIdentity.FromEdid(null).StableId);
        Assert.Null(ConsoleMonitorIdentity.FromEdid(new byte[10]).StableId);
        Assert.Null(ConsoleMonitorIdentity.FromEdid(new byte[128]).StableId);
        byte[] badVendor = Edid("TST", 1, 1, null); badVendor[8] = 0; badVendor[9] = 0;
        Assert.Null(ConsoleMonitorIdentity.FromEdid(badVendor).StableId);
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#TST1234#5&1a2b3c4d&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "TST1234", "5&1a2b3c4d&0&UID4357")]
    [InlineData("", null, null)]
    [InlineData("not-an-interface", null, null)]
    [InlineData(@"\\?\DISPLAY##x#{g}", null, null)]
    public void The_windows_monitor_interface_name_is_parsed_defensively(string input, string? code, string? instance) =>
        Assert.Equal((code, instance), ConsoleMonitorIdentity.ParseInterfaceName(input));

    [Fact]
    public void Without_edid_the_instance_path_still_gives_a_stable_but_weaker_identifier()
    {
        var a = ConsoleMonitorIdentity.FromInstancePath("TST1234", "inst-1");
        Assert.Equal(a.StableId, ConsoleMonitorIdentity.FromInstancePath("TST1234", "inst-1").StableId);
        Assert.NotEqual(a.StableId, ConsoleMonitorIdentity.FromInstancePath("TST1234", "inst-2").StableId);
        Assert.Null(ConsoleMonitorIdentity.FromInstancePath(null, "x").StableId);
    }

    [Fact]
    public void The_real_topology_service_rejects_an_oversized_or_control_character_identifier()
    {
        var bad = new GamingDisplayModeDto("d", true, 0, 0, 100, 100, 60) { StableId = new string('x', 65) };
        Assert.False(GamingDisplayTopologyService.ValidMode(bad));
        Assert.True(GamingDisplayTopologyService.ValidMode(new GamingDisplayModeDto("d", true, 0, 0, 100, 100, 60) { StableId = "TST1-abc" }));
    }

    // ---- history, multi-selection, forget ----------------------------------------------------------------------------------------

    [Fact]
    public void Connected_monitors_are_remembered_and_stay_listed_after_they_are_unplugged()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC monitor", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "Living room TV", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now);
        Assert.Equal(2, history.Count);

        var later = ConsoleTvRegistry.RecordSeen(history, Snapshot(pc), Now.AddDays(3));   // TV is off
        Assert.Equal(2, later.Count);
        Assert.Equal(Now, later.Single(e => e.Name == "Living room TV").LastSeenUtc);
        Assert.Equal(Now.AddDays(3), later.Single(e => e.Name == "PC monitor").LastSeenUtc);
    }

    [Fact]
    public void Several_monitors_can_be_chosen_including_ones_that_are_not_connected()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC monitor", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "Living room TV", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now);
        history = ConsoleTvRegistry.SetSelected(history, tv.StableId!, true);
        history = ConsoleTvRegistry.SetSelected(history, pc.StableId!, true);

        Assert.Equal(2, ConsoleTvRegistry.ConnectedTvs(history, Snapshot(pc, tv)).Count);
        var onlyPc = ConsoleTvRegistry.ConnectedTvs(history, Snapshot(pc));
        Assert.Equal("PC monitor", ConsoleTvRegistry.DisplayName(Assert.Single(onlyPc)));
        Assert.True(ConsoleTvRegistry.HasSelection(history));
    }

    [Fact]
    public void Forgetting_removes_one_entry_and_its_selection_and_nothing_else()
    {
        var a = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "A");
        var b = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "B", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(a, b), Now), b.StableId!, true);
        history = ConsoleTvRegistry.Forget(history, b.StableId!);
        Assert.Equal("A", Assert.Single(history).Name);
        Assert.False(ConsoleTvRegistry.HasSelection(history));
        Assert.Single(ConsoleTvRegistry.Forget(history, "does-not-exist"));
    }

    [Fact]
    public void Use_the_current_monitor_as_tv_selects_only_the_primary_and_keeps_other_choices()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC monitor", "AAA0001");
        var other = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "Second", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, other), Now), other.StableId!, true);
        var result = ConsoleTvRegistry.SelectCurrentMonitor(history, Snapshot(pc, other), Now);
        Assert.True(result.Single(e => e.StableId == pc.StableId).Selected);
        Assert.True(result.Single(e => e.StableId == other.StableId).Selected);

        var fresh = ConsoleTvRegistry.SelectCurrentMonitor(null, Snapshot(pc, other), Now);
        Assert.Equal(pc.StableId, Assert.Single(fresh, e => e.Selected).StableId);
        Assert.Empty(ConsoleTvRegistry.SelectCurrentMonitor(null, new GamingDisplaySnapshotDto(false, "x", Array.Empty<GamingDisplayModeDto>()), Now));
    }

    [Fact]
    public void Legacy_launcher_tv_names_select_matching_remembered_monitors_only()
    {
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "Living room TV", "BBB0002", x: 1920);
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "Desk monitor", "AAA0001");
        var history = ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now);
        var (list, count) = ConsoleTvRegistry.SelectByNameHints(history, new[] { "living room", "x", "Unknown TV" });
        Assert.Equal(1, count);
        Assert.True(list.Single(e => e.Name == "Living room TV").Selected);
        Assert.False(list.Single(e => e.Name == "Desk monitor").Selected);
    }

    [Fact]
    public void The_import_preview_lists_what_would_be_selected_what_is_already_chosen_and_what_matches_nothing()
    {
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "Living room TV", "BBB0002", x: 1920);
        var den = Monitor(@"\\.\DISPLAY3", false, "CCC0003-cccccccccccc", "Den TV", "CCC0003", x: 3840);
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "Desk monitor", "AAA0001");
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv, den), Now), den.StableId!, true);
        var hints = new[] { "living room", "den tv", "Bedroom TV", "x" };

        var preview = ConsoleTvRegistry.PreviewNameHints(history, hints);
        Assert.Equal("Living room TV", Assert.Single(preview.WouldSelect).Name);
        Assert.Equal("Den TV", Assert.Single(preview.AlreadySelected).Name);
        Assert.Contains("Bedroom TV", preview.NoMatch);
        Assert.Contains("x", preview.NoMatch);                       // too short to match anything: reported, never applied
        Assert.DoesNotContain(history, e => e.Name == "Living room TV" && e.Selected);   // previewing changes nothing

        var (applied, count) = ConsoleTvRegistry.SelectByNameHints(history, hints);
        Assert.Equal(1, count);
        Assert.False(applied.Single(e => e.Name == "Desk monitor").Selected);
    }

    [Fact]
    public void The_native_console_button_never_blocks_on_a_missing_tv()
    {
        // Contract: the explicit start has no blocking dialog; it uses a discreet bilingual notice and leaves the window where it is.
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "PaRuX.App", "MainWindow.ConsoleMode.cs"));
        Assert.DoesNotContain("TV not available", src);
        Assert.Contains("TV not connected: using the current screen.", src);
        Assert.Contains("TV non collegata: uso lo schermo attuale.", src);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    // ---- stable matching -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_tv_is_found_again_when_windows_renumbers_its_display()
    {
        var tvBefore = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(tvBefore), Now), tvBefore.StableId!, true);
        var tvAfter = Monitor(@"\\.\DISPLAY5", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 0);
        Assert.Equal(@"\\.\DISPLAY5", Assert.Single(ConsoleTvRegistry.ConnectedTvs(history, Snapshot(tvAfter))).DisplayId);
    }

    [Fact]
    public void The_same_model_on_a_changed_identity_matches_only_when_unambiguous()
    {
        var known = Monitor(@"\\.\DISPLAY2", false, "BBB0002-111111111111", "TV", "BBB0002");
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(known), Now), known.StableId!, true);
        var reidentified = Monitor(@"\\.\DISPLAY3", false, "BBB0002-222222222222", "TV", "BBB0002");
        Assert.Single(ConsoleTvRegistry.ConnectedTvs(history, Snapshot(reidentified)));

        var twin = Monitor(@"\\.\DISPLAY4", false, "BBB0002-333333333333", "TV", "BBB0002");
        Assert.Empty(ConsoleTvRegistry.ConnectedTvs(history, Snapshot(reidentified, twin)));           // two candidates: never guess
        var other = Monitor(@"\\.\DISPLAY6", false, "CCC0003-444444444444", "Other", "CCC0003");
        Assert.Empty(ConsoleTvRegistry.ConnectedTvs(history, Snapshot(other)));                         // a different model never matches
    }

    [Fact]
    public void A_display_without_hardware_identity_is_never_confused_with_a_remembered_monitor()
    {
        var anon = Monitor(@"\\.\DISPLAY1", true, null, "Generic", null);
        Assert.Equal("win:" + anon.DisplayId, ConsoleMonitorIdentity.IdOf(anon));
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(anon), Now), "win:" + anon.DisplayId, true);
        Assert.Single(ConsoleTvRegistry.ConnectedTvs(history, Snapshot(anon)));
        Assert.Empty(ConsoleTvRegistry.ConnectedTvs(history, Snapshot(Monitor(@"\\.\DISPLAY2", true, null, "Generic", null))));
    }

    [Fact]
    public void A_corrupt_history_is_cleaned_and_bounded()
    {
        var junk = new List<ConsoleTvEntry?>
        {
            null,
            new() { StableId = "", Name = "no id" },
            new() { StableId = "ok-1", Name = "  Name\u0007 with control  ", LastDisplayId = "bad\u0001id", MonitorCode = new string('M', 40) },
            new() { StableId = "OK-1", Name = "duplicate", Selected = true, LastSeenUtc = Now },
        };
        junk.AddRange(Enumerable.Range(0, 100).Select(i => new ConsoleTvEntry { StableId = "id-" + i, Name = "n" + i, LastSeenUtc = Now.AddMinutes(i) }));
        var clean = ConsoleTvRegistry.Sanitize(junk);
        Assert.Equal(ConsoleTvRegistry.MaxEntries, clean.Count);
        var kept = clean.Single(e => e.StableId.Equals("ok-1", StringComparison.OrdinalIgnoreCase));
        Assert.True(kept.Selected);                                  // a selected entry is never pruned
        Assert.DoesNotContain(clean, e => e.Name.Any(char.IsControl));
        Assert.All(clean, e => Assert.Null(e.LastDisplayId is "bad\u0001id" ? e.LastDisplayId : null));
        Assert.All(clean, e => Assert.True(e.MonitorCode is null || e.MonitorCode.Length <= 16));
    }

    // ---- sync with the older single-target settings -----------------------------------------------------------------------------------

    [Fact]
    public void With_no_tv_chosen_the_target_is_empty_so_console_opens_on_the_current_screen()
    {
        var only = ConsoleTvSettingsSnapshots.OnlyMonitor;
        var sync = ConsoleTvRegistry.Sync(null, only, null, null, Now);
        Assert.Equal(string.Empty, sync.TargetDisplayId);
        Assert.Empty(sync.ConnectedTvs);
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(only, sync.Roles, sync.TargetDisplayId));   // empty target: no routing, no block
    }

    [Fact]
    public void A_chosen_tv_that_is_connected_becomes_the_routing_target_with_a_tv_role()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 1920, w: 3840, h: 2160);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now), tv.StableId!, true);
        var sync = ConsoleTvRegistry.Sync(history, Snapshot(pc, tv), null, null, Now);
        Assert.Equal(@"\\.\DISPLAY2", sync.TargetDisplayId);
        Assert.Equal(3840, ConsoleDisplaySelectionPolicy.SelectTarget(Snapshot(pc, tv), sync.Roles, sync.TargetDisplayId)!.Width);
        Assert.Equal(ConsoleDisplaySelectionPolicy.MonitorRole, sync.Roles.Single(r => r.DisplayId == @"\\.\DISPLAY1").Role);
    }

    [Fact]
    public void A_chosen_tv_that_is_unplugged_never_redirects_to_another_screen()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now), tv.StableId!, true);
        var sync = ConsoleTvRegistry.Sync(history, Snapshot(pc), null, null, Now.AddDays(1));
        Assert.False(string.IsNullOrWhiteSpace(sync.TargetDisplayId));
        Assert.NotEqual(@"\\.\DISPLAY1", sync.TargetDisplayId);
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(Snapshot(pc), sync.Roles, sync.TargetDisplayId));

        // A target with no last-known display id still resolves to something that matches no screen.
        var noHint = history.Select(e => { var c = e.Clone(); c.LastDisplayId = null; return c; }).ToList();
        var sync2 = ConsoleTvRegistry.Sync(noHint, Snapshot(pc), null, null, Now);
        Assert.Equal(ConsoleTvRegistry.DisconnectedTargetPlaceholder, sync2.TargetDisplayId);
        Assert.Null(ConsoleDisplaySelectionPolicy.SelectTarget(Snapshot(pc), sync2.Roles, sync2.TargetDisplayId));
    }

    [Fact]
    public void An_unreadable_topology_keeps_whatever_was_saved()
    {
        var sync = ConsoleTvRegistry.Sync(null, null, null, @"\\.\DISPLAY9", Now);
        Assert.Equal(@"\\.\DISPLAY9", sync.TargetDisplayId);
    }

    [Fact]
    public void The_older_single_tv_choice_migrates_once_into_the_new_list()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 1920);
        var roles = new[] { new ConsoleDisplayAssignment(@"\\.\DISPLAY2", ConsoleDisplaySelectionPolicy.TvRole) };
        var sync = ConsoleTvRegistry.Sync(null, Snapshot(pc, tv), roles, @"\\.\DISPLAY2", Now);
        Assert.Equal(tv.StableId, Assert.Single(sync.Tvs, e => e.Selected).StableId);
        Assert.Equal(@"\\.\DISPLAY2", sync.TargetDisplayId);
    }

    // ---- settings persistence -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Old_settings_files_without_the_tv_list_load_with_an_empty_history()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"ConsoleTargetDisplayId\":\"\\\\\\\\.\\\\DISPLAY2\",\"ConsoleStartOnTvWhenPresent\":true}")!;
        Assert.Empty(old.ConsoleTvs);
        Assert.False(old.ConsoleTvsMigrated);
        Assert.True(old.ConsoleStartOnTvWhenPresent);
    }

    [Fact]
    public void The_history_round_trips_through_the_settings_json_without_a_serial_or_raw_edid()
    {
        var settings = new AppSettings();
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC", "AAA0001");
        ConsoleTvSettingsSync.Apply(settings, Snapshot(pc), ConsoleTvSimulation.Mode.Off, Now, out _);
        settings.ConsoleTvs[0].Selected = true;
        string json = JsonSerializer.Serialize(settings);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("AAA0001-aaaaaaaaaaaa", Assert.Single(back.ConsoleTvs).StableId);
        Assert.True(back.ConsoleTvs[0].Selected);
        Assert.DoesNotContain("EDID", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applying_the_same_snapshot_twice_does_not_rewrite_the_settings()
    {
        var settings = new AppSettings();
        var snap = ConsoleTvSettingsSnapshots.OnlyMonitor;
        Assert.True(ConsoleTvSettingsSync.Apply(settings, snap, ConsoleTvSimulation.Mode.Off, Now, out _));    // first run records the monitor
        Assert.False(ConsoleTvSettingsSync.Apply(settings, snap, ConsoleTvSimulation.Mode.Off, Now.AddHours(2), out _));
        Assert.False(ConsoleTvSettingsSync.Apply(settings, null, ConsoleTvSimulation.Mode.Off, Now, out _));   // unreadable: untouched
    }

    [Fact]
    public void A_simulated_tv_is_visible_to_decisions_but_never_saved()
    {
        var settings = new AppSettings();
        var snap = ConsoleTvSettingsSnapshots.OnlyMonitor;
        ConsoleTvSettingsSync.Apply(settings, snap, ConsoleTvSimulation.Mode.CurrentPrimary, Now, out ConsoleTvSyncResult? result);
        Assert.Single(result!.ConnectedTvs);
        Assert.DoesNotContain(settings.ConsoleTvs, e => e.Selected);
        Assert.Equal(string.Empty, settings.ConsoleTargetDisplayId);
        Assert.DoesNotContain(settings.ConsoleDisplays, a => a.Role == ConsoleDisplaySelectionPolicy.TvRole);
    }

    [Fact]
    public void Clearing_the_selection_returns_console_to_the_current_screen()
    {
        var settings = new AppSettings();
        var snap = ConsoleTvSettingsSnapshots.OnlyMonitor;
        settings.ConsoleTvs = ConsoleTvRegistry.SelectCurrentMonitor(settings.ConsoleTvs, snap, Now);
        ConsoleTvSettingsSync.Apply(settings, snap, ConsoleTvSimulation.Mode.Off, Now, out _);
        Assert.Equal(@"\\.\DISPLAY1", settings.ConsoleTargetDisplayId);
        settings.ConsoleTvs.ForEach(e => e.Selected = false);
        ConsoleTvSettingsSync.Apply(settings, snap, ConsoleTvSimulation.Mode.Off, Now, out _);
        Assert.Equal(string.Empty, settings.ConsoleTargetDisplayId);
    }

    // ---- test seam: the current monitor is a TV ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("current", ConsoleTvSimulation.Mode.CurrentPrimary)]
    [InlineData(" CURRENT ", ConsoleTvSimulation.Mode.CurrentPrimary)]
    [InlineData("", ConsoleTvSimulation.Mode.Off)]
    [InlineData(null, ConsoleTvSimulation.Mode.Off)]
    [InlineData("1", ConsoleTvSimulation.Mode.Off)]
    public void The_test_variable_value_is_parsed_strictly(string? value, ConsoleTvSimulation.Mode expected) =>
        Assert.Equal(expected, ConsoleTvSimulation.Parse(value));

    [Fact]
    public void The_environment_seam_is_honoured_only_in_debug_builds()
    {
        string? old = Environment.GetEnvironmentVariable(ConsoleTvSimulation.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConsoleTvSimulation.EnvironmentVariable, "current");
            Assert.Equal(ConsoleTvSimulation.EnvironmentSeamCompiledIn ? ConsoleTvSimulation.Mode.CurrentPrimary : ConsoleTvSimulation.Mode.Off,
                ConsoleTvSimulation.FromEnvironment());
            Environment.SetEnvironmentVariable(ConsoleTvSimulation.EnvironmentVariable, null);
            Assert.Equal(ConsoleTvSimulation.Mode.Off, ConsoleTvSimulation.FromEnvironment());
        }
        finally { Environment.SetEnvironmentVariable(ConsoleTvSimulation.EnvironmentVariable, old); }
    }

    [Fact]
    public void The_simulated_tv_is_the_primary_monitor_not_a_secondary_one()
    {
        var pc = Monitor(@"\\.\DISPLAY2", true, "AAA0001-aaaaaaaaaaaa", "Primary", "AAA0001");
        var second = Monitor(@"\\.\DISPLAY1", false, "BBB0002-bbbbbbbbbbbb", "Secondary", "BBB0002", x: 1920);
        var tvs = ConsoleTvRegistry.ConnectedTvs(null, Snapshot(second, pc), ConsoleTvSimulation.Mode.CurrentPrimary);
        Assert.Equal("Primary", ConsoleTvRegistry.DisplayName(Assert.Single(tvs)));
        Assert.Empty(ConsoleTvRegistry.ConnectedTvs(null, Snapshot(second, pc)));
    }

    // ---- startup: automatic Console/Desktop choice uses the chosen list --------------------------------------------------------------

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static Task<ConsoleStartupTvProbe.Result> Probe(IConsoleTopologyProvider provider, IReadOnlyList<ConsoleTvEntry> tvs,
        ConsoleTvSimulation.Mode sim = ConsoleTvSimulation.Mode.Off, Func<bool>? stop = null) =>
        ConsoleStartupTvProbe.RunAsync(provider, () => tvs, sim, NoDelay, stop ?? (() => false));

    [Fact]
    public async Task With_no_tv_chosen_start_does_not_wait_or_read_displays_and_console_is_never_blocked()
    {
        var fake = new FakeConsoleTopologyProvider(ConsoleTvSettingsSnapshots.OnlyMonitor);
        var probe = await Probe(fake, Array.Empty<ConsoleTvEntry>());
        Assert.False(probe.HasSelection);
        Assert.False(probe.TvPresent);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(ConsoleStartupDecision.Normal, ConsoleStartupPolicy.Decide(true, probe.DisplayId, probe.TvPresent, "normal"));
    }

    [Fact]
    public async Task Start_in_tv_mode_when_any_chosen_tv_is_connected()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY3", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now), tv.StableId!, true);
        var probe = await Probe(new FakeConsoleTopologyProvider(Snapshot(pc, tv)), history);
        Assert.True(probe.TvPresent);
        Assert.Equal(@"\\.\DISPLAY3", probe.DisplayId);
        Assert.Equal(ConsoleStartupDecision.EnterConsole, ConsoleStartupPolicy.Decide(true, probe.DisplayId, probe.TvPresent, "minimized"));
    }

    [Fact]
    public async Task A_chosen_tv_that_stays_absent_applies_the_fallback_after_two_confirmations()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY3", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now), tv.StableId!, true);
        var fake = new FakeConsoleTopologyProvider(Snapshot(pc));
        var probe = await Probe(fake, history);
        Assert.True(probe.HasSelection);
        Assert.False(probe.TvPresent);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(ConsoleStartupDecision.Minimize, ConsoleStartupPolicy.Decide(true, probe.DisplayId, probe.TvPresent, "minimized"));
        Assert.Equal(ConsoleStartupDecision.Normal, ConsoleStartupPolicy.Decide(true, probe.DisplayId, probe.TvPresent, "normal"));
    }

    [Fact]
    public async Task A_tv_that_wakes_up_during_the_wait_is_picked_up_and_unreadable_answers_are_retried()
    {
        var pc = Monitor(@"\\.\DISPLAY1", true, "AAA0001-aaaaaaaaaaaa", "PC", "AAA0001");
        var tv = Monitor(@"\\.\DISPLAY2", false, "BBB0002-bbbbbbbbbbbb", "TV", "BBB0002", x: 1920);
        var history = ConsoleTvRegistry.SetSelected(ConsoleTvRegistry.RecordSeen(null, Snapshot(pc, tv), Now), tv.StableId!, true);
        var fake = new FakeConsoleTopologyProvider(null, Snapshot(pc), Snapshot(pc, tv));
        var probe = await Probe(fake, history);
        Assert.True(probe.TvPresent);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task The_wait_is_bounded_when_core_never_answers_and_stops_at_shutdown()
    {
        var history = ConsoleTvRegistry.SelectCurrentMonitor(null, ConsoleTvSettingsSnapshots.OnlyMonitor, Now);
        var never = new FakeConsoleTopologyProvider(new GamingDisplaySnapshotDto?[] { null });
        var probe = await Probe(never, history);
        Assert.False(probe.TvPresent);
        Assert.Equal(ConsoleStartupProbePolicy.DefaultMaxProbes, never.Calls);

        var stopped = new FakeConsoleTopologyProvider(ConsoleTvSettingsSnapshots.OnlyMonitor);
        Assert.False((await Probe(stopped, history, stop: () => true)).TvPresent);
        Assert.Equal(0, stopped.Calls);
    }

    [Fact]
    public async Task With_a_single_monitor_and_the_current_monitor_declared_a_tv_start_enters_console_on_it()
    {
        // The test seam: the one monitor of this PC is "the TV". No saved choice, no Windows setting involved.
        var fake = new FakeConsoleTopologyProvider(FakeConsoleTopologyProvider.SingleMonitor());
        var probe = await Probe(fake, Array.Empty<ConsoleTvEntry>(), ConsoleTvSimulation.Mode.CurrentPrimary);
        Assert.True(probe.HasSelection);
        Assert.True(probe.TvPresent);
        Assert.Equal(@"\\.\DISPLAY1", probe.DisplayId);
        Assert.Equal(ConsoleStartupDecision.EnterConsole, ConsoleStartupPolicy.Decide(true, probe.DisplayId, probe.TvPresent, "normal"));
    }

    [Fact]
    public async Task A_provider_that_throws_is_treated_as_unreadable_not_as_a_crash()
    {
        var history = ConsoleTvRegistry.SelectCurrentMonitor(null, ConsoleTvSettingsSnapshots.OnlyMonitor, Now);
        var probe = await Probe(new ThrowingProvider(), history);
        Assert.False(probe.TvPresent);
    }

    private sealed class ThrowingProvider : IConsoleTopologyProvider
    {
        public Task<GamingDisplaySnapshotDto?> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("core down");
    }

    // ---- the selector against this PC's real topology (read-only) --------------------------------------------------------------------

    [Fact]
    public void The_selector_lists_the_real_current_monitor_and_use_current_monitor_selects_it_read_only()
    {
        if (!OperatingSystem.IsWindows()) return;
        GamingDisplaySnapshotDto real = new GamingDisplayTopologyService().Snapshot();
        if (!real.Supported) return;   // headless session: nothing to list

        List<ConsoleTvEntry> history = ConsoleTvRegistry.RecordSeen(null, real, DateTime.UtcNow);
        Assert.Equal(real.Displays.Count, history.Count);                     // every connected monitor is offered
        GamingDisplayModeDto primary = ConsoleTvRegistry.PrimaryOf(real)!;
        Assert.Contains(history, e => e.StableId == ConsoleMonitorIdentity.IdOf(primary));
        Assert.All(history, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));

        List<ConsoleTvEntry> chosen = ConsoleTvRegistry.SelectCurrentMonitor(null, real, DateTime.UtcNow);
        Assert.Equal(ConsoleMonitorIdentity.IdOf(primary), Assert.Single(chosen, e => e.Selected).StableId);
        Assert.Equal(primary.DisplayId, Assert.Single(ConsoleTvRegistry.ConnectedTvs(chosen, real)).DisplayId);

        // The real identity must not leak a raw serial, and the simulation seam must pick the same monitor.
        Assert.True(primary.StableId is null || ConsoleMonitorIdentity.ValidStableId(primary.StableId));
        Assert.Equal(primary.DisplayId, Assert.Single(ConsoleTvRegistry.ConnectedTvs(null, real, ConsoleTvSimulation.Mode.CurrentPrimary)).DisplayId);

        // Opt-in diagnostics for the developer only (names stay in the local console, never in the repository).
        if (Environment.GetEnvironmentVariable("PARUX_PRINT_REAL_TOPOLOGY") == "1")
            foreach (ConsoleTvEntry e in chosen)
                _output.WriteLine($"{(e.Selected ? "[TV]" : "[  ]")} name='{e.Name}' code={e.MonitorCode ?? "-"} id={e.StableId} display={e.LastDisplayId}");
    }
}

/// <summary>Shared synthetic single-monitor snapshot.</summary>
internal static class ConsoleTvSettingsSnapshots
{
    public static GamingDisplaySnapshotDto OnlyMonitor => FakeConsoleTopologyProvider.SingleMonitor();
}
