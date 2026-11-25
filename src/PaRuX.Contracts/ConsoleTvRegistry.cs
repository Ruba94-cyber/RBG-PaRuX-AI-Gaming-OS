using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Contracts;

/// <summary>
/// One monitor the owner has seen (connected now, or in the past) and may use as a TV for Console mode. The identifier is a hash of the
/// monitor's own EDID identity (manufacturer + product + serial), never the Windows <c>\\.\DISPLAYn</c> name, which changes when ports or
/// the arrangement change. Persisted in the existing desktop settings; the raw EDID serial is never stored or sent.
/// </summary>
public sealed class ConsoleTvEntry
{
    public string StableId { get; set; } = string.Empty;
    /// <summary>Readable name shown in Settings (EDID model name, or the Windows description).</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>EDID manufacturer + product code (for example "ABC1234"), shared by identical models. Optional.</summary>
    public string? MonitorCode { get; set; }
    /// <summary>The Windows display name this monitor had the last time it was connected. A hint only, never an identity.</summary>
    public string? LastDisplayId { get; set; }
    public DateTime LastSeenUtc { get; set; }
    /// <summary>True when the owner chose this monitor as a TV. A Console start looks for any selected TV that is connected.</summary>
    public bool Selected { get; set; }

    public ConsoleTvEntry Clone() => (ConsoleTvEntry)MemberwiseClone();
}

/// <summary>Pure monitor identity helpers: EDID parsing and the stable, privacy-preserving identifier.</summary>
public static class ConsoleMonitorIdentity
{
    public sealed record Info(string? Code, string? ModelName, string? StableId);

    public const int MaxIdLength = 64;

    /// <summary>
    /// Builds the identity from a base EDID block. When the monitor has no serial at all (many TVs report none), the caller-supplied
    /// <paramref name="instanceDiscriminator"/> (the Windows monitor instance path) keeps two identical models apart.
    /// Returns an Info with only null fields when the block is not a valid EDID.
    /// </summary>
    public static Info FromEdid(byte[]? edid, string? instanceDiscriminator = null)
    {
        if (edid is null || edid.Length < 128) return new Info(null, null, null);
        byte[] header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        if (!edid.AsSpan(0, 8).SequenceEqual(header)) return new Info(null, null, null);

        int vendor = (edid[8] << 8) | edid[9];
        char c1 = (char)(((vendor >> 10) & 0x1F) + 64);
        char c2 = (char)(((vendor >> 5) & 0x1F) + 64);
        char c3 = (char)((vendor & 0x1F) + 64);
        if (!IsLetter(c1) || !IsLetter(c2) || !IsLetter(c3)) return new Info(null, null, null);
        int product = edid[10] | (edid[11] << 8);
        string code = $"{c1}{c2}{c3}{product:X4}";
        uint serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));

        string? name = null, textSerial = null;
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0) continue;
            string text = ReadDescriptorText(edid, offset + 5);
            if (edid[offset + 3] == 0xFC && text.Length > 0) name = text;
            else if (edid[offset + 3] == 0xFF && text.Length > 0) textSerial = text;
        }

        string secret = serial != 0 || textSerial is not null
            ? $"{serial}|{textSerial}"
            : "inst:" + (instanceDiscriminator ?? string.Empty);
        return new Info(code, name, Make(code, secret));
    }

    /// <summary>Fallback when no EDID could be read: still stable per Windows monitor instance, never claims to be hardware-unique.</summary>
    public static Info FromInstancePath(string? pnpCode, string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(pnpCode)) return new Info(null, null, null);
        string code = pnpCode.Trim().ToUpperInvariant();
        if (code.Length > 16 || code.Any(ch => !char.IsAsciiLetterOrDigit(ch))) return new Info(null, null, null);
        return new Info(code, null, Make(code, "inst:" + (instancePath ?? string.Empty)));
    }

    /// <summary>Parses a Windows monitor interface name, <c>\\?\DISPLAY#CODE#instance#{guid}</c>.</summary>
    public static (string? Code, string? Instance) ParseInterfaceName(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName)) return (null, null);
        string[] parts = interfaceName.Split('#');
        if (parts.Length < 3 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return (null, null);
        string code = parts[1], instance = parts[2];
        if (code.Length is 0 or > 16 || code.Any(ch => !char.IsAsciiLetterOrDigit(ch))) return (null, null);
        if (instance.Length is 0 or > 128 || instance.Any(char.IsControl)) return (null, null);
        return (code, instance);
    }

    /// <summary>The identifier to remember a display by: the EDID-derived one when Core supplied it, otherwise the Windows name (a weak,
    /// session-bound fallback that never matches a different monitor).</summary>
    public static string IdOf(GamingDisplayModeDto display) =>
        ValidStableId(display.StableId) ? display.StableId! : "win:" + display.DisplayId;

    public static bool ValidStableId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaxIdLength && !id.Any(char.IsControl);

    private static string Make(string code, string secret)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(code + "|" + secret));
        return code + "-" + Convert.ToHexString(hash, 0, 6);
    }

    private static bool IsLetter(char c) => c is >= 'A' and <= 'Z';

    private static string ReadDescriptorText(byte[] edid, int start)
    {
        var sb = new StringBuilder(13);
        for (int i = start; i < start + 13 && i < edid.Length; i++)
        {
            byte b = edid[i];
            if (b == 0x0A) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }
}

