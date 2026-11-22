using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Playnite;

/// <summary>One row of a process snapshot (cheap: id, parent id, image name).</summary>
internal sealed record ProcEntry(int Pid, int ParentPid, string Name);

/// <summary>Details looked up only for the few processes the tracker has to judge.</summary>
internal sealed record ProcDetail(DateTime StartUtc, string? Path, bool HasWindow);

internal enum PlayniteTrackerEvent { None, Start, End }

internal sealed record TrackedProcess(int Pid, DateTime StartUtc, string Name, string? Path);

internal sealed record PlayniteTrackerResult(
    PlayniteTrackerEvent Event,
    bool GameActive,
    bool PlayniteAlive,
    IReadOnlyList<TrackedProcess> Game,
    TrackedProcess? Primary);

/// <summary>
/// The game-session signal as a pure state machine over process snapshots. It OBSERVES only: it holds no handle, never terminates,
/// suspends or changes any process, and returns a verdict that the caller turns into an orchestrator transition.
///
/// What counts as "the game": descendants (any depth, so nested launchers and wrapper scripts work) of a Playnite process, plus
/// the windowed non-helper children of a known store launcher (a Steam/Epic/GOG game is started by the launcher, not by Playnite).
/// Once a process is tracked it stays tracked until it exits, so closing Playnite while the game runs does not end the session
/// and a child exiting before its parent does not either. The session starts only after the game has been seen for
/// <see cref="StartDebounce"/> (a stub that dies at once never yields the GPU) and ends only after nothing has been alive for
/// <see cref="EndGrace"/> (a launcher stub handing over to the real game is one session, a crash dialog is not a game).
/// Process ids are reused by Windows, so every tracked process is identified by id AND start time.
/// </summary>
internal sealed class PlayniteGameSessionTracker
{
    public static readonly TimeSpan DefaultStartDebounce = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan DefaultEndGrace = TimeSpan.FromSeconds(8);
    internal const int MaxTracked = 128;
    private static readonly TimeSpan WindowlessAdoptionLimit = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> PlayniteNames = new(StringComparer.OrdinalIgnoreCase)
    { "Playnite.DesktopApp", "Playnite.FullscreenApp" };

