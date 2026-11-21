using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Two PCs played the same game without syncing: the histories forked. Nothing is merged silently and nothing is ever deleted.</summary>
public sealed class GameSaveSyncConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-save-conflict-" + Guid.NewGuid().ToString("N"));
    private bool _gaming;

    public GameSaveSyncConflictTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private GameSaveBackupService Pc(string name)
    {
        string home = Path.Combine(_root, name);
        Directory.CreateDirectory(home);
        return new GameSaveBackupService(Path.Combine(home, "backups"), () => _gaming, () => CancellationToken.None);
    }

    private async Task<string> Backup(GameSaveBackupService pc, string label, string content)
    {
        string source = Path.Combine(_root, "saves-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "slot.dat"), content);
        await Task.Delay(25);   // distinct creation times, in order
        return (await pc.CreateAsync(new GameSaveBackupRequest(source, label), CancellationToken.None)).Id;
    }

    private string MirrorFolder()
    {
        string mirror = Path.Combine(_root, "cloud");
        Directory.CreateDirectory(mirror);
        return mirror;
    }

    private static string[] MirrorIds(string mirror) =>
        Directory.GetFiles(Path.Combine(mirror, "PaRuX-save-backups"), "save-*.zip").Select(f => Path.GetFileNameWithoutExtension(f)[5..]).OrderBy(x => x).ToArray();

    /// <summary>A and B share a base backup, then each makes a new one; A syncs first, so B finds the fork.</summary>
    private async Task<(GameSaveBackupService A, GameSaveBackupService B, string Mirror, string Base, string A1, string B1)> Fork(string labelA = "Hades", string labelB = "Hades")
    {
        var a = Pc("A"); var b = Pc("B");
        string mirror = MirrorFolder();
        a.SetSyncFolder(mirror); b.SetSyncFolder(mirror);
        string baseId = await Backup(a, labelA, "base");
        await a.SyncAsync(CancellationToken.None);
        await b.SyncAsync(CancellationToken.None);
        string a1 = await Backup(a, labelA, "played on A");
        string b1 = await Backup(b, labelB, "played on B");
        await a.SyncAsync(CancellationToken.None);   // the mirror gets A1 only
        return (a, b, mirror, baseId, a1, b1);
    }

    [Fact]
    public async Task A_forked_history_is_held_back_not_merged_and_nothing_moves()
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork();
        string[] mirrorBefore = MirrorIds(mirror);

        GameSaveSyncResultDto result = await b.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { "Hades" }, result.Conflicts);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Pulled);
        Assert.Equal(mirrorBefore, MirrorIds(mirror));                         // B1 was not pushed
        Assert.DoesNotContain(b.List(), x => x.Id == a1);                      // A1 was not pulled
        Assert.Contains(b.List(), x => x.Id == b1);
        Assert.Contains(b.List(), x => x.Id == baseId);
    }

    [Fact]
    public async Task The_preview_shows_both_sides_metadata_side_by_side()
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork();

        GameSaveSyncConflictsDto preview = b.SyncConflicts();

        GameSaveSyncConflictDto conflict = Assert.Single(preview.Conflicts);
        Assert.Equal("Hades", conflict.Label);
        Assert.Equal(b1, conflict.Local.Id);
        Assert.Equal(a1, conflict.Mirror.Id);
        Assert.Equal(1, conflict.Local.Files);
        Assert.True(conflict.Local.Bytes > 0 && conflict.Mirror.Bytes > 0);
        Assert.True(conflict.Mirror.CreatedUtc < conflict.Local.CreatedUtc);   // A1 was made before B1 in this run
        Assert.Equal(1, conflict.LocalOnlyCount);
        Assert.Equal(1, conflict.MirrorOnlyCount);
        Assert.Equal(1, conflict.CommonCount);
        Assert.Null(conflict.Decision);
        Assert.Empty(preview.Decisions);
        _ = (a, mirror, baseId);
    }

    [Theory]
    [InlineData("keep-local")]
    [InlineData("keep-mirror")]
    [InlineData("keep-both")]
    public async Task Every_choice_keeps_every_backup_on_both_sides_and_deletes_nothing(string choice)
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork();

        GameSaveSyncDecisionDto decision = b.ResolveConflict("Hades", choice);
        Assert.Equal(choice, decision.Choice);
        Assert.Equal(choice == "keep-local" ? b1 : choice == "keep-mirror" ? a1 : null, decision.PreferredId);

        GameSaveSyncResultDto result = await b.SyncAsync(CancellationToken.None);
        await a.SyncAsync(CancellationToken.None);

        Assert.Empty(result.Conflicts);
        string[] all = new[] { baseId, a1, b1 }.OrderBy(x => x).ToArray();
        Assert.Equal(all, MirrorIds(mirror));
        Assert.Equal(all, b.List().Select(x => x.Id).OrderBy(x => x).ToArray());
        Assert.Equal(all, a.List().Select(x => x.Id).OrderBy(x => x).ToArray());
        foreach (string id in new[] { a1, b1, baseId })
        {
            Assert.True((await b.VerifyAsync(id, CancellationToken.None)).Valid);
            Assert.True((await a.VerifyAsync(id, CancellationToken.None)).Valid);
        }
        Assert.Empty(b.SyncConflicts().Conflicts);                               // resolved once both histories are on both sides
        Assert.Equal(choice, Assert.Single(b.SyncConflicts().Decisions).Choice);  // the choice stays on record
    }

    [Fact]
    public async Task A_decision_does_not_cover_a_later_fork()
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork();
        b.ResolveConflict("Hades", "keep-local");
        await b.SyncAsync(CancellationToken.None);
        await a.SyncAsync(CancellationToken.None);

        string a2 = await Backup(a, "Hades", "A again");
        string b2 = await Backup(b, "Hades", "B again");
        await a.SyncAsync(CancellationToken.None);
        GameSaveSyncResultDto result = await b.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { "Hades" }, result.Conflicts);                       // asked again
        Assert.Null(Assert.Single(b.SyncConflicts().Conflicts).Decision);
        Assert.DoesNotContain(b.List(), x => x.Id == a2);
        Assert.Contains(b.List(), x => x.Id == b2);
        _ = (mirror, baseId, a1, b1);
    }

    [Fact]
    public async Task One_sided_progress_is_a_plain_fast_forward_and_other_games_sync_while_one_is_held()
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork();
        string other = await Backup(b, "Celeste", "unrelated");

        GameSaveSyncResultDto result = await b.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { "Hades" }, result.Conflicts);
        Assert.Equal(1, result.Pushed);                                          // Celeste went through
        Assert.Contains(other, MirrorIds(mirror));
        Assert.DoesNotContain(b1, MirrorIds(mirror));

        // A game only one side progressed in is not a conflict.
        string c1 = await Backup(a, "Tunic", "only on A");
        await a.SyncAsync(CancellationToken.None);
        GameSaveSyncResultDto next = await b.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { "Hades" }, next.Conflicts);
        Assert.Contains(b.List(), x => x.Id == c1);
        _ = (baseId, a1);
    }

    [Fact]
    public async Task Labels_are_compared_ignoring_case_and_spaces()
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork("Hades", "  hades ");
        Assert.Single(b.SyncConflicts().Conflicts);
        b.ResolveConflict("HADES", "keep-both");
        GameSaveSyncResultDto result = await b.SyncAsync(CancellationToken.None);
        Assert.Empty(result.Conflicts);
        _ = (a, mirror, baseId, a1, b1);
    }

    [Fact]
    public async Task Bad_choices_and_games_without_a_conflict_are_refused_and_record_nothing()
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork();

        Assert.Throws<InvalidDataException>(() => b.ResolveConflict("Hades", "delete-mirror"));
        Assert.Throws<InvalidDataException>(() => b.ResolveConflict("Hades", ""));
        Assert.Throws<InvalidDataException>(() => b.ResolveConflict("Unknown game", "keep-both"));
        Assert.Empty(b.SyncConflicts().Decisions);
        Assert.Equal(new[] { "Hades" }, (await b.SyncAsync(CancellationToken.None)).Conflicts);
        _ = (a, mirror, baseId, a1, b1);
    }

    [Fact]
    public async Task Nothing_is_read_or_decided_while_gaming_is_active_and_without_a_sync_folder()
    {
        var (a, b, mirror, baseId, a1, b1) = await Fork();
        _gaming = true;
        Assert.Throws<InvalidOperationException>(() => b.SyncConflicts());
        Assert.Throws<InvalidOperationException>(() => b.ResolveConflict("Hades", "keep-both"));
        _gaming = false;
        Assert.Empty(b.SyncConflicts().Decisions);

        var lonely = Pc("C");
        Assert.Empty(lonely.SyncConflicts().Conflicts);
        Assert.Throws<InvalidDataException>(() => lonely.ResolveConflict("Hades", "keep-both"));
        _ = (a, mirror, baseId, a1, b1);
        await Task.CompletedTask;
    }

    private static GameSaveBackupDto Dto(string id, string label, int minute) =>
        new(id, label, 1, 10, new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc), new string('a', 64));

    [Fact]
    public void Detection_ignores_old_stragglers_and_needs_a_real_fork()
    {
        var common = Dto("c1", "G", 10);
        // Mirror has an OLDER backup the PC lacks, PC has a newer one: history is a line, not a fork.
        Assert.Empty(GameSaveBackupService.Detect(
            new[] { common, Dto("l2", "G", 30) }, new[] { common, Dto("m0", "G", 5) }));
        // Each side newer than the last common one: a fork.
        Assert.Single(GameSaveBackupService.Detect(
            new[] { common, Dto("l2", "G", 30) }, new[] { common, Dto("m2", "G", 31) }));
        // No common backup at all and both have some: still a fork (two unrelated histories).
        Assert.Single(GameSaveBackupService.Detect(new[] { Dto("l1", "G", 30) }, new[] { Dto("m1", "G", 31) }));
        // Identical sides and one-sided differences are not forks.
        Assert.Empty(GameSaveBackupService.Detect(new[] { common }, new[] { common }));
        Assert.Empty(GameSaveBackupService.Detect(new[] { common, Dto("l2", "G", 30) }, new[] { common }));
        Assert.Empty(GameSaveBackupService.Detect(new[] { common }, new[] { common, Dto("m2", "G", 31) }));
        // Different games never conflict with each other.
        Assert.Empty(GameSaveBackupService.Detect(new[] { Dto("l1", "A", 30) }, new[] { Dto("m1", "B", 31) }));
    }
}
