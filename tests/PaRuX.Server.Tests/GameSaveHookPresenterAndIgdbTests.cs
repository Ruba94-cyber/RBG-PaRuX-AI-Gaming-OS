using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSaveHookPresenterTests
{
    private static GameSaveRestorePlanDto Plan(params (string Name, string Action)[] items) =>
        new("id", true, "ok", items.Select(i => new GameSaveRestoreItemDto(i.Name, 10, i.Action)).ToList());

    [Fact]
    public void Confirmation_states_the_counts_and_the_safety_guarantees()
    {
        var plan = Plan(("a.sav", "create"), ("b.sav", "overwrite"), ("c.sav", "overwrite"), ("d.sav", "unchanged"));

        string text = GameSaveHookPresenter.ConfirmationText(plan, false);

        Assert.Contains("1 new files, 2 replaced, 1 already identical", text);
        Assert.Contains("automatically saves a copy", text);
        Assert.Contains("never deleted", text);
        Assert.Contains("SHA-256", text);
        Assert.Contains("+ a.sav", text);
        Assert.Contains("~ b.sav", text);
        Assert.DoesNotContain("d.sav", text);   // unchanged files are not listed
    }

    [Fact]
    public void Confirmation_preview_is_bounded()
    {
        var plan = Plan(Enumerable.Range(0, 30).Select(i => ($"f{i}.sav", "create")).ToArray());

        string text = GameSaveHookPresenter.ConfirmationText(plan, false);

        Assert.Equal(GameSaveHookPresenter.PreviewNames, text.Split('\n').Count(l => l.StartsWith("+ ", StringComparison.Ordinal)));
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Restore_outcome_never_claims_success_for_a_failed_restore()
    {
        var failed = new GameSaveRestoreResultDto("id", false, "hash mismatch", 0, null);
        var ok = new GameSaveRestoreResultDto("id", true, "done", 3, "pre-1");
        var okEmpty = new GameSaveRestoreResultDto("id", true, "done", 3, null);

        Assert.StartsWith("Restore failed: hash mismatch", GameSaveHookPresenter.RestoreOutcome(failed, false));
        Assert.DoesNotContain("Restored", GameSaveHookPresenter.RestoreOutcome(failed, false));
        Assert.Contains("Restored 3 files and verified them", GameSaveHookPresenter.RestoreOutcome(ok, false));
        Assert.Contains("copy of the previous files", GameSaveHookPresenter.RestoreOutcome(ok, false));
        Assert.Contains("no pre-restore copy was needed", GameSaveHookPresenter.RestoreOutcome(okEmpty, false));
        Assert.Contains("Ripristinati 3 file", GameSaveHookPresenter.RestoreOutcome(ok, true));
    }

    [Fact]
    public void Per_game_backups_are_matched_by_label()
    {
        var mine = new GameSaveBackupDto("1", "My Game", 2, 20, DateTime.UtcNow, "x");
        var other = new GameSaveBackupDto("2", "Other", 2, 20, DateTime.UtcNow, "x");

        Assert.True(GameSaveHookPresenter.BelongsToGame(mine, "my game"));
        Assert.False(GameSaveHookPresenter.BelongsToGame(other, "My Game"));
        Assert.Equal("Game", GameSaveHookPresenter.BackupLabel("  \u0001 "));
        Assert.Equal(100, GameSaveHookPresenter.BackupLabel(new string('x', 300)).Length);
    }

    [Fact]
    public void Hook_form_empty_path_means_unset_without_an_error()
    {
        Assert.Equal((null, null), GameSaveHookPresenter.ParseHookForm("  ", "x", "99", false));
    }

    [Theory]
    [InlineData("C:\\tools\\run.bat")]
    [InlineData("C:\\tools\\run.ps1")]
    [InlineData("C:\\tools\\run.lnk")]
    [InlineData("C:\\tools\\run")]
    public void Hook_form_refuses_anything_but_an_exe(string path)
    {
        var (spec, error) = GameSaveHookPresenter.ParseHookForm(path, "", "15", false);
        Assert.Null(spec);
        Assert.Contains(".exe", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("abc")]
    [InlineData("-5")]
    public void Hook_form_refuses_a_timeout_outside_1_to_60(string timeout)
    {
        var (spec, error) = GameSaveHookPresenter.ParseHookForm("C:\\tools\\run.exe", "", timeout, false);
        Assert.Null(spec);
        Assert.NotNull(error);
    }

    [Fact]
    public void Hook_form_accepts_a_valid_exe_and_trims_quotes()
    {
        var (spec, error) = GameSaveHookPresenter.ParseHookForm("\"C:\\tools\\run.exe\"", "--quiet", "30", false);
        Assert.Null(error);
        Assert.Equal(new GameHookSpec("C:\\tools\\run.exe", "--quiet", 30), spec);
        Assert.Equal(15, GameSaveHookPresenter.ParseHookForm("C:\\tools\\run.exe", "", "", false).Spec!.TimeoutSeconds);
    }

    [Fact]
    public void Hook_form_refuses_control_characters_and_long_arguments()
    {
        Assert.NotNull(GameSaveHookPresenter.ParseHookForm("C:\\a.exe", "a\nb", "5", false).Error);
        Assert.NotNull(GameSaveHookPresenter.ParseHookForm("C:\\a.exe", new string('x', 1025), "5", false).Error);
    }

    [Fact]
    public void Hook_confirmation_names_the_exact_program_and_the_limits()
    {
        var text = GameSaveHookPresenter.HookConfirmationText(
            new GameHooksDto(new GameHookSpec("C:\\t\\pre.exe", "-a", 10), new GameHookSpec("C:\\t\\post.exe", "", 20)), false);

        Assert.Contains("C:\\t\\pre.exe -a (10 s)", text);
        Assert.Contains("C:\\t\\post.exe (20 s)", text);
        Assert.Contains("never as administrator", text);
        Assert.Contains("never starts while Gaming is active", text);
    }
}
