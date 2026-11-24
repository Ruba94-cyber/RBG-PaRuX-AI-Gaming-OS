using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>One registry value PaRuX may touch. Everything lives under HKEY_CURRENT_USER (no admin, no HKLM policy).</summary>
internal sealed record GameBarValueId(string SubKey, string Name);

/// <summary>What a registry value looks like right now. <see cref="Dword"/> is null when the value exists with another type.</summary>
internal sealed record GameBarRegState(bool Exists, int? Dword)
{
    public static readonly GameBarRegState Absent = new(false, null);
}

/// <summary>Seam over HKCU. Only the four whitelisted values are ever read, written or deleted.</summary>
internal interface IGameBarRegistry
{
    GameBarRegState Read(GameBarValueId id);
    void WriteDword(GameBarValueId id, int value);
    void Delete(GameBarValueId id);
}

internal enum GameBarPlanKind { Write, SkipAlreadyOff, SkipOtherType, SkipAlreadyManaged }

/// <summary>A planned change: <see cref="Prior"/> is the exact previous state to put back (null = the value did not exist).</summary>
internal sealed record GameBarPlanItem(GameBarValueId Id, GameBarPlanKind Kind, int? Prior);

internal enum GameBarRestoreKind { Restore, LeaveUserChange, AlreadyGone }

internal sealed record GameBarRestorePlanItem(GameBarJournalValue Value, GameBarRestoreKind Kind);

/// <summary>The restore point: what each changed value held before (null = absent) and whether the owner asked for a permanent block.</summary>
internal sealed record GameBarJournalValue(string SubKey, string Name, int? Prior)
{
    public GameBarValueId Id => new(SubKey, Name);
}

internal sealed record GameBarJournal(bool Permanent, DateTime AppliedUtc, List<GameBarJournalValue> Values);

internal sealed record GameBarTargetStatus(string SubKey, string Name, bool Exists, int? Value);

internal sealed record GameBarStatus(bool Applied, bool Permanent, IReadOnlyList<GameBarTargetStatus> Targets, string Message);

internal enum GameBarOutcome { Ok, Refused, Failed }

internal sealed record GameBarResult(GameBarOutcome Outcome, string Message);

/// <summary>
/// Pure decisions, no I/O. Apply writes 0 only where the value is absent or non-zero; a value that already reads 0 is not ours to
/// "restore" and a value of another type is never touched. Restore puts the exact previous state back only while the value still
/// reads what PaRuX wrote; anything else is a later owner choice and is left alone.
/// </summary>
internal static class GameBarSuppressionPolicy
{
    internal const string GameBarKey = @"Software\Microsoft\GameBar";
    internal const string GameConfigKey = @"System\GameConfigStore";
    internal const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

    /// <summary>
    /// The complete whitelist. AutoGameModeEnabled/AllowAutoGameMode (Game Mode) and every other value stay untouched.
    /// UseNexusForGameBarEnabled is in the Microsoft "Reference for Windows 11 settings"; ShowStartupPanel, GameDVR_Enabled and
    /// AppCaptureEnabled are the values Windows Settings itself uses but are not in that reference (see the audit document).
    /// </summary>
    internal static readonly IReadOnlyList<GameBarValueId> Targets = new[]
    {
        new GameBarValueId(GameBarKey, "UseNexusForGameBarEnabled"),
        new GameBarValueId(GameBarKey, "ShowStartupPanel"),
        new GameBarValueId(GameConfigKey, "GameDVR_Enabled"),
        new GameBarValueId(GameDvrKey, "AppCaptureEnabled"),
    };

    internal const int OffValue = 0;

    internal static bool IsTarget(GameBarValueId id) =>
        Targets.Any(t => string.Equals(t.SubKey, id.SubKey, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, id.Name, StringComparison.Ordinal));

    internal static IReadOnlyList<GameBarPlanItem> PlanApply(Func<GameBarValueId, GameBarRegState> current, IReadOnlyCollection<GameBarJournalValue> alreadyJournaled)
    {
        var plan = new List<GameBarPlanItem>();
        foreach (GameBarValueId id in Targets)
        {
            if (alreadyJournaled.Any(j => j.Id == id)) { plan.Add(new(id, GameBarPlanKind.SkipAlreadyManaged, null)); continue; }
            GameBarRegState s = current(id);
            if (s.Exists && s.Dword is null) plan.Add(new(id, GameBarPlanKind.SkipOtherType, null));
            else if (s.Exists && s.Dword == OffValue) plan.Add(new(id, GameBarPlanKind.SkipAlreadyOff, null));
            else plan.Add(new(id, GameBarPlanKind.Write, s.Exists ? s.Dword : null));
        }
        return plan;
    }

    internal static IReadOnlyList<GameBarRestorePlanItem> PlanRestore(IEnumerable<GameBarJournalValue> values, Func<GameBarValueId, GameBarRegState> current)
    {
        var plan = new List<GameBarRestorePlanItem>();
        foreach (GameBarJournalValue v in values)
        {
            GameBarRegState s = current(v.Id);
            if (s.Exists && s.Dword == OffValue)
            {
                // Still exactly what PaRuX wrote. If the owner's previous state was "0" nothing would have been journaled.
                plan.Add(new(v, GameBarRestoreKind.Restore));
            }
            else plan.Add(new(v, s.Exists ? GameBarRestoreKind.LeaveUserChange : GameBarRestoreKind.AlreadyGone));
        }
        return plan;
    }
}