    /// <summary>Store launchers whose windowed, non-helper children are games started on behalf of Playnite.</summary>
    internal static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "EpicGamesLauncher", "GalaxyClient", "EADesktop", "EALauncher", "upc", "UbisoftConnect", "Battle.net",
        "Origin", "itch", "Amazon Games", "Playnite.DesktopApp", "Playnite.FullscreenApp"
    };

    /// <summary>Helpers that are part of Playnite or of the OS, never "the game" (they must not hold a session open).</summary>
    private static readonly Regex HelperName = new(
        @"^(playnite(?!\.(desktopapp|fullscreenapp)$).*|cefsharp\..*|conhost|werfault.*|dllhost|crashpad_handler|crashreporter|" +
        @"steamwebhelper|steamservice|steamerrorreporter|gameoverlayui|epicwebhelper|eosoverlayrenderer.*|eacefsubprocess|" +
        @"unitycrashhandler\d*|ubisoftextension.*|galaxyclientservice|galaxycommunication|vc_redist.*|dxsetup|dotnetfx.*|" +
        @".*(webhelper|crashhandler|crashreport|updater|installer|uninstall).*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TimeSpan _startDebounce;
    private readonly TimeSpan _endGrace;
    private readonly int _selfPid;
    private readonly Dictionary<int, TrackedProcess> _tracked = new();
    private readonly Dictionary<int, ProcDetail> _detailCache = new();
    private readonly HashSet<int> _rejected = new();
    private DateTime? _candidateSince;
    private DateTime? _endingSince;
    private bool _active;

    public PlayniteGameSessionTracker(int selfPid, TimeSpan? startDebounce = null, TimeSpan? endGrace = null)
    {
        _selfPid = selfPid;
        _startDebounce = startDebounce ?? DefaultStartDebounce;
        _endGrace = endGrace ?? DefaultEndGrace;
    }

    public bool GameActive => _active;

    internal static string BaseName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    internal static bool IsHelper(string name) => HelperName.IsMatch(BaseName(name));

    /// <param name="installFolder">When known, only Playnite executables from this folder count as Playnite.</param>
    public PlayniteTrackerResult Sample(IReadOnlyList<ProcEntry> table, DateTime nowUtc, Func<int, ProcDetail?> identify,
        string? installFolder = null)
    {
        var byPid = new Dictionary<int, ProcEntry>();
        foreach (ProcEntry e in table) byPid[e.Pid] = e;

        // Forget everything about processes that are gone, or whose id now belongs to a different program.
        foreach (int pid in _tracked.Keys.ToArray())
            if (!byPid.TryGetValue(pid, out ProcEntry? live) ||
                !string.Equals(BaseName(live.Name), _tracked[pid].Name, StringComparison.OrdinalIgnoreCase))
                _tracked.Remove(pid);
        foreach (int pid in _detailCache.Keys.ToArray()) if (!byPid.ContainsKey(pid)) _detailCache.Remove(pid);
        _rejected.RemoveWhere(pid => !byPid.ContainsKey(pid));

        ProcDetail? Detail(int pid)
        {
            if (_detailCache.TryGetValue(pid, out ProcDetail? cached)) return cached;
            ProcDetail? d = identify(pid);
            if (d is not null) _detailCache[pid] = d;
            return d;
        }

        // Playnite roots (identity checked against the located installation when we know it).
        var roots = new Dictionary<int, DateTime>();
        foreach (ProcEntry e in table)
        {
            if (!PlayniteNames.Contains(BaseName(e.Name))) continue;
            ProcDetail? d = Detail(e.Pid);
            if (d is null) continue;
            if (installFolder is not null && d.Path is not null && !PlayniteLocator.IsInFolder(d.Path, installFolder)) continue;
            roots[e.Pid] = d.StartUtc;
        }

        // Expand to a fixed point: the table is unordered, so a grandchild can precede its parent.
        bool grew = true;
        while (grew && _tracked.Count < MaxTracked)
        {
            grew = false;
            foreach (ProcEntry e in table)
            {
                if (_tracked.ContainsKey(e.Pid) || _rejected.Contains(e.Pid) || e.Pid == _selfPid || e.Pid <= 4) continue;
                string name = BaseName(e.Name);
                bool parentIsRoot = roots.ContainsKey(e.ParentPid);
                bool parentTracked = _tracked.TryGetValue(e.ParentPid, out TrackedProcess? parentProc);
                bool parentIsLauncher = byPid.TryGetValue(e.ParentPid, out ProcEntry? parentEntry) &&
                    LauncherNames.Contains(BaseName(parentEntry.Name)) && !PlayniteNames.Contains(BaseName(parentEntry.Name));
                if (!parentIsRoot && !parentTracked && !(parentIsLauncher && roots.Count > 0)) continue;
                if (PlayniteNames.Contains(name)) continue;   // Playnite switching its own mode is not a game
                if (QuickResumePolicy.IsProtected(name) || name.StartsWith("PaRuX", StringComparison.OrdinalIgnoreCase)) { _rejected.Add(e.Pid); continue; }
                if (IsHelper(name)) { _rejected.Add(e.Pid); continue; }

                ProcDetail? d = Detail(e.Pid);
                if (d is null) continue;   // may be a transient failure; judged again on the next sample
                DateTime parentStart = parentIsRoot ? roots[e.ParentPid]
                    : parentTracked ? parentProc!.StartUtc
                    : (Detail(e.ParentPid)?.StartUtc ?? DateTime.MinValue);
                // Started before its "parent": that id was reused, so this is not a child.
                if (d.StartUtc < parentStart.AddSeconds(-2)) { _rejected.Add(e.Pid); continue; }

                if (parentIsLauncher && !parentIsRoot && !parentTracked)
                {
                    // A store launcher's own children are games only when they look like one: a real window, and the game must not
                    // predate the Playnite session it is attributed to. Windowless leftovers are re-judged while young, then ignored.
                    DateTime oldestRoot = roots.Values.Min();
                    if (d.StartUtc < oldestRoot) { _rejected.Add(e.Pid); continue; }
                    if (!d.HasWindow)
                    {
                        if (nowUtc - d.StartUtc > WindowlessAdoptionLimit) _rejected.Add(e.Pid);
                        _detailCache.Remove(e.Pid);   // look again: the window usually appears a moment later
                        continue;
                    }
                }
                _tracked[e.Pid] = new TrackedProcess(e.Pid, d.StartUtc, name, d.Path);
                grew = true;
                if (_tracked.Count >= MaxTracked) break;
            }
        }

        List<TrackedProcess> game = _tracked.Values.OrderBy(t => t.StartUtc).ThenBy(t => t.Pid).ToList();
        bool anyGame = game.Count > 0;
        PlayniteTrackerEvent ev = PlayniteTrackerEvent.None;

        if (!_active)
        {
            if (anyGame)
            {
                _candidateSince ??= nowUtc;
                if (nowUtc - _candidateSince.Value >= _startDebounce) { _active = true; _endingSince = null; ev = PlayniteTrackerEvent.Start; }
            }
            else _candidateSince = null;
        }
        else
        {
            if (anyGame) _endingSince = null;
            else
            {
                _endingSince ??= nowUtc;
                if (nowUtc - _endingSince.Value >= _endGrace)
                {
                    _active = false; _endingSince = null; _candidateSince = null; ev = PlayniteTrackerEvent.End;
                    _tracked.Clear();
                }
            }
        }

        return new PlayniteTrackerResult(ev, _active, roots.Count > 0, game, game.FirstOrDefault());
    }

    /// <summary>Forget everything (integration switched off or Playnite skipped).</summary>
    public void Reset()
    {
        _tracked.Clear(); _detailCache.Clear(); _rejected.Clear();
        _candidateSince = null; _endingSince = null; _active = false;
    }
}
