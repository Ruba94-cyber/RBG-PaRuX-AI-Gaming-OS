using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PaRuX.Server.Orchestration.Gaming;

internal sealed record PowerPlanInfo(string Id, string Name);

/// <summary>Seam over the Windows power-scheme API (no process is launched). Only existing schemes are ever selected; none is created or edited.</summary>
internal interface IPowerPlanApi
{
    string? GetActive();
    IReadOnlyList<PowerPlanInfo> List();
    bool SetActive(string id);
}

/// <summary>The restore point of an owner-requested power plan switch: what was active before, and what PaRuX selected.</summary>
internal sealed record PowerProfileJournalEntry(string PreviousPlanId, string AppliedPlanId, DateTime AppliedUtc);

internal sealed record PowerProfileStatus(bool Applied, string? PreviousPlanId, string? AppliedPlanId, string? ActivePlanId,
    bool RestorePending, string Message, IReadOnlyList<PowerPlanInfo> Available);

internal enum PowerProfileOutcome { Ok, Refused, Failed }

internal sealed record PowerProfileResult(PowerProfileOutcome Outcome, string Message);

/// <summary>
/// Opt-in "Console power plan": select an EXISTING Windows power plan while Console/Playnite is in use and put the previous one
/// back afterwards. A write-ahead journal makes the restore survive a crash; the restore never overrides a plan the owner chose in
/// the meantime. Nothing is created, edited or deleted in Windows; no process is launched (powrprof API only); every apply needs
/// an explicit consent flag from the caller.
/// </summary>
internal sealed class PowerProfileService
{
    /// <summary>Windows "Balanced": the only fallback used when the plan active before no longer exists.</summary>
    internal const string BalancedPlanId = "381b4222-f694-41f0-9685-ff5bb260df2e";
    internal const long MaxJournalBytes = 4096;

    private readonly IPowerPlanApi _api;
    private readonly string _journalPath;
    private readonly object _gate = new();

    public PowerProfileService(IPowerPlanApi api, string journalPath) { _api = api; _journalPath = journalPath; }

    public static bool TryNormalizeId(string? id, out string normalized)
    {
        normalized = string.Empty;
        if (id is null || id.Length != 36 || !Guid.TryParseExact(id, "D", out Guid g)) return false;
        normalized = g.ToString("D");
        return true;
    }

    public PowerProfileStatus Status()
    {
        lock (_gate)
        {
            PowerProfileJournalEntry? entry = Load(out _);
            string? active = Safe(_api.GetActive);
            IReadOnlyList<PowerPlanInfo> plans = SafeList();
            return new PowerProfileStatus(entry is not null, entry?.PreviousPlanId, entry?.AppliedPlanId, active, entry is not null,
                entry is null ? "No power plan change is active." : "A Console power plan is active; it is restored when Console ends.", plans);
        }
    }

    public PowerProfileResult Apply(string? planId, bool consent)
    {
        if (!consent) return new(PowerProfileOutcome.Refused, "Changing the Windows power plan needs your explicit consent.");
        if (!TryNormalizeId(planId, out string wanted))
            return new(PowerProfileOutcome.Refused, "planId must be a power plan GUID.");
        lock (_gate)
        {
            PowerProfileJournalEntry? existing = Load(out _);
            if (existing is not null)
                return existing.AppliedPlanId == wanted
                    ? new(PowerProfileOutcome.Ok, "That power plan is already active for Console.")
                    : new(PowerProfileOutcome.Refused, "Restore the previous power plan first.");
            if (!SafeList().Any(p => p.Id == wanted))
                return new(PowerProfileOutcome.Refused, "That power plan does not exist on this PC; PaRuX never creates plans.");
            string? previous = Safe(_api.GetActive);
            if (previous is null) return new(PowerProfileOutcome.Failed, "The current power plan could not be read; nothing was changed.");
            if (previous == wanted) return new(PowerProfileOutcome.Ok, "That power plan is already the active one; nothing was changed.");
            try { Save(new PowerProfileJournalEntry(previous, wanted, DateTime.UtcNow)); }
            catch (InvalidOperationException ex) { return new(PowerProfileOutcome.Failed, ex.Message); }

            bool set = SafeBool(() => _api.SetActive(wanted));
            if (!set || Safe(_api.GetActive) != wanted)
            {
                // Put back what was there and drop the restore point only if that worked.
                if (Safe(_api.GetActive) == previous || SafeBool(() => _api.SetActive(previous))) Delete();
                return new(PowerProfileOutcome.Failed, "Windows did not accept the power plan change; the previous plan is active.");
            }
            return new(PowerProfileOutcome.Ok, "Console power plan active.");
        }
    }