/// <summary>Where a Console decision gets its display list from. The real one asks the local Core; tests plug in a fake and never touch Windows.</summary>
public interface IConsoleTopologyProvider
{
    /// <summary>The current display snapshot, or null when it could not be read.</summary>
    Task<GamingDisplaySnapshotDto?> ReadAsync(CancellationToken ct);
}

/// <summary>Test seam: returns scripted snapshots (the last one repeats). Reads nothing from Windows and changes nothing.</summary>
public sealed class FakeConsoleTopologyProvider : IConsoleTopologyProvider
{
    private readonly GamingDisplaySnapshotDto?[] _script;
    private int _calls;

    public FakeConsoleTopologyProvider(params GamingDisplaySnapshotDto?[] script) =>
        _script = script is { Length: > 0 } ? script : new GamingDisplaySnapshotDto?[] { null };

    public int Calls => Volatile.Read(ref _calls);

    public Task<GamingDisplaySnapshotDto?> ReadAsync(CancellationToken ct)
    {
        int index = Math.Min(Interlocked.Increment(ref _calls) - 1, _script.Length - 1);
        return Task.FromResult(_script[index]);
    }

    /// <summary>A synthetic single-monitor PC (the primary), which is how most owners' desktops look.</summary>
    public static GamingDisplaySnapshotDto SingleMonitor(string stableId = "TST0001-000000000001", string name = "Test monitor") =>
        new(true, "fake", new[]
        {
            new GamingDisplayModeDto(@"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 60) { FriendlyName = name, StableId = stableId, MonitorCode = "TST0001" }
        });
}

/// <summary>
/// Test/Debug-only seam that declares the PRIMARY monitor of this PC to be a TV, without touching any Windows setting or saved choice.
/// The environment variable is honoured only in Debug builds; a Release build always ignores it.
/// </summary>
public static class ConsoleTvSimulation
{
    public const string EnvironmentVariable = "PARUX_TEST_TV_MONITOR";

    public enum Mode { Off, CurrentPrimary }

#if DEBUG
    public const bool EnvironmentSeamCompiledIn = true;
#else
    public const bool EnvironmentSeamCompiledIn = false;
#endif

    public static Mode Parse(string? value) =>
        string.Equals(value?.Trim(), "current", StringComparison.OrdinalIgnoreCase) ? Mode.CurrentPrimary : Mode.Off;

    public static Mode FromEnvironment() =>
        EnvironmentSeamCompiledIn ? Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)) : Mode.Off;
}

/// <summary>Result of applying the TV list to the current monitors, in the shape the older single-target settings expect.</summary>
public sealed record ConsoleTvSyncResult(
    IReadOnlyList<ConsoleTvEntry> Tvs,
    IReadOnlyList<GamingDisplayModeDto> ConnectedTvs,
    IReadOnlyList<ConsoleDisplayAssignment> Roles,
    string TargetDisplayId);

