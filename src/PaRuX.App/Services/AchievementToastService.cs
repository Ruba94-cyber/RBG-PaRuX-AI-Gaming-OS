using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// Local unlock toast for Steam and RetroAchievements games. It only ever runs for a game PaRuX itself launched
    /// (<see cref="NoteLaunch"/> is called by the launch paths), only while Gaming Mode reports that game as running, and stops the
    /// moment the game ends, the owner turns the option off, Corporate Lite is active or the Core says it cannot read achievements
    /// (no credentials, Internet switch off). Polling is bounded (see <see cref="AchievementSessionWatcher"/>) and goes through the
    /// owner-only Core routes, so the Internet switch and credential rules stay in one place. The announcement is a tray balloon.
    /// </summary>
    public sealed class AchievementToastService
    {
        public static AchievementToastService Current { get; } = new AchievementToastService();

        private sealed record Target(string Source, string Key, string Name, string Platform);

        private readonly object _gate = new();
        private Target? _pending;
        private CancellationTokenSource? _cts;
        private bool _subscribed;

        private AchievementToastService() { }

        /// <summary>Where the announcement goes (set by the main window); null disables toasts.</summary>
        public Action<string, string>? Announce { get; set; }

        public bool IsWatching { get { lock (_gate) return _cts is not null; } }

        public void Start()
        {
            lock (_gate)
            {
                if (_subscribed) return;
                _subscribed = true;
            }
            GamingManager.Current.StateChanged += OnGamingStateChanged;
        }

        /// <summary>A Steam game was launched by PaRuX (app id).</summary>
        public void NoteSteamLaunch(string appId, string name)
        {
            if (!int.TryParse(appId, out int id) || id <= 0) return;
            lock (_gate) _pending = new Target("steam", appId, name ?? string.Empty, string.Empty);
            BeginWhenGameIsActive();
        }

        /// <summary>A library entry (manual game or emulated game) was launched by PaRuX; only emulator entries are watched.</summary>
        public void NoteManualLaunch(string manualGameId)
        {
            if (string.IsNullOrWhiteSpace(manualGameId)) return;
            lock (_gate) _pending = new Target("manual", manualGameId, string.Empty, string.Empty);
            BeginWhenGameIsActive();
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            lock (_gate) { cts = _cts; _cts = null; _pending = null; }
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            cts?.Dispose();
        }

        private void OnGamingStateChanged(object? sender, EventArgs e)
        {
            if (!GamingManager.Current.IsActive) Stop();
            else Begin();
        }

        /// <summary>The Gaming marker appears a moment after the launch call returns; wait up to ~30 s for it, then give up quietly.</summary>
        private void BeginWhenGameIsActive()
        {
            Start();
            if (!SettingsStore.Current.Data.AchievementToastsEnabled || CorporateMode.IsActive) return;
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    if (GamingManager.Current.IsActive) { Begin(); return; }
                    await Task.Delay(1500).ConfigureAwait(false);
                }
                lock (_gate) { if (_cts is null) _pending = null; }
            });
        }

        private bool Allowed() =>
            SettingsStore.Current.Data.AchievementToastsEnabled
            && !CorporateMode.IsActive
            && GamingManager.Current.IsActive
            && Announce is not null;

        private void Begin()
        {
            Target? target;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_pending is null || _cts is not null) return;
                if (!Allowed()) return;
                target = _pending;
                _cts = cts = new CancellationTokenSource();
            }
            _ = Task.Run(() => RunAsync(target, cts));
        }

        private async Task RunAsync(Target target, CancellationTokenSource cts)
        {
            try
            {
                Func<CancellationToken, Task<AchievementSnapshot>>? fetch = await ResolveFetcherAsync(target, cts.Token).ConfigureAwait(false);
                if (fetch is null) return;
                var watcher = new AchievementSessionWatcher(fetch, Allowed, OnUnlocked);
                await watcher.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Write("Achievement watcher stopped: " + ex.GetType().Name); }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_cts, cts)) { _cts = null; _pending = null; } }
                cts.Dispose();
            }
        }

        private static async Task<Func<CancellationToken, Task<AchievementSnapshot>>?> ResolveFetcherAsync(Target target, CancellationToken ct)
        {
            if (target.Source == "steam")
            {
                int appId = int.Parse(target.Key, System.Globalization.CultureInfo.InvariantCulture);
                return async token =>
                {
                    SteamAchievementsResult r = await OwnerRecordsClient.Current.SteamAchievementsAsync(appId, token).ConfigureAwait(false);
                    return new AchievementSnapshot(r.GameName.Length > 0 ? r.GameName : target.Name,
                        r.Achievements.Select(AchievementUnlockDiff.FromSteam).ToList(), r.Problem);
                };
            }

            // Manual entry: only emulator entries have a RetroAchievements console; a plain PC executable has none.
            GamingCallResult<List<ManualGameDto>> list = await GamingOrchestratorClient.Current.ListManualGamesAsync(ct).ConfigureAwait(false);
            ManualGameDto? game = list.Value?.FirstOrDefault(g => string.Equals(g.Id, target.Key, StringComparison.Ordinal));
            if (game is null || string.IsNullOrWhiteSpace(game.EmulatorId) || string.IsNullOrWhiteSpace(game.Platform)) return null;
            return async token =>
            {
                RetroAchievementsResult r = await OwnerRecordsClient.Current.RetroAchievementsAsync(0, game.Name, game.Platform, token).ConfigureAwait(false);
                return new AchievementSnapshot(r.GameName.Length > 0 ? r.GameName : game.Name, r.Achievements, r.Problem);
            };
        }

        /// <summary>Server text is shown in a system notification: control, bidi and other invisible format characters are dropped and the length is bounded.</summary>
        internal static string Printable(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new System.Text.StringBuilder(Math.Min(text.Length, 200));
            foreach (System.Text.Rune r in text.EnumerateRunes())
            {
                System.Globalization.UnicodeCategory c = System.Text.Rune.GetUnicodeCategory(r);
                if (c is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                    or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate
                    or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
                sb.Append(r.ToString());
                if (sb.Length >= 200) break;
            }
            return sb.ToString().Trim();
        }

        private void OnUnlocked(string gameName, AchievementEntryDto a)
        {
            string title = (string.IsNullOrWhiteSpace(gameName) ? "" : Printable(gameName) + " - ") + UiText.Pick("Achievement unlocked", "Risultato sbloccato");
            string body = Printable(a.Name) + (string.IsNullOrWhiteSpace(a.Description) ? string.Empty : " - " + Printable(a.Description));
            try { Announce?.Invoke(title, body); }
            catch (Exception ex) { Log.Write("Achievement toast failed: " + ex.GetType().Name); }
        }
    }
}
