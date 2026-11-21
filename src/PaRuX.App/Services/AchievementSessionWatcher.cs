using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>One read of a game's achievements, or the plain reason it could not be read.</summary>
    public sealed record AchievementSnapshot(string GameName, IReadOnlyList<AchievementEntryDto> Entries, string Problem);

    /// <summary>
    /// Bounded poll of one game's achievements during one PaRuX-launched session. The first good read is only a baseline, so
    /// nothing already earned is announced; later reads announce what is newly unlocked. It stops on cancellation, when the
    /// session is no longer allowed (game ended, switch off, Gaming-incompatible state), after <see cref="MaxPolls"/> reads or
    /// after <see cref="MaxConsecutiveProblems"/> unreadable answers in a row (no credentials, Internet off, offline).
    /// Pure logic: the clock, the reader and the announcement are injected, so it is tested without any window or network.
    /// </summary>
    public sealed class AchievementSessionWatcher
    {
        public const int MaxPolls = 480;
        public const int MaxConsecutiveProblems = 3;
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

        private readonly Func<CancellationToken, Task<AchievementSnapshot>> _fetch;
        private readonly Func<bool> _allowed;
        private readonly Action<string, AchievementEntryDto> _announce;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _interval;
        private HashSet<string>? _known;

        public AchievementSessionWatcher(
            Func<CancellationToken, Task<AchievementSnapshot>> fetch, Func<bool> allowed, Action<string, AchievementEntryDto> announce,
            Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? interval = null)
        {
            _fetch = fetch;
            _allowed = allowed;
            _announce = announce;
            _delay = delay ?? Task.Delay;
            _interval = interval ?? DefaultInterval;
        }

        public int Polls { get; private set; }
        public bool BaselineEstablished => _known is not null;
        /// <summary>Why the loop ended: "cancelled", "not-allowed", "problems", "limit" (empty while running).</summary>
        public string StopReason { get; private set; } = string.Empty;

        public async Task RunAsync(CancellationToken ct)
        {
            int problems = 0;
            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested) { StopReason = "cancelled"; return; }
                    if (!_allowed()) { StopReason = "not-allowed"; return; }
                    if (Polls >= MaxPolls) { StopReason = "limit"; return; }
                    Polls++;
                    AchievementSnapshot snap;
                    try { snap = await _fetch(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { StopReason = "cancelled"; return; }
                    catch (Exception) { snap = new AchievementSnapshot(string.Empty, Array.Empty<AchievementEntryDto>(), "unreadable"); }

                    if (snap.Problem.Length > 0)
                    {
                        if (++problems >= MaxConsecutiveProblems) { StopReason = "problems"; return; }
                    }
                    else
                    {
                        problems = 0;
                        if (_known is null) _known = AchievementUnlockDiff.UnlockedKeys(snap.Entries);
                        else
                        {
                            foreach (AchievementEntryDto a in AchievementUnlockDiff.NewlyUnlocked(_known, snap.Entries))
                            {
                                _known.Add(a.Key);
                                if (_allowed()) _announce(snap.GameName, a);
                            }
                        }
                    }
                    await _delay(_interval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { StopReason = "cancelled"; }
        }
    }
}
