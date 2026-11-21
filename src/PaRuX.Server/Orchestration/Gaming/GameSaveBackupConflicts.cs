using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Divergence between this PC's save backups and the mirror folder. Backups are immutable archives with unique ids, so a game has
/// diverged when BOTH sides hold newer backups the other lacks (two devices played on without syncing). Plain sync would merge
/// both histories silently; instead the game is held back, the owner sees the metadata side by side and chooses.
/// Nothing is ever deleted: every choice keeps every backup on both sides, and the choice only decides which one is marked current.
/// </summary>
public sealed partial class GameSaveBackupService
{
    private string DecisionsPath => Path.Combine(Path.GetDirectoryName(_root)!, "game-save-sync-decisions.json");

    private sealed record StoredDecision(string Label, string Choice, string? PreferredId, string Fingerprint, DateTime DecidedUtc);

    /// <summary>One game's raw comparison, before decisions are applied.</summary>
    internal sealed record Divergence(
        string Label, IReadOnlyList<GameSaveBackupDto> LocalOnly, IReadOnlyList<GameSaveBackupDto> MirrorOnly,
        int CommonCount, string Fingerprint);

    private static string Key(string? label) => (label ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Pure comparison. A game diverged when each side has a backup newer than the last one both share.</summary>
    internal static IReadOnlyList<Divergence> Detect(IReadOnlyList<GameSaveBackupDto> local, IReadOnlyList<GameSaveBackupDto> mirror)
    {
        var result = new List<Divergence>();
        var mirrorIds = mirror.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var localIds = local.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        foreach (IGrouping<string, GameSaveBackupDto> group in local.Concat(mirror.Where(m => !localIds.Contains(m.Id))).GroupBy(b => Key(b.Label)))
        {
            List<GameSaveBackupDto> all = group.ToList();
            List<GameSaveBackupDto> common = all.Where(b => localIds.Contains(b.Id) && mirrorIds.Contains(b.Id)).ToList();
            List<GameSaveBackupDto> localOnly = all.Where(b => localIds.Contains(b.Id) && !mirrorIds.Contains(b.Id)).OrderByDescending(b => b.CreatedUtc).ToList();
            List<GameSaveBackupDto> mirrorOnly = all.Where(b => mirrorIds.Contains(b.Id) && !localIds.Contains(b.Id)).OrderByDescending(b => b.CreatedUtc).ToList();
            if (localOnly.Count == 0 || mirrorOnly.Count == 0) continue;
            DateTime baseline = common.Count == 0 ? DateTime.MinValue : common.Max(b => b.CreatedUtc);
            if (localOnly[0].CreatedUtc <= baseline || mirrorOnly[0].CreatedUtc <= baseline) continue;   // old stragglers, not a fork
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('|', localOnly.Select(b => b.Id).OrderBy(i => i, StringComparer.Ordinal))
                + "#" + string.Join('|', mirrorOnly.Select(b => b.Id).OrderBy(i => i, StringComparer.Ordinal))))).ToLowerInvariant();
            result.Add(new Divergence(localOnly[0].Label, localOnly, mirrorOnly, common.Count, fingerprint));
        }
        return result;
    }

    private IReadOnlyList<GameSaveBackupDto> ReadMirror(string target)
    {
        if (!Directory.Exists(target)) return Array.Empty<GameSaveBackupDto>();
        var list = new List<GameSaveBackupDto>();
        foreach (string path in Directory.EnumerateFiles(target, "save-*.json").Take(MaxArchives * 2))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 37) continue;
            string id = name[5..];
            Manifest? manifest = ReadManifestIn(target, id);
            if (manifest != null && File.Exists(Path.Combine(target, "save-" + id + ".zip"))) list.Add(manifest.Backup);
        }
        return list;
    }

    private string? MirrorTarget()
    {
        string? configured = ReadSyncConfig();
        return configured is null ? null : Path.Combine(ValidateSyncFolder(configured), SyncSubfolder);
    }

    private List<StoredDecision> ReadDecisions()
    {
        try
        {
            return File.Exists(DecisionsPath)
                ? JsonSerializer.Deserialize<List<StoredDecision>>(File.ReadAllText(DecisionsPath)) ?? new List<StoredDecision>()
                : new List<StoredDecision>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new List<StoredDecision>(); }
    }

    private void WriteDecisions(List<StoredDecision> decisions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DecisionsPath)!);
        string temp = DecisionsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(decisions));
        File.Move(temp, DecisionsPath, overwrite: true);
    }

    private static GameSaveSideDto Side(GameSaveBackupDto b) => new(b.Id, b.CreatedUtc, b.Files, b.Bytes);

    /// <summary>The diverged games with a side-by-side metadata preview, and the choices already recorded. Reads only manifests.</summary>
    public GameSaveSyncConflictsDto SyncConflicts()
    {
        if (_gaming()) throw new InvalidOperationException("Save backups are unavailable during Gaming.");
        string? target = MirrorTarget();
        if (target is null) return new GameSaveSyncConflictsDto(Array.Empty<GameSaveSyncConflictDto>(), Array.Empty<GameSaveSyncDecisionDto>());
        CheckLocalDirectory(_root);
        List<StoredDecision> decisions = ReadDecisions();
        var conflicts = Detect(List(), ReadMirror(target)).Select(d =>
        {
            StoredDecision? decided = decisions.FirstOrDefault(x => Key(x.Label) == Key(d.Label) && x.Fingerprint == d.Fingerprint);
            return new GameSaveSyncConflictDto(d.Label, Side(d.LocalOnly[0]), Side(d.MirrorOnly[0]), d.LocalOnly.Count, d.MirrorOnly.Count, d.CommonCount, decided?.Choice);
        }).ToList();
        return new GameSaveSyncConflictsDto(conflicts,
            decisions.Select(x => new GameSaveSyncDecisionDto(x.Label, x.Choice, x.PreferredId, x.DecidedUtc)).ToList());
    }

    /// <summary>Records the owner's choice for one diverged game. Throws <see cref="InvalidDataException"/> for a bad choice or a game that is not diverged.</summary>
    public GameSaveSyncDecisionDto ResolveConflict(string label, string choice)
    {
        if (_gaming()) throw new InvalidOperationException("Save backups are unavailable during Gaming.");
        if (!GameSaveConflictChoices.IsValid(choice)) throw new InvalidDataException("The choice must be keep-local, keep-mirror or keep-both.");
        string target = MirrorTarget() ?? throw new InvalidDataException("Choose a sync folder first.");
        Divergence? divergence = Detect(List(), ReadMirror(target)).FirstOrDefault(d => Key(d.Label) == Key(label));
        if (divergence is null) throw new InvalidDataException("That game has no conflict to resolve.");

        string? preferred = choice == GameSaveConflictChoices.KeepLocal ? divergence.LocalOnly[0].Id
            : choice == GameSaveConflictChoices.KeepMirror ? divergence.MirrorOnly[0].Id : null;
        var stored = new StoredDecision(divergence.Label, choice, preferred, divergence.Fingerprint, DateTime.UtcNow);
        List<StoredDecision> all = ReadDecisions();
        all.RemoveAll(x => Key(x.Label) == Key(divergence.Label));
        all.Add(stored);
        WriteDecisions(all);
        return new GameSaveSyncDecisionDto(stored.Label, stored.Choice, stored.PreferredId, stored.DecidedUtc);
    }

    /// <summary>The labels (normalized) whose ids must be held back this run: diverged and not decided for this exact divergence.</summary>
    private (HashSet<string> HeldIds, List<string> HeldLabels) UndecidedConflicts(string target)
    {
        List<StoredDecision> decisions = ReadDecisions();
        var held = new HashSet<string>(StringComparer.Ordinal);
        var labels = new List<string>();
        foreach (Divergence d in Detect(List(), ReadMirror(target)))
        {
            if (decisions.Any(x => Key(x.Label) == Key(d.Label) && x.Fingerprint == d.Fingerprint)) continue;
            foreach (GameSaveBackupDto b in d.LocalOnly.Concat(d.MirrorOnly)) held.Add(b.Id);
            labels.Add(d.Label);
        }
        return (held, labels);
    }
}