/// <summary>
/// "Block the Xbox Game Bar" for the current Windows user only: the controller Guide/Nexus button no longer opens the Game Bar, the
/// startup panel and Game DVR capture are off. Nothing else is changed; no HKLM policy, no admin, no process is launched. Every apply
/// needs an explicit consent flag, a write-ahead journal records the exact previous state (including "absent"), and restore never
/// overrides a value the owner changed afterwards. A permanent block is kept by automatic restores and only an explicit owner restore
/// removes it. The Win+G keyboard shortcut is not covered by these values.
/// </summary>
internal sealed class GameBarSuppressionService
{
    internal const long MaxJournalBytes = 8192;

    private readonly IGameBarRegistry _registry;
    private readonly string _journalPath;
    private readonly object _gate = new();

    public GameBarSuppressionService(IGameBarRegistry registry, string journalPath) { _registry = registry; _journalPath = journalPath; }

    public GameBarStatus Status()
    {
        lock (_gate)
        {
            GameBarJournal? journal = Load(out _);
            var targets = GameBarSuppressionPolicy.Targets.Select(t =>
            {
                GameBarRegState s = SafeRead(t);
                return new GameBarTargetStatus(t.SubKey, t.Name, s.Exists, s.Dword);
            }).ToArray();
            string message = journal is null ? "The Xbox Game Bar block is not active."
                : journal.Permanent ? "The Xbox Game Bar block is permanent until you restore it." : "The Xbox Game Bar block is active; it is restored when Console ends.";
            return new GameBarStatus(journal is not null, journal?.Permanent == true, targets, message);
        }
    }

    public GameBarResult Apply(bool consent, bool permanent)
    {
        if (!consent) return new(GameBarOutcome.Refused, "Blocking the Xbox Game Bar changes Windows settings for your user and needs your explicit consent.");
        lock (_gate)
        {
            GameBarJournal? journal = Load(out _);
            var existing = journal?.Values ?? new List<GameBarJournalValue>();
            IReadOnlyList<GameBarPlanItem> plan = GameBarSuppressionPolicy.PlanApply(SafeRead, existing);
            var writes = plan.Where(p => p.Kind == GameBarPlanKind.Write).ToList();
            bool makePermanent = permanent && journal?.Permanent != true;

            if (writes.Count == 0)
            {
                if (journal is not null && makePermanent) return SaveOrFail(journal with { Permanent = true }, "The Xbox Game Bar block is now permanent.");
                return new(GameBarOutcome.Ok, journal is null
                    ? "Nothing to change: the Xbox Game Bar values were already off or cannot be edited safely."
                    : "The Xbox Game Bar block is already active.");
            }

            var merged = new GameBarJournal(permanent || journal?.Permanent == true, journal?.AppliedUtc ?? DateTime.UtcNow,
                existing.Concat(writes.Select(w => new GameBarJournalValue(w.Id.SubKey, w.Id.Name, w.Prior))).ToList());
            try { Save(merged); }
            catch (InvalidOperationException ex) { return new(GameBarOutcome.Failed, ex.Message); }

            var written = new List<GameBarPlanItem>();
            foreach (GameBarPlanItem w in writes)
            {
                bool ok = SafeBool(() => { _registry.WriteDword(w.Id, GameBarSuppressionPolicy.OffValue); return true; })
                    && SafeRead(w.Id) is { Exists: true, Dword: GameBarSuppressionPolicy.OffValue };
                if (ok) { written.Add(w); continue; }
                // Undo what this call changed, keep what an earlier call journaled.
                foreach (GameBarPlanItem done in written.Append(w)) PutBack(new GameBarJournalValue(done.Id.SubKey, done.Id.Name, done.Prior));
                if (journal is null) DeleteJournal(); else TrySave(journal);
                return new(GameBarOutcome.Failed, "Windows did not accept the change; the previous Xbox Game Bar settings were put back.");
            }
            return new(GameBarOutcome.Ok, "Xbox Game Bar blocked for your user.");
        }
    }

