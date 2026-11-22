using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
}
