using System.Text.Json;
using Microsoft.Win32;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// Opt-in READ-ONLY dry run on the real PC (PARUX_STARTUP_DRYRUN_REAL=1): reads the saved settings (never writes), the connected displays,
/// the present graphics adapters and the current Run entry, then prints what a sign-in start would choose. It starts nothing and changes
/// nothing. Set PARUX_REAL_SETTINGS to read another settings file. Skipped (passes) otherwise.
/// </summary>
public sealed class LogonStartupRealProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task Describe_what_a_sign_in_start_would_choose_on_this_pc_without_starting_anything()
    {
        if (Environment.GetEnvironmentVariable("PARUX_STARTUP_DRYRUN_REAL") != "1") return;
        string path = Environment.GetEnvironmentVariable("PARUX_REAL_SETTINGS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX", "settings.json");
        AppSettings s = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings() : new AppSettings();

        GamingDisplaySnapshotDto snapshot = new GamingDisplayTopologyService().Snapshot();
        IReadOnlyList<string> adapters = PresentDisplayAdapters.Read();
        output.WriteLine($"settings file found={File.Exists(path)} startInConsoleMode={s.StartInConsoleMode} startOnTv={s.ConsoleStartOnTvWhenPresent} fallback={s.StartupWithoutTv} tvs(selected)={s.ConsoleTvs.Count(t => t.Selected)}/{s.ConsoleTvs.Count} gpuRule='{s.LogonGpuNamePattern}'");
        output.WriteLine($"displays supported={snapshot.Supported} count={snapshot.Displays.Count} adapters={adapters.Count}");

        string? gpuRule = LogonGpuRule.Normalize(s.LogonGpuNamePattern);
        var settings = new LogonStartupSettings(s.StartInConsoleMode, s.ConsoleStartOnTvWhenPresent, s.StartupWithoutTv, gpuRule, 0,
            s.ConsolePowerProfileEnabled && !string.IsNullOrWhiteSpace(s.ConsolePowerPlanId), s.BlockXboxGameBar && s.GameBarBlockConsentAsked, false);
        bool tvSelected = ConsoleTvRegistry.HasSelection(s.ConsoleTvs);
        bool tvPresent = tvSelected && ConsoleTvRegistry.ConnectedTvs(s.ConsoleTvs, snapshot).Count > 0;
        bool? gpu = gpuRule is null ? null : LogonGpuRule.Matches(gpuRule, adapters);
        var request = new LogonStartupRequest(true, false, false);
        LogonPlan plan = LogonStartupPolicy.Decide(request, PlayniteLauncherModeResolver.Resolve(PlayniteLauncherRequest.Nothing, false, false), settings,
            new LogonProbe(tvSelected, tvPresent, gpu, false));
        output.WriteLine("--- decision (English) ---");
        output.WriteLine(plan.Describe(false));
        output.WriteLine("--- decision (Italian) ---");
        output.WriteLine(plan.Describe(true));

        using RegistryKey? run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        string? current = run?.GetValue(LogonRegistration.ValueName) as string;
        output.WriteLine("PaRuX sign-in entry present=" + (current is not null) + (current is null ? string.Empty : " withLogonArgument=" + current.Contains("--logon")));
        Assert.NotNull(plan);
        await Task.CompletedTask;
    }
}
