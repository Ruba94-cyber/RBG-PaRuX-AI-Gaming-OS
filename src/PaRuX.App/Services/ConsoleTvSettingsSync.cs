using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Applies the persistent TV list (<see cref="AppSettings.ConsoleTvs"/>) to a fresh display snapshot and refreshes the older
    /// <see cref="AppSettings.ConsoleDisplays"/> / <see cref="AppSettings.ConsoleTargetDisplayId"/> that window routing, the Playnite
    /// display and game routing still read. Reads settings and a snapshot only: it never touches Windows display settings.
    /// </summary>
    public static class ConsoleTvSettingsSync
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions();

        /// <summary>
        /// Returns true when saved settings changed (the caller saves). An unreadable snapshot changes nothing. A simulated TV (test seam)
        /// is reflected only in <paramref name="result"/> and in the in-memory routing target, never in what the owner saved.
        /// </summary>
        public static bool Apply(AppSettings data, GamingDisplaySnapshotDto? snapshot, ConsoleTvSimulation.Mode simulation,
            DateTime nowUtc, out ConsoleTvSyncResult? result)
        {
            result = null;
            if (snapshot is not { Supported: true }) return false;
            string before = Fingerprint(data) + data.ConsoleTvsMigrated;
            ConsoleTvSyncResult real = ConsoleTvRegistry.Sync(data.ConsoleTvs, snapshot, data.ConsoleDisplays,
                data.ConsoleTargetDisplayId, nowUtc, ConsoleTvSimulation.Mode.Off, migrateLegacy: !data.ConsoleTvsMigrated);
            data.ConsoleTvsMigrated = true;
            data.ConsoleTvs = new List<ConsoleTvEntry>(real.Tvs);
            data.ConsoleDisplays = new List<ConsoleDisplayAssignment>(real.Roles);
            data.ConsoleTargetDisplayId = real.TargetDisplayId;
            bool changed = before != Fingerprint(data) + data.ConsoleTvsMigrated;
            result = simulation == ConsoleTvSimulation.Mode.Off
                ? real
                : ConsoleTvRegistry.Sync(data.ConsoleTvs, snapshot, data.ConsoleDisplays, data.ConsoleTargetDisplayId, nowUtc, simulation, migrateLegacy: false);
            return changed;
        }

        /// <summary>Reads the topology through <paramref name="provider"/> and applies it. Returns true when settings changed.</summary>
        public static async Task<bool> RefreshAsync(AppSettings data, IConsoleTopologyProvider provider, ConsoleTvSimulation.Mode simulation,
            CancellationToken ct = default)
        {
            GamingDisplaySnapshotDto? snapshot = await provider.ReadAsync(ct).ConfigureAwait(false);
            return Apply(data, snapshot, simulation, DateTime.UtcNow, out _);
        }

        private static string Fingerprint(AppSettings d) =>
            JsonSerializer.Serialize(new
            {
                // "Last seen" is compared by day so a plain start does not rewrite settings.json every time.
                Tvs = d.ConsoleTvs.Select(e => new { e.StableId, e.Name, e.MonitorCode, e.LastDisplayId, e.Selected, Day = e.LastSeenUtc.Date }),
                d.ConsoleDisplays,
                d.ConsoleTargetDisplayId
            }, Json);
    }
}