/// <summary>
/// The persistent monitor history and TV selection. Pure functions over immutable-by-convention lists: record what is connected, choose
/// any number of monitors (connected or only remembered) as TVs, forget one, or declare the current primary monitor a TV.
/// </summary>
public static class ConsoleTvRegistry
{
    public const int MaxEntries = 32;
    public const string DisconnectedTargetPlaceholder = "tv-not-connected";

    /// <summary>Bounds and de-duplicates a list that may come from an old, edited or corrupt settings file.</summary>
    public static List<ConsoleTvEntry> Sanitize(IEnumerable<ConsoleTvEntry?>? source)
    {
        var map = new Dictionary<string, ConsoleTvEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ConsoleTvEntry? raw in source ?? Array.Empty<ConsoleTvEntry?>())
        {
            if (raw is null || !ConsoleMonitorIdentity.ValidStableId(raw.StableId)) continue;
            ConsoleTvEntry e = raw.Clone();
            e.Name = Clean(e.Name, 100);
            if (e.Name.Length == 0) e.Name = e.StableId;
            e.MonitorCode = e.MonitorCode is { Length: > 0 and <= 16 } mc && !mc.Any(char.IsControl) ? mc : null;
            e.LastDisplayId = ConsoleDisplaySelectionPolicy.ValidDisplayId(e.LastDisplayId) ? e.LastDisplayId : null;
            if (map.TryGetValue(e.StableId, out ConsoleTvEntry? old))
            {
                old.Selected |= e.Selected;
                if (e.LastSeenUtc > old.LastSeenUtc) { old.LastSeenUtc = e.LastSeenUtc; old.Name = e.Name; old.LastDisplayId = e.LastDisplayId; }
            }
            else map[e.StableId] = e;
        }
        // Keep every selected entry; drop the least recently seen unselected ones past the cap.
        return map.Values.OrderByDescending(e => e.Selected).ThenByDescending(e => e.LastSeenUtc)
            .Take(MaxEntries).OrderByDescending(e => e.LastSeenUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds or refreshes every connected monitor in the history. Never changes who is selected.</summary>
    public static List<ConsoleTvEntry> RecordSeen(IEnumerable<ConsoleTvEntry?>? history, GamingDisplaySnapshotDto? snapshot, DateTime nowUtc)
    {
        List<ConsoleTvEntry> list = Sanitize(history);
        if (snapshot is not { Supported: true }) return list;
        foreach (GamingDisplayModeDto d in snapshot.Displays)
        {
            if (!ConsoleDisplaySelectionPolicy.ValidDisplayId(d.DisplayId)) continue;
            string id = ConsoleMonitorIdentity.IdOf(d);
            ConsoleTvEntry? e = list.FirstOrDefault(x => string.Equals(x.StableId, id, StringComparison.OrdinalIgnoreCase));
            if (e is null) { e = new ConsoleTvEntry { StableId = id }; list.Add(e); }
            e.Name = Clean(DisplayName(d), 100);
            e.MonitorCode = d.MonitorCode is { Length: > 0 and <= 16 } ? d.MonitorCode : e.MonitorCode;
            e.LastDisplayId = d.DisplayId;
            e.LastSeenUtc = nowUtc;
        }
        return Sanitize(list);
    }

    public static List<ConsoleTvEntry> SetSelected(IEnumerable<ConsoleTvEntry?>? history, string stableId, bool selected)
    {
        List<ConsoleTvEntry> list = Sanitize(history);
        ConsoleTvEntry? e = list.FirstOrDefault(x => string.Equals(x.StableId, stableId, StringComparison.OrdinalIgnoreCase));
        if (e is not null) e.Selected = selected;
        return list;
    }

    /// <summary>Removes one remembered monitor (and with it its TV selection). A connected monitor is listed again at the next detection.</summary>
    public static List<ConsoleTvEntry> Forget(IEnumerable<ConsoleTvEntry?>? history, string stableId) =>
        Sanitize(history).Where(x => !string.Equals(x.StableId, stableId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>"Use the current monitor as TV": the primary connected monitor is recorded and selected. Other selections stay.</summary>
    public static List<ConsoleTvEntry> SelectCurrentMonitor(IEnumerable<ConsoleTvEntry?>? history, GamingDisplaySnapshotDto? snapshot, DateTime nowUtc)
    {
        List<ConsoleTvEntry> list = RecordSeen(history, snapshot, nowUtc);
        GamingDisplayModeDto? primary = PrimaryOf(snapshot);
        if (primary is null) return list;
        return SetSelected(list, ConsoleMonitorIdentity.IdOf(primary), true);
    }

    /// <summary>
    /// Owner-supplied name hints (the legacy launcher's <c>tvNames</c>): selects every remembered monitor whose name contains a hint.
    /// Returns the updated list and how many entries it newly selected. Nothing is created from a bare name.
    /// </summary>
    public static (List<ConsoleTvEntry> Tvs, int Selected) SelectByNameHints(IEnumerable<ConsoleTvEntry?>? history, IEnumerable<string>? hints)
    {
        List<ConsoleTvEntry> list = Sanitize(history);
        string[] needles = (hints ?? Array.Empty<string>()).Select(h => h?.Trim() ?? string.Empty).Where(h => h.Length >= 2).ToArray();
        int count = 0;
        foreach (ConsoleTvEntry e in list)
            if (!e.Selected && needles.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            { e.Selected = true; count++; }
        return (list, count);
    }

    /// <summary>What importing legacy <c>tvNames</c> would do, computed without changing anything: remembered monitors that would become
    /// TVs, ones already chosen, and hint names that match no remembered monitor (listed honestly so the owner can see them).</summary>
    public sealed record NameHintPreview(IReadOnlyList<ConsoleTvEntry> WouldSelect, IReadOnlyList<ConsoleTvEntry> AlreadySelected,
        IReadOnlyList<string> NoMatch);

    public static NameHintPreview PreviewNameHints(IEnumerable<ConsoleTvEntry?>? history, IEnumerable<string>? hints)
    {
        List<ConsoleTvEntry> list = Sanitize(history);
        string[] needles = (hints ?? Array.Empty<string>()).Select(h => h?.Trim() ?? string.Empty).Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool Matches(ConsoleTvEntry e, string n) => n.Length >= 2 && e.Name.Contains(n, StringComparison.OrdinalIgnoreCase);
        var would = list.Where(e => !e.Selected && needles.Any(n => Matches(e, n))).ToList();
        var already = list.Where(e => e.Selected && needles.Any(n => Matches(e, n))).ToList();
        var none = needles.Where(n => !list.Any(e => Matches(e, n))).ToList();
        return new NameHintPreview(would, already, none);
    }

    public static GamingDisplayModeDto? PrimaryOf(GamingDisplaySnapshotDto? snapshot) =>
        snapshot is { Supported: true }
            ? snapshot.Displays.Where(d => d.Width > 0 && d.Height > 0 && ConsoleDisplaySelectionPolicy.ValidDisplayId(d.DisplayId))
                .OrderByDescending(d => d.Primary).FirstOrDefault()
            : null;

    /// <summary>The connected monitors that are TVs: those matching a selected entry, plus (test seam only) the primary monitor.</summary>
    public static List<GamingDisplayModeDto> ConnectedTvs(IEnumerable<ConsoleTvEntry?>? history, GamingDisplaySnapshotDto? snapshot,
        ConsoleTvSimulation.Mode simulation = ConsoleTvSimulation.Mode.Off)
    {
        var result = new List<GamingDisplayModeDto>();
        if (snapshot is not { Supported: true }) return result;
        List<ConsoleTvEntry> all = Sanitize(history);
        var displays = snapshot.Displays.Where(d => d.Width > 0 && d.Height > 0 && ConsoleDisplaySelectionPolicy.ValidDisplayId(d.DisplayId)).ToList();
        var knownIds = new HashSet<string>(all.Select(e => e.StableId), StringComparer.OrdinalIgnoreCase);

        foreach (ConsoleTvEntry entry in all.Where(e => e.Selected))
        {
            GamingDisplayModeDto? match = displays.FirstOrDefault(d => string.Equals(ConsoleMonitorIdentity.IdOf(d), entry.StableId, StringComparison.OrdinalIgnoreCase));
            // Same model on another port or after a driver change: accept it only when it is unambiguous and not another remembered monitor.
            if (match is null && !string.IsNullOrEmpty(entry.MonitorCode))
            {
                var sameModel = displays.Where(d => string.Equals(d.MonitorCode, entry.MonitorCode, StringComparison.OrdinalIgnoreCase)
                    && !knownIds.Contains(ConsoleMonitorIdentity.IdOf(d))).ToList();
                if (sameModel.Count == 1) match = sameModel[0];
            }
            if (match is not null && !result.Any(r => string.Equals(r.DisplayId, match.DisplayId, StringComparison.OrdinalIgnoreCase)))
                result.Add(match);
        }
        if (simulation == ConsoleTvSimulation.Mode.CurrentPrimary && PrimaryOf(snapshot) is { } primary
            && !result.Any(r => string.Equals(r.DisplayId, primary.DisplayId, StringComparison.OrdinalIgnoreCase)))
            result.Add(primary);
        return result;
    }

    /// <summary>True when at least one monitor is selected as TV (connected or not), or the test seam is on.</summary>
    public static bool HasSelection(IEnumerable<ConsoleTvEntry?>? history, ConsoleTvSimulation.Mode simulation = ConsoleTvSimulation.Mode.Off) =>
        simulation != ConsoleTvSimulation.Mode.Off || Sanitize(history).Any(e => e.Selected);

    /// <summary>
    /// One-time, safe migration of the older single-TV setting: when the legacy target display is connected now, it becomes a selected
    /// history entry. Returns the list unchanged when there is nothing to migrate or the legacy display is not connected.
    /// </summary>
    public static List<ConsoleTvEntry> MigrateLegacy(IEnumerable<ConsoleTvEntry?>? history, GamingDisplaySnapshotDto? snapshot,
        IEnumerable<ConsoleDisplayAssignment>? legacyRoles, string? legacyTargetId, DateTime nowUtc)
    {
        List<ConsoleTvEntry> list = RecordSeen(history, snapshot, nowUtc);
        if (snapshot is not { Supported: true } || list.Any(e => e.Selected)) return list;
        var candidates = new List<string>();
        if (ConsoleDisplaySelectionPolicy.ValidDisplayId(legacyTargetId)) candidates.Add(legacyTargetId!);
        candidates.AddRange((legacyRoles ?? Array.Empty<ConsoleDisplayAssignment>())
            .Where(a => a is not null && a.Role == ConsoleDisplaySelectionPolicy.TvRole && ConsoleDisplaySelectionPolicy.ValidDisplayId(a.DisplayId))
            .Select(a => a.DisplayId));
        foreach (string id in candidates)
        {
            GamingDisplayModeDto? d = snapshot.Displays.FirstOrDefault(x => string.Equals(x.DisplayId, id, StringComparison.OrdinalIgnoreCase));
            if (d is not null) list = SetSelected(list, ConsoleMonitorIdentity.IdOf(d), true);
        }
        return list;
    }

    /// <summary>
    /// Applies the TV list to a fresh snapshot: refreshes the history and derives the legacy per-display roles and single target that
    /// the rest of Console (window routing, Playnite display, game routing) already reads. A selected TV that is not connected leaves a
    /// target that matches nothing, so Console never silently uses another screen; with no TV selected the target is empty (current screen).
    /// </summary>
    public static ConsoleTvSyncResult Sync(IEnumerable<ConsoleTvEntry?>? history, GamingDisplaySnapshotDto? snapshot,
        IEnumerable<ConsoleDisplayAssignment>? legacyRoles, string? legacyTargetId, DateTime nowUtc,
        ConsoleTvSimulation.Mode simulation = ConsoleTvSimulation.Mode.Off, bool migrateLegacy = true)
    {
        List<ConsoleTvEntry> tvs = migrateLegacy
            ? MigrateLegacy(history, snapshot, legacyRoles, legacyTargetId, nowUtc)
            : RecordSeen(history, snapshot, nowUtc);
        List<GamingDisplayModeDto> connected = ConnectedTvs(tvs, snapshot, simulation);

        var roles = (legacyRoles ?? Array.Empty<ConsoleDisplayAssignment>()).Where(a => a is not null).ToList();
        if (snapshot is { Supported: true })
        {
            foreach (GamingDisplayModeDto d in snapshot.Displays.Where(x => ConsoleDisplaySelectionPolicy.ValidDisplayId(x.DisplayId)))
            {
                roles.RemoveAll(a => string.Equals(a.DisplayId, d.DisplayId, StringComparison.OrdinalIgnoreCase));
                bool tv = connected.Any(c => string.Equals(c.DisplayId, d.DisplayId, StringComparison.OrdinalIgnoreCase));
                roles.Add(new ConsoleDisplayAssignment(d.DisplayId, tv ? ConsoleDisplaySelectionPolicy.TvRole : ConsoleDisplaySelectionPolicy.MonitorRole));
            }
        }

        string target;
        if (connected.Count > 0) target = connected[0].DisplayId;
        else if (snapshot is not { Supported: true }) target = legacyTargetId ?? string.Empty;   // unreadable topology: keep what was saved
        else if (tvs.Any(e => e.Selected))
            target = tvs.Where(e => e.Selected).Select(e => e.LastDisplayId).FirstOrDefault(ConsoleDisplaySelectionPolicy.ValidDisplayId) ?? DisconnectedTargetPlaceholder;
        else target = string.Empty;
        return new ConsoleTvSyncResult(tvs, connected, roles, target);
    }

    public static string DisplayName(GamingDisplayModeDto d) =>
        !string.IsNullOrWhiteSpace(d.FriendlyName) ? d.FriendlyName!.Trim()
        : !string.IsNullOrWhiteSpace(d.MonitorCode) ? d.MonitorCode! : d.DisplayId;

    private static string Clean(string? value, int max)
    {
        string v = new string((value ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return v.Length <= max ? v : v[..max];
    }
}

/// <summary>Bounded wait for a selected TV at start, driven by an <see cref="IConsoleTopologyProvider"/> so it is testable without a display.</summary>
public static class ConsoleStartupTvProbe
{
    public sealed record Result(bool HasSelection, bool TvPresent, string? DisplayId, GamingDisplaySnapshotDto? Snapshot);

    public static async Task<Result> RunAsync(IConsoleTopologyProvider provider, Func<IReadOnlyList<ConsoleTvEntry>> selection,
        ConsoleTvSimulation.Mode simulation, Func<TimeSpan, CancellationToken, Task> delay, Func<bool> shuttingDown,
        int maxProbes = ConsoleStartupProbePolicy.DefaultMaxProbes, int absentConfirmations = ConsoleStartupProbePolicy.DefaultAbsentConfirmations,
        TimeSpan? interval = null, CancellationToken ct = default)
    {
        if (!ConsoleTvRegistry.HasSelection(selection(), simulation))
            return new Result(false, false, null, null);   // no TV chosen: nothing to wait for, Console never depends on one

        GamingDisplaySnapshotDto? last = null;
        int absentSeen = 0;
        for (int probe = 0; probe < Math.Max(1, maxProbes) && !shuttingDown() && !ct.IsCancellationRequested; probe++)
        {
            GamingDisplaySnapshotDto? snapshot = null;
            try { snapshot = await provider.ReadAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { snapshot = null; }
            bool readable = snapshot is { Supported: true };
            if (readable) last = snapshot;
            List<GamingDisplayModeDto> tvs = ConsoleTvRegistry.ConnectedTvs(selection(), snapshot, simulation);
            bool present = readable && tvs.Count > 0;
            if (readable && !present) absentSeen++;
            ConsoleProbeStep step = ConsoleStartupProbePolicy.Next(probe, maxProbes, readable, present, absentSeen, absentConfirmations);
            if (step == ConsoleProbeStep.Present) return new Result(true, true, tvs[0].DisplayId, last);
            if (step is ConsoleProbeStep.Absent or ConsoleProbeStep.GiveUp) break;
            await delay(interval ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        return new Result(true, false, null, last);
    }
}