    /// <summary>Restores the previous plan. A plan the owner selected meanwhile is left alone. Safe to call when nothing is applied.</summary>
    public PowerProfileResult Restore()
    {
        lock (_gate)
        {
            PowerProfileJournalEntry? entry = Load(out bool corrupt);
            if (entry is null)
                return new(PowerProfileOutcome.Ok, corrupt ? "A damaged power plan restore point was set aside." : "Nothing to restore.");
            string? active = Safe(_api.GetActive);
            if (active is not null && active != entry.AppliedPlanId)
            {
                Delete();
                return new(PowerProfileOutcome.Ok, "The power plan was changed meanwhile; it was left as you set it.");
            }
            string target = SafeList().Any(p => p.Id == entry.PreviousPlanId) ? entry.PreviousPlanId : BalancedPlanId;
            if (!SafeBool(() => _api.SetActive(target)))
                return new(PowerProfileOutcome.Failed, "The previous power plan could not be restored yet; the restore point is kept and retried.");
            Delete();
            return new(PowerProfileOutcome.Ok, "Previous power plan restored.");
        }
    }

    /// <summary>Startup/shutdown recovery: the same restore, for a session a crash or a forced stop left behind.</summary>
    public PowerProfileResult RecoverAfterCrash() => Restore();

    private static string? Safe(Func<string?> f)
    {
        try { return f(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private IReadOnlyList<PowerPlanInfo> SafeList()
    {
        try { return _api.List(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { return Array.Empty<PowerPlanInfo>(); }
    }

    private PowerProfileJournalEntry? Load(out bool corrupt)
    {
        corrupt = false;
        try
        {
            if (!File.Exists(_journalPath)) return null;
            if (new FileInfo(_journalPath).Length > MaxJournalBytes) throw new JsonException("journal too large");
            var e = JsonSerializer.Deserialize<PowerProfileJournalEntry>(File.ReadAllText(_journalPath)) ?? throw new JsonException("null");
            if (!TryNormalizeId(e.PreviousPlanId, out string p) || !TryNormalizeId(e.AppliedPlanId, out string a)) throw new JsonException("bad id");
            return e with { PreviousPlanId = p, AppliedPlanId = a };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            corrupt = true;
            try { File.Move(_journalPath, _journalPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: true); } catch { }
            return null;
        }
    }

    private void Save(PowerProfileJournalEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            string temp = _journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, _journalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The power plan restore point could not be saved; nothing was changed.");
        }
    }

    private void Delete()
    {
        try { File.Delete(_journalPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>powrprof.dll implementation. Reads and selects schemes only.</summary>
internal sealed class WindowsPowerPlanApi : IPowerPlanApi
{
    private const uint AccessScheme = 16;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRoot, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr userRoot, ref Guid schemeGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr schemeGuid, IntPtr subGroup, uint flags, uint index, IntPtr buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid schemeGuid, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);

    public string? GetActive()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr).ToString("D"); }
        finally { LocalFree(ptr); }
    }

    public IReadOnlyList<PowerPlanInfo> List()
    {
        var plans = new List<PowerPlanInfo>();
        if (!OperatingSystem.IsWindows()) return plans;
        for (uint index = 0; index < 64; index++)
        {
            uint size = (uint)Marshal.SizeOf<Guid>();
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
                if (rc != 0) break;   // ERROR_NO_MORE_ITEMS or any failure ends the listing
                Guid id = Marshal.PtrToStructure<Guid>(buffer);
                plans.Add(new PowerPlanInfo(id.ToString("D"), FriendlyName(id)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return plans;
    }

    private static string FriendlyName(Guid id)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size < 2 || size > 4096) return id.ToString("D");
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return id.ToString("D");
            string? name = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(name) ? id.ToString("D") : name.Length > 80 ? name[..80] : name;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public bool SetActive(string id)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(id, "D", out Guid g)) return false;
        return PowerSetActiveScheme(IntPtr.Zero, ref g) == 0;
    }
}
