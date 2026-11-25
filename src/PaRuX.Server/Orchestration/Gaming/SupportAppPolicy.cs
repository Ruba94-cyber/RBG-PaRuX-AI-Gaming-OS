using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>One running process that may be an entry's program.</summary>
public sealed record SupportAppProcess(int Pid, string? Path, DateTime? StartUtc);

public enum SupportAppStartDecision { Start, ReplaceStale, AlreadyRunning, SkippedDisabled, SkippedMissing, SkippedInvalid, SkippedGamingActive }

/// <summary>
/// Pure rules for the owner's support apps (helper programs started with Console). An entry is a program the owner chose
/// (USER_MANAGED, never installed or changed by PaRuX) or one PaRuX downloaded into its own folder after explicit consent
/// (PARUX_MANAGED, removable). Nothing here starts or stops a process.
/// </summary>
public static partial class SupportAppPolicy
{
    private static readonly char[] ShellMetaChars = ['&', '|', ';', '<', '>', '^', '`', '\n', '\r', '\0'];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    public static string ManagedRoot => PaRuXDataRoot.Combine("support-apps");

    public static bool IsValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    public static string NewId() => "app-" + Guid.NewGuid().ToString("N")[..12];

    public static SupportAppEntry Normalize(SupportAppEntry e) => e with
    {
        Id = (e.Id ?? string.Empty).Trim().ToLowerInvariant(),
        Name = (e.Name ?? string.Empty).Trim(),
        Path = (e.Path ?? string.Empty).Trim(),
        Args = string.IsNullOrWhiteSpace(e.Args) ? null : e.Args.Trim(),
        Process = string.IsNullOrWhiteSpace(e.Process) ? null : e.Process.Trim(),
        TimeoutSeconds = Math.Clamp(e.TimeoutSeconds, SupportAppLimits.MinTimeoutSeconds, SupportAppLimits.MaxTimeoutSeconds),
        CatalogId = string.IsNullOrWhiteSpace(e.CatalogId) ? null : e.CatalogId.Trim().ToLowerInvariant(),
        Version = string.IsNullOrWhiteSpace(e.Version) ? null : e.Version.Trim(),
    };

    public static bool IsInside(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static ComponentOwnership OwnershipOf(SupportAppEntry e, string managedRoot) =>
        e.CatalogId is not null && IsInside(e.Path, managedRoot) ? ComponentOwnership.PARUX_MANAGED : ComponentOwnership.USER_MANAGED;

    public static string? ProbeName(SupportAppEntry e)
    {
        string? n = !string.IsNullOrWhiteSpace(e.Process) ? e.Process : System.IO.Path.GetFileNameWithoutExtension(e.Path);
        if (string.IsNullOrWhiteSpace(n)) return null;
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>Entry admission. Returns an error text or null.</summary>
    public static string? Validate(SupportAppEntry e)
    {
        if (!IsValidId(e.Id)) return "The app id is not valid.";
        if (string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 80 || e.Name.Any(char.IsControl))
            return "The app name must be 1 to 80 characters without control characters.";
        if (e.Args is not null && (e.Args.Length > 1024 || e.Args.Any(char.IsControl) || e.Args.IndexOfAny(ShellMetaChars) >= 0))
            return $"{e.Name}: arguments must be at most 1024 characters, without control characters or shell metacharacters.";
        if (e.Process is { Length: > 0 } && !SignInAppListPolicy.IsValidProcessName(e.Process))
            return $"{e.Name}: the process name is not valid.";
        if (e.TimeoutSeconds is < SupportAppLimits.MinTimeoutSeconds or > SupportAppLimits.MaxTimeoutSeconds)
            return $"{e.Name}: the wait must be from {SupportAppLimits.MinTimeoutSeconds} to {SupportAppLimits.MaxTimeoutSeconds} seconds.";
        if (e.CatalogId is not null && SupportAppCatalog.Find(e.CatalogId) is null) return $"{e.Name}: unknown catalog entry.";
        if (!StructurallyValidPath(e.Path)) return $"{e.Name}: the path must be an absolute local .exe path (no network path or relative segment).";
        if (!GamingLocalFileSafetyPolicy.IsSafeRegularFile(e.Path, GamingLocalFileSafetyPolicy.ExecutableExtensions))
            return $"{e.Name}: the program was not found at this path (or it is a link or on a network drive).";
        return null;
    }

    /// <summary>True when only the file on disk is the problem (the entry is otherwise well formed): the owner can fix or remove it.</summary>
    public static bool IsAvailabilityProblem(SupportAppEntry e) =>
        IsValidId(e.Id) && StructurallyValidPath(e.Path) && !GamingLocalFileSafetyPolicy.IsSafeRegularFile(e.Path, GamingLocalFileSafetyPolicy.ExecutableExtensions);

    private static bool StructurallyValidPath(string? p) =>
        p is { Length: > 0 and <= 1024 } && !p.StartsWith(@"\\", StringComparison.Ordinal) && System.IO.Path.IsPathFullyQualified(p) &&
        !p.Split('\\', '/').Any(s => s == "..") && string.Equals(System.IO.Path.GetExtension(p), ".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The start decision for one entry. <paramref name="problem"/> is <see cref="Validate"/>'s text; running is what the process probe found.</summary>
    public static SupportAppStartDecision DecideStart(
        SupportAppEntry e, bool gamingActive, string? problem, bool availabilityProblem, IReadOnlyList<SupportAppProcess> running, DateTime? exeWriteUtc)
    {
        if (gamingActive) return SupportAppStartDecision.SkippedGamingActive;
        if (!e.Enabled) return SupportAppStartDecision.SkippedDisabled;
        if (problem is not null) return availabilityProblem ? SupportAppStartDecision.SkippedMissing : SupportAppStartDecision.SkippedInvalid;
        if (running.Count == 0) return SupportAppStartDecision.Start;
        // A running copy that was started before the program on disk was replaced is stale; only an entry that asked for it is replaced.
        bool stale = e.ReplaceIfStale && exeWriteUtc is DateTime written && running.All(p =>
            p.StartUtc is DateTime started && PathsMatch(p.Path, e.Path) && written > started.AddSeconds(2));
        return stale ? SupportAppStartDecision.ReplaceStale : SupportAppStartDecision.AlreadyRunning;
    }

    public static bool PathsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