    /// <summary>
    /// Puts the previous values back. A value the owner changed meanwhile is left as set. A permanent block is kept unless
    /// <paramref name="includePermanent"/> (explicit owner action). Safe to call when nothing is applied.
    /// </summary>
    public GameBarResult Restore(bool includePermanent = false)
    {
        lock (_gate)
        {
            GameBarJournal? journal = Load(out bool corrupt);
            if (journal is null)
                return new(GameBarOutcome.Ok, corrupt ? "A damaged Xbox Game Bar restore point was set aside." : "Nothing to restore.");
            if (journal.Permanent && !includePermanent)
                return new(GameBarOutcome.Ok, "The permanent Xbox Game Bar block is kept until you restore it yourself.");

            var failed = new List<GameBarJournalValue>();
            bool leftUserChoice = false;
            foreach (GameBarRestorePlanItem item in GameBarSuppressionPolicy.PlanRestore(journal.Values, SafeRead))
            {
                if (item.Kind == GameBarRestoreKind.LeaveUserChange) leftUserChoice = true;
                if (item.Kind != GameBarRestoreKind.Restore) continue;
                if (!PutBack(item.Value)) failed.Add(item.Value);
            }
            if (failed.Count > 0)
            {
                TrySave(journal with { Values = failed });
                return new(GameBarOutcome.Failed, "Some Xbox Game Bar settings could not be restored yet; the restore point is kept and retried.");
            }
            DeleteJournal();
            return new(GameBarOutcome.Ok, leftUserChoice
                ? "Xbox Game Bar settings restored; values you changed meanwhile were left as you set them."
                : "Previous Xbox Game Bar settings restored.");
        }
    }

    /// <summary>Startup/shutdown recovery for a session a crash or a forced stop left behind. A permanent block is kept.</summary>
    public GameBarResult RecoverAfterCrash() => Restore(includePermanent: false);

    private bool PutBack(GameBarJournalValue v)
    {
        // Verify the value still reads what we wrote before undoing it, so a late rollback never clobbers a user change.
        if (SafeRead(v.Id) is not { Exists: true, Dword: GameBarSuppressionPolicy.OffValue }) return true;
        bool ok = v.Prior is int prior
            ? SafeBool(() => { _registry.WriteDword(v.Id, prior); return true; })
            : SafeBool(() => { _registry.Delete(v.Id); return true; });
        return ok && (v.Prior is int p ? SafeRead(v.Id) is { Exists: true } s && s.Dword == p : !SafeRead(v.Id).Exists);
    }

    private GameBarRegState SafeRead(GameBarValueId id)
    {
        try { return _registry.Read(id); }
        catch (Exception ex) when (IsRegistryFailure(ex)) { return new GameBarRegState(true, null); } // unreadable = treat as "not ours to touch"
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); } catch (Exception ex) when (IsRegistryFailure(ex)) { return false; }
    }

    private static bool IsRegistryFailure(Exception ex) =>
        ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException;

    private GameBarJournal? Load(out bool corrupt)
    {
        corrupt = false;
        try
        {
            if (!File.Exists(_journalPath)) return null;
            if (new FileInfo(_journalPath).Length > MaxJournalBytes) throw new JsonException("journal too large");
            var j = JsonSerializer.Deserialize<GameBarJournal>(File.ReadAllText(_journalPath)) ?? throw new JsonException("null");
            if (j.Values is null || j.Values.Count == 0 || j.Values.Count > GameBarSuppressionPolicy.Targets.Count
                || j.Values.Any(v => !GameBarSuppressionPolicy.IsTarget(v.Id))) throw new JsonException("bad entry");
            return j;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            corrupt = true;
            try { File.Move(_journalPath, _journalPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: true); } catch { }
            return null;
        }
    }

    private GameBarResult SaveOrFail(GameBarJournal journal, string okMessage)
    {
        try { Save(journal); return new(GameBarOutcome.Ok, okMessage); }
        catch (InvalidOperationException ex) { return new(GameBarOutcome.Failed, ex.Message); }
    }

    private void TrySave(GameBarJournal journal)
    {
        try { Save(journal); } catch (InvalidOperationException) { }
    }

    private void Save(GameBarJournal journal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            string temp = _journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(journal));
            File.Move(temp, _journalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The Xbox Game Bar restore point could not be saved; nothing was changed.");
        }
    }

    private void DeleteJournal()
    {
        try { File.Delete(_journalPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>HKEY_CURRENT_USER implementation restricted to <see cref="GameBarSuppressionPolicy.Targets"/>.</summary>
internal sealed class WindowsGameBarRegistry : IGameBarRegistry
{
    private static void Guard(GameBarValueId id)
    {
        if (!GameBarSuppressionPolicy.IsTarget(id)) throw new InvalidOperationException("Registry value is not on the Game Bar whitelist.");
    }

    public GameBarRegState Read(GameBarValueId id)
    {
        Guard(id);
        if (!OperatingSystem.IsWindows()) return GameBarRegState.Absent;
        using RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(id.SubKey, writable: false);
        object? raw = key?.GetValue(id.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return GameBarRegState.Absent;
        return new GameBarRegState(true, raw is int i && key!.GetValueKind(id.Name) == RegistryValueKind.DWord ? i : null);
    }

    public void WriteDword(GameBarValueId id, int value)
    {
        Guard(id);
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows only.");
        using RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(id.SubKey, writable: true)
            ?? throw new InvalidOperationException("Registry key could not be opened.");
        key.SetValue(id.Name, value, RegistryValueKind.DWord);
    }

    public void Delete(GameBarValueId id)
    {
        Guard(id);
        if (!OperatingSystem.IsWindows()) return;
        using RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(id.SubKey, writable: true);
        key?.DeleteValue(id.Name, throwOnMissingValue: false);
    }
}
