using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingSteeringAndFileRewindTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-steer-rewind-" + Guid.NewGuid().ToString("N"));
    private readonly CodingTaskChangeTracker _tracker;

    public CodingSteeringAndFileRewindTests()
    {
        Directory.CreateDirectory(_root);
        _tracker = new CodingTaskChangeTracker(Path.Combine(_root, ".state"), null);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string P(string rel) => Path.Combine(_root, rel);

    private void AgentWrite(string taskId, string rel, string content)
    {
        _tracker.BeforeWrite(taskId, _root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
        _tracker.AfterWrite(taskId, _root, rel);
    }

    // ---- steering ----

    [Fact]
    public void An_instruction_sent_mid_run_is_delivered_at_the_next_step_boundary_in_order()
    {
        string session = "steer-" + Guid.NewGuid().ToString("N");
        using IDisposable run = CodingAgentSteering.Activate(session);
        var log = new List<string>();
        var steps = new List<string>();

        // Scripted runtime stub: records exactly what the "model" would see at the start of each step.
        var seenByModel = new List<string[]>();
        for (int step = 1; step <= 3; step++)
        {
            CodingAgentSteering.DeliverAtStepBoundary(session, step, log, steps.Add);
            seenByModel.Add(log.ToArray());
            log.Add("step " + step + " result");
            if (step == 1)
            {
                Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "use tabs, not spaces"));
                Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "and skip the docs"));
            }
        }

        Assert.DoesNotContain(seenByModel[0], l => l.Contains("NEW USER INSTRUCTION"));
        string[] delivered = seenByModel[1].Where(l => l.Contains("NEW USER INSTRUCTION")).ToArray();
        Assert.Equal(2, delivered.Length);
        Assert.Contains("use tabs, not spaces", delivered[0]);
        Assert.Contains("skip the docs", delivered[1]);
        Assert.Contains("before step 2", delivered[0]);
        Assert.Equal(2, steps.Count(s => s.Contains("delivered a new user instruction")));
        Assert.Equal(0, CodingAgentSteering.PendingCount(session));
        // Delivered once only.
        Assert.Equal(2, seenByModel[2].Count(l => l.Contains("NEW USER INSTRUCTION")));
    }

    [Fact]
    public void Steering_a_run_that_is_not_live_is_refused_and_nothing_is_kept_for_a_later_run()
    {
        string session = "steer-" + Guid.NewGuid().ToString("N");
        Assert.Equal(CodingSteeringOutcome.NoActiveRun, CodingAgentSteering.Submit(session, "hello"));
        Assert.Equal(CodingSteeringOutcome.NoActiveRun, CodingAgentSteering.Submit(null, "hello"));
        using (CodingAgentSteering.Activate(session))
            Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "late"));
        Assert.False(CodingAgentSteering.IsActive(session));
        using (CodingAgentSteering.Activate(session))
            Assert.Equal(0, CodingAgentSteering.PendingCount(session));   // the earlier undelivered text did not leak
    }

    [Fact]
    public void Steering_is_bounded()
    {
        string session = "steer-" + Guid.NewGuid().ToString("N");
        using IDisposable run = CodingAgentSteering.Activate(session);
        Assert.Equal(CodingSteeringOutcome.Empty, CodingAgentSteering.Submit(session, "   "));
        Assert.Equal(CodingSteeringOutcome.TooLong, CodingAgentSteering.Submit(session, new string('x', CodingAgentSteering.MaxCharacters + 1)));
        for (int i = 0; i < CodingAgentSteering.MaxPending; i++)
            Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "i" + i));
        Assert.Equal(CodingSteeringOutcome.QueueFull, CodingAgentSteering.Submit(session, "one too many"));
        var log = new List<string>();
        Assert.Equal(CodingAgentSteering.MaxPending, CodingAgentSteering.DeliverAtStepBoundary(session, 1, log, null));
        Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "room again"));
    }

    [Fact]
    public void A_second_run_with_the_same_id_keeps_its_own_queue_when_the_first_run_ends()
    {
        string session = "steer-" + Guid.NewGuid().ToString("N");
        IDisposable first = CodingAgentSteering.Activate(session);
        using IDisposable second = CodingAgentSteering.Activate(session);
        first.Dispose();
        Assert.True(CodingAgentSteering.IsActive(session));
        Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "still works"));
    }

    // ---- file rewind ----

    private static Conversation Chat(params (string Role, string? Task)[] messages)
    {
        var c = new Conversation { IsIncognito = true };
        foreach ((string role, string? task) in messages)
            c.Messages.Add(new ChatMessage { Role = role, Content = role + "-" + c.Messages.Count, CodeTaskId = task });
        return c;
    }

    [Fact]
    public void Rewinding_to_a_message_restores_only_the_agent_files_changed_after_it_and_leaves_user_edits_alone()
    {
        File.WriteAllText(P("a.txt"), "orig");
        File.WriteAllText(P("user.txt"), "mine");
        AgentWrite("task1", "a.txt", "v1");
        AgentWrite("task1", "one.txt", "one");
        AgentWrite("task2", "a.txt", "v2");
        AgentWrite("task2", "two.txt", "two");
        File.WriteAllText(P("user.txt"), "mine edited");

        Conversation chat = Chat(("user", null), ("assistant", "task1"), ("user", null), ("assistant", "task2"));
        RewindResult result = ConversationRewind.Rewind(chat, 1, _ => true);
        Assert.Equal(RewindStatus.Rewound, result.Status);
        Assert.Equal(new[] { "task2" }, result.RemovedCodeTaskIds);

        CodingFileRewindResult files = _tracker.RewindFiles(result.RemovedCodeTaskIds);

        Assert.Equal("v1", File.ReadAllText(P("a.txt")));
        Assert.False(File.Exists(P("two.txt")));
        Assert.Equal("one", File.ReadAllText(P("one.txt")));      // task1 is before the chosen message: untouched
        Assert.Equal("mine edited", File.ReadAllText(P("user.txt")));
        Assert.Equal(2, files.RestoredFiles);
        Assert.Equal(0, files.SkippedFiles);
        Assert.True(files.CanUndo);
    }

    [Fact]
    public void Rewinding_across_several_tasks_peels_them_back_newest_first()
    {
        File.WriteAllText(P("a.txt"), "orig");
        AgentWrite("task1", "a.txt", "v1");
        AgentWrite("task2", "a.txt", "v2");
        Conversation chat = Chat(("user", null), ("assistant", "task1"), ("user", null), ("assistant", "task2"));
        RewindResult result = ConversationRewind.Rewind(chat, 0, _ => true);
        Assert.Equal(new[] { "task2", "task1" }, result.RemovedCodeTaskIds);

        _tracker.RewindFiles(result.RemovedCodeTaskIds);

        Assert.Equal("orig", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void A_file_the_user_edited_after_the_agent_is_never_overwritten_by_a_rewind()
    {
        File.WriteAllText(P("a.txt"), "orig");
        AgentWrite("task1", "a.txt", "agent");
        File.WriteAllText(P("a.txt"), "agent plus my edit");

        CodingFileRewindResult files = _tracker.RewindFiles(new[] { "task1" });

        Assert.Equal("agent plus my edit", File.ReadAllText(P("a.txt")));
        Assert.Equal(1, files.SkippedFiles);
        Assert.Contains(files.Tasks[0].Items, i => i.Outcome == CodingRevertOutcome.SkippedModifiedByUser);
    }

    [Fact]
    public void A_task_without_a_snapshot_is_reported_not_guessed()
    {
        File.WriteAllText(P("a.txt"), "orig");
        CodingFileRewindResult files = _tracker.RewindFiles(new[] { "unknown-task" });
        Assert.Equal(1, files.TasksWithoutSnapshot);
        Assert.Equal(0, files.RestoredFiles);
        Assert.False(files.CanUndo);
        Assert.Equal("orig", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void File_rewind_is_undoable_and_undo_skips_files_the_user_changed_since()
    {
        File.WriteAllText(P("a.txt"), "orig");
        AgentWrite("task1", "a.txt", "agent-a");
        AgentWrite("task1", "new.txt", "agent-new");

        CodingFileRewindResult files = _tracker.RewindFiles(new[] { "task1" });
        Assert.Equal("orig", File.ReadAllText(P("a.txt")));
        Assert.False(File.Exists(P("new.txt")));

        File.WriteAllText(P("new.txt"), "user created this after the rewind");
        CodingFileRewindResult undone = _tracker.UndoFileRewind(files);

        Assert.Equal("agent-a", File.ReadAllText(P("a.txt")));
        Assert.Equal("user created this after the rewind", File.ReadAllText(P("new.txt")));   // not replaced
        Assert.Equal(1, undone.RestoredFiles);
        Assert.Equal(1, undone.SkippedFiles);
    }

    [Fact]
    public void A_file_the_agent_deleted_comes_back_on_rewind_and_goes_again_on_undo()
    {
        File.WriteAllText(P("gone.txt"), "keep me");
        _tracker.BeforeWrite("task1", _root, "gone.txt");
        File.Delete(P("gone.txt"));
        _tracker.AfterWrite("task1", _root, "gone.txt");

        CodingFileRewindResult files = _tracker.RewindFiles(new[] { "task1" });
        Assert.Equal("keep me", File.ReadAllText(P("gone.txt")));
        _tracker.UndoFileRewind(files);
        Assert.False(File.Exists(P("gone.txt")));
    }

    [Fact]
    public void Paths_outside_the_workspace_are_never_touched_by_a_rewind()
    {
        string outside = Path.Combine(Path.GetTempPath(), "parux-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "outside");
        try
        {
            _tracker.BeforeWrite("task1", _root, "..\\" + Path.GetFileName(outside));
            _tracker.AfterWrite("task1", _root, "..\\" + Path.GetFileName(outside));
            _tracker.RewindFiles(new[] { "task1" });
            Assert.Equal("outside", File.ReadAllText(outside));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Conversation_rewind_lists_removed_coding_task_ids_newest_first_without_duplicates()
    {
        Conversation chat = Chat(("user", null), ("assistant", "t1"), ("assistant", "t2"), ("assistant", "t2"), ("assistant", null));
        RewindResult result = ConversationRewind.Rewind(chat, 0, _ => true);
        Assert.Equal(new[] { "t2", "t1" }, result.RemovedCodeTaskIds);
        Assert.Equal(3, result.CodingMessagesRemoved);
    }

    // ---- observable & cancellable steering ----

    [Fact]
    public void Steering_queue_is_observable_and_cancellable_before_the_next_step()
    {
        string session = "steer-obs-" + Guid.NewGuid().ToString("N");
        int notifications = 0;
        void OnChanged(string id)
        {
            if (id == session) Interlocked.Increment(ref notifications);
        }
        void ThrowingObserver(string id)
        {
            if (id == session) throw new InvalidOperationException("observer failure must not break loop");
        }

        CodingAgentSteering.StateChanged += OnChanged;
        CodingAgentSteering.StateChanged += ThrowingObserver;
        try
        {
            using (CodingAgentSteering.Activate(session))
            {
                Assert.Empty(CodingAgentSteering.PeekPending(session));
                Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "first guidance"));
                Assert.Equal(CodingSteeringOutcome.Accepted, CodingAgentSteering.Submit(session, "second guidance"));

                IReadOnlyList<string> pending = CodingAgentSteering.PeekPending(session);
                Assert.Equal(new[] { "first guidance", "second guidance" }, pending);

                int cleared = CodingAgentSteering.ClearPending(session);
                Assert.Equal(2, cleared);
                Assert.Empty(CodingAgentSteering.PeekPending(session));
                Assert.Equal(0, CodingAgentSteering.PendingCount(session));

                var log = new List<string>();
                Assert.Equal(0, CodingAgentSteering.DeliverAtStepBoundary(session, 2, log, null));
                Assert.Empty(log);
            }
            Assert.True(notifications >= 4); // activate + 2 submits + clear + dispose
        }
        finally
        {
            CodingAgentSteering.StateChanged -= OnChanged;
            CodingAgentSteering.StateChanged -= ThrowingObserver;
        }
    }

    [Fact]
    public async Task Steering_is_thread_safe_under_concurrent_submit_peek_and_deliver()
    {
        string session = "steer-conc-" + Guid.NewGuid().ToString("N");
        using IDisposable run = CodingAgentSteering.Activate(session);
        var deliveredLog = new List<string>();
        int accepted = 0;
        int delivered = 0;

        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                if (CodingAgentSteering.Submit(session, "msg-" + i) == CodingSteeringOutcome.Accepted)
                    Interlocked.Increment(ref accepted);
                _ = CodingAgentSteering.PeekPending(session);
            }
        });

        Task consumer = Task.Run(() =>
        {
            for (int step = 1; step <= 60; step++)
            {
                delivered += CodingAgentSteering.DeliverAtStepBoundary(session, step, deliveredLog, null);
                Thread.Yield();
            }
        });

        await Task.WhenAll(producer, consumer);
        delivered += CodingAgentSteering.DeliverAtStepBoundary(session, 61, deliveredLog, null);

        Assert.Equal(accepted, delivered);
        Assert.Equal(accepted, deliveredLog.Count);
        Assert.Equal(0, CodingAgentSteering.PendingCount(session));
    }

    [Fact]
    public void Recovery_digest_preserves_steered_user_instructions()
    {
        var checkpoint = new CodingAgentCheckpoint { SessionId = "s1", WorkspaceRoot = _root, StepsTaken = 3 };
        var observations = new List<string>
        {
            "read_file ok",
            "NEW USER INSTRUCTION (sent while this task was running; delivered before step 3). Acknowledge it: switch to tabs",
            "list_dir ok"
        };

        string digest = CodingAgentRecoveryDigest.Build(checkpoint, observations, 1_200);
        Assert.Contains("switch to tabs", digest, StringComparison.Ordinal);
    }

    // ---- durable applied-worktree file rewind & undo ----

    private (string Repo, string Worktree, string UndoState) CreateGitFixture(string name)
    {
        string baseDir = Path.Combine(_root, name);
        string repo = Path.Combine(baseDir, "repo");
        string worktree = Path.Combine(baseDir, "worktree");
        string undoState = Path.Combine(baseDir, "undo-state");
        Directory.CreateDirectory(repo);
        Git(repo, "init");
        Git(repo, "config", "user.name", "PaRuX Tests");
        Git(repo, "config", "user.email", "tests@parux.invalid");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "orig\n");
        File.WriteAllText(Path.Combine(repo, "user.txt"), "mine\n");
        Git(repo, "add", "a.txt", "user.txt");
        Git(repo, "commit", "-m", "baseline");
        Git(repo, "worktree", "add", "-b", "parux-" + name, worktree, "HEAD");
        return (repo, worktree, undoState);
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', args) + " failed: " + error);
        return output;
    }

    private static string ReadLf(string path) => File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public async Task Rewinding_to_message_N_reverses_applied_worktree_patches_after_N_even_after_restart_and_supports_undo()
    {
        (string repo, string worktree, string undoState) = CreateGitFixture("apply-rewind");

        // Task 1: modify a.txt -> v1, create one.txt, apply to repo with taskId "task1"
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "v1\n");
        File.WriteAllText(Path.Combine(worktree, "one.txt"), "one\n");
        CodeApplyResult apply1 = await CodeWorktreeApplyService.ApplyAsync(
            repo, worktree, CancellationToken.None, undoStateDirectory: undoState, taskId: "task1");
        Assert.True(apply1.Success, apply1.Message);

        // Task 2: create a second worktree from current repo state to simulate a subsequent task modifying a.txt -> v2 and creating two.txt
        string worktree2 = Path.Combine(_root, "apply-rewind", "worktree2");
        Git(repo, "worktree", "add", "-b", "parux-apply-rewind-2", worktree2, "HEAD");
        // Task 2 patch builds on top of v1 in repo: wait - git diff HEAD in worktree2 is relative to HEAD, so let's make task2 touch two.txt and b.txt
        File.WriteAllText(Path.Combine(worktree2, "two.txt"), "two\n");
        File.WriteAllText(Path.Combine(worktree2, "three.txt"), "three\n");
        CodeApplyResult apply2 = await CodeWorktreeApplyService.ApplyAsync(
            repo, worktree2, CancellationToken.None, undoStateDirectory: undoState, taskId: "task2");
        Assert.True(apply2.Success, apply2.Message);

        // Unrelated user edit in repo
        File.WriteAllText(Path.Combine(repo, "user.txt"), "mine edited\n");

        // Rewind chat to message 1 (keeps task1, removes task2) using a FRESH tracker (simulating app restart)
        Conversation chat = Chat(("user", null), ("assistant", "task1"), ("user", null), ("assistant", "task2"));
        RewindResult chatRewind = ConversationRewind.Rewind(chat, 1, _ => true);
        Assert.Equal(RewindStatus.Rewound, chatRewind.Status);
        Assert.Equal(new[] { "task2" }, chatRewind.RemovedCodeTaskIds);

        var restartedTracker = new CodingTaskChangeTracker(Path.Combine(_root, ".state-restarted"), null);
        CodingFileRewindResult files = await restartedTracker.RewindFilesAsync(
            chatRewind.RemovedCodeTaskIds, CancellationToken.None, applyUndoStateDirectory: undoState);

        Assert.Equal(0, files.TasksWithoutSnapshot);
        Assert.Equal(2, files.RestoredFiles);
        Assert.Equal(0, files.SkippedFiles);
        Assert.True(files.CanUndo);
        Assert.False(File.Exists(Path.Combine(repo, "two.txt")));
        Assert.False(File.Exists(Path.Combine(repo, "three.txt")));
        Assert.Equal("v1\n", ReadLf(Path.Combine(repo, "a.txt")));            // task1 kept
        Assert.Equal("one\n", ReadLf(Path.Combine(repo, "one.txt")));         // task1 kept
        Assert.Equal("mine edited\n", ReadLf(Path.Combine(repo, "user.txt"))); // user edit kept

        // Undo the file rewind -> task2 files come back and user edit remains untouched
        CodingFileRewindResult undone = await restartedTracker.UndoFileRewindAsync(files, CancellationToken.None);
        Assert.Equal(2, undone.RestoredFiles);
        Assert.Equal(0, undone.SkippedFiles);
        Assert.Equal("two\n", ReadLf(Path.Combine(repo, "two.txt")));
        Assert.Equal("three\n", ReadLf(Path.Combine(repo, "three.txt")));
        Assert.Equal("mine edited\n", ReadLf(Path.Combine(repo, "user.txt")));
        Assert.NotEmpty(CodeWorktreeApplyService.UndoIdsForTask("task2", undoState));
    }

    [Fact]
    public async Task Applied_patch_rewind_refuses_honestly_when_user_edited_applied_file_after_apply()
    {
        (string repo, string worktree, string undoState) = CreateGitFixture("apply-refuse");

        File.WriteAllText(Path.Combine(worktree, "a.txt"), "agent-v1\n");
        CodeApplyResult applied = await CodeWorktreeApplyService.ApplyAsync(
            repo, worktree, CancellationToken.None, undoStateDirectory: undoState, taskId: "task1");
        Assert.True(applied.Success, applied.Message);

        // User modifies the applied file afterwards
        File.WriteAllText(Path.Combine(repo, "a.txt"), "agent-v1 + user edit\n");

        var tracker = new CodingTaskChangeTracker(Path.Combine(_root, ".state-refuse"), null);
        CodingFileRewindResult files = await tracker.RewindFilesAsync(
            new[] { "task1" }, CancellationToken.None, applyUndoStateDirectory: undoState);

        Assert.Equal("agent-v1 + user edit\n", ReadLf(Path.Combine(repo, "a.txt")));
        Assert.Equal(0, files.RestoredFiles);
        Assert.True(files.SkippedFiles >= 1);
        Assert.NotEmpty(files.AppliedPatchRefusals);
        Assert.False(files.CanUndo);
    }

    [Fact]
    public async Task Applied_patch_rewind_refuses_honestly_when_user_committed_applied_changes()
    {
        (string repo, string worktree, string undoState) = CreateGitFixture("apply-head");

        File.WriteAllText(Path.Combine(worktree, "a.txt"), "agent-committed\n");
        CodeApplyResult applied = await CodeWorktreeApplyService.ApplyAsync(
            repo, worktree, CancellationToken.None, undoStateDirectory: undoState, taskId: "task1");
        Assert.True(applied.Success, applied.Message);

        Git(repo, "add", "a.txt");
        Git(repo, "commit", "-m", "user committed agent changes");

        var tracker = new CodingTaskChangeTracker(Path.Combine(_root, ".state-head"), null);
        CodingFileRewindResult files = await tracker.RewindFilesAsync(
            new[] { "task1" }, CancellationToken.None, applyUndoStateDirectory: undoState);

        Assert.Equal("agent-committed\n", ReadLf(Path.Combine(repo, "a.txt")));
        Assert.Equal(0, files.RestoredFiles);
        Assert.True(files.SkippedFiles >= 1);
        Assert.Contains(files.AppliedPatchRefusals, m => m.Contains("different commit", StringComparison.OrdinalIgnoreCase));
        Assert.False(files.CanUndo);
    }

    [Fact]
    public async Task Applied_patch_rewind_undo_refuses_honestly_when_user_edited_file_after_rewind()
    {
        (string repo, string worktree, string undoState) = CreateGitFixture("apply-undo-refuse");

        File.WriteAllText(Path.Combine(worktree, "a.txt"), "agent-v1\n");
        CodeApplyResult applied = await CodeWorktreeApplyService.ApplyAsync(
            repo, worktree, CancellationToken.None, undoStateDirectory: undoState, taskId: "task1");
        Assert.True(applied.Success, applied.Message);

        var tracker = new CodingTaskChangeTracker(Path.Combine(_root, ".state-undo-refuse"), null);
        CodingFileRewindResult files = await tracker.RewindFilesAsync(
            new[] { "task1" }, CancellationToken.None, applyUndoStateDirectory: undoState);
        Assert.Equal("orig\n", ReadLf(Path.Combine(repo, "a.txt")));
        Assert.True(files.CanUndo);

        // User edits a.txt after the rewind
        File.WriteAllText(Path.Combine(repo, "a.txt"), "user wrote this after rewind\n");

        CodingFileRewindResult undone = await tracker.UndoFileRewindAsync(files, CancellationToken.None);
        Assert.Equal("user wrote this after rewind\n", ReadLf(Path.Combine(repo, "a.txt")));
        Assert.Equal(0, undone.RestoredFiles);
        Assert.True(undone.SkippedFiles >= 1);
        Assert.NotEmpty(undone.AppliedPatchRefusals);
    }
}
