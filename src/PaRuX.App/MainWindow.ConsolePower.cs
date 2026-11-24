using System;
using System.Threading.Tasks;
using PaRuX.Services;

namespace PaRuX;

/// <summary>
/// Opt-in Console power plan. Only does anything when the owner turned it on in Settings > Gaming and picked an existing Windows power
/// plan. The Core owns the restore point (crash-safe, never overrides a plan the owner changed meanwhile); this partial only asks it to
/// apply when Console/TV or a PaRuX-started Playnite Fullscreen begins and to restore when it ends. A refusal is logged, never forced.
/// </summary>
public sealed partial class MainWindow
{
    internal async Task ApplyConsolePowerProfileAsync()
    {
        try
        {
            var data = SettingsStore.Current.Data;
            if (!data.ConsolePowerProfileEnabled || string.IsNullOrWhiteSpace(data.ConsolePowerPlanId) || App.IsShuttingDown) return;
            var result = await PlayniteClient.Current.ApplyPowerProfileAsync(data.ConsolePowerPlanId);
            Log.Write("Console power plan: " + (result.Succeeded ? "applied" : "not applied (" + result.Error + ")"));
        }
        catch (Exception ex) { Log.Write("Console power plan apply failed: " + ex.GetType().Name); }
    }

    internal async Task RestoreConsolePowerProfileAsync()
    {
        try
        {
            // Restore is harmless when nothing is applied, and must also run if the owner switched the option off meanwhile.
            var result = await PlayniteClient.Current.RestorePowerProfileAsync();
            if (!result.Succeeded) Log.Write("Console power plan restore failed: " + result.Error);
        }
        catch (Exception ex) { Log.Write("Console power plan restore failed: " + ex.GetType().Name); }
    }
}
