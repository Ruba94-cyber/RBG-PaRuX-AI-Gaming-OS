using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Rewinding a chat to a chosen message. The fake history store follows the same rule as the real one: whenever the
/// path being saved is not a strict extension of the previously saved path, the previous path is kept as a retained
/// version. The tests prove that rewinding never loses the longer chat, whatever fails.
/// </summary>
public sealed class ConversationRewindTests
{
    private sealed class FakeHistory
    {
        public Conversation? Stored;
        public int Saves;
        public int FailOnSave = -1;          // 1-based index of the save call that fails (returns false)
        public int ThrowOnSave = -1;
        public bool RetainSupersededPaths = true;

        public bool Save(Conversation conversation)
        {
            Saves++;
            if (Saves == FailOnSave) return false;
            if (Saves == ThrowOnSave) throw new InvalidOperationException("disk exploded");
            if (Stored != null && RetainSupersededPaths)
            {
                int common = 0;
                int limit = Math.Min(Stored.Messages.Count, conversation.Messages.Count);
                while (common < limit && Same(Stored.Messages[common], conversation.Messages[common])) common++;
                if (common != Stored.Messages.Count &&
                    !conversation.Branches.Any(b => b.Messages.Count == Stored.Messages.Count && b.Messages.Zip(Stored.Messages).All(p => Same(p.First, p.Second))))
                {
                    conversation.Branches.Add(new ConversationBranch
                    {
                        ForkIndex = common,
                        Reason = "regenerate-or-edit",
                        Messages = Stored.Messages.Select(ConversationBranching.CloneMessage).ToList()
                    });
                }
            }
            Stored = Clone(conversation);
            return true;
        }

        public bool RestoreBranch(Conversation conversation, string branchId)
        {
            ConversationBranch? branch = conversation.Branches.FirstOrDefault(b => b.Id == branchId);
            if (branch == null) return false;
            conversation.Messages = branch.Messages.Select(ConversationBranching.CloneMessage).ToList();
            return Save(conversation);
        }

        private static bool Same(ChatMessage a, ChatMessage b) =>
            a.Role == b.Role && a.Content == b.Content; // like the real store: ids do not make a new version

        private static Conversation Clone(Conversation c) =>
            System.Text.Json.JsonSerializer.Deserialize<Conversation>(System.Text.Json.JsonSerializer.Serialize(c))!;
    }

    private static Conversation Sample(int turns = 3)
    {
        var c = new Conversation { Title = "Planning" };
        for (int i = 1; i <= turns; i++)
        {
            c.Messages.Add(new ChatMessage { Role = "user", Content = "question " + i });
            c.Messages.Add(new ChatMessage { Role = "assistant", Content = "answer " + i });
        }
        return c;
    }

    private static string[] Contents(Conversation c) => c.Messages.Select(m => m.Content).ToArray();

    [Fact]
    public void Rewind_removes_the_later_messages_and_keeps_the_longer_chat_as_a_retained_version()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        ChatMessage target = chat.Messages[1]; // "answer 1"

        RewindResult result = ConversationRewind.Rewind(chat, target, history.Save);

        Assert.Equal(RewindStatus.Rewound, result.Status);
        Assert.Equal(new[] { "question 1", "answer 1" }, Contents(chat));
        Assert.Equal(2, result.KeptCount);
        Assert.Equal(4, result.RemovedCount);
        Assert.True(result.Retained);
        Assert.NotNull(result.RetainedBranchId);
        ConversationBranch branch = Assert.Single(chat.Branches);
        Assert.Equal(result.RetainedBranchId, branch.Id);
        Assert.Equal(6, branch.Messages.Count);
        Assert.Equal("answer 3", branch.Messages[^1].Content);
        // History holds the shorter chat as the current one.
        Assert.Equal(new[] { "question 1", "answer 1" }, Contents(history.Stored!));
        Assert.Equal(2, history.Saves); // once for the longer chat, once for the shorter
        Assert.False(string.IsNullOrEmpty(target.MessageId));
    }

    [Fact]
    public void Undo_brings_the_whole_chat_back()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.True(ConversationRewind.CanUndo(chat, result));
        Assert.True(ConversationRewind.Undo(chat, result, history.RestoreBranch));

        Assert.Equal(6, chat.Messages.Count);
        Assert.Equal("answer 3", chat.Messages[^1].Content);
        Assert.Equal(6, history.Stored!.Messages.Count);
        Assert.Contains(chat.Branches, b => b.Messages.Count == 6); // the longer path stays reachable as a version
    }

    [Fact]
    public void Chatting_on_after_a_rewind_never_destroys_the_old_path_and_undo_still_works()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        chat.Messages.Add(new ChatMessage { Role = "user", Content = "a different question" });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "a different answer" });
        history.Save(chat);

        Assert.True(ConversationRewind.CanUndo(chat, result));
        Assert.True(ConversationRewind.Undo(chat, result, history.RestoreBranch));
        Assert.Equal("answer 3", chat.Messages[^1].Content);
        // The new line of conversation is retained too: nothing was lost anywhere.
        Assert.Contains(chat.Branches, b => b.Messages.Any(m => m.Content == "a different answer"));
    }

    [Fact]
    public void Rewinding_to_the_last_message_changes_and_saves_nothing()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();

        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[^1], history.Save);

        Assert.Equal(RewindStatus.NothingToRewind, result.Status);
        Assert.Equal(6, chat.Messages.Count);
        Assert.Equal(0, history.Saves);
        Assert.Empty(chat.Branches);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public void An_index_outside_the_chat_is_not_found(int index)
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        Assert.Equal(RewindStatus.NotFound, ConversationRewind.Rewind(chat, index, history.Save).Status);
        Assert.Equal(6, chat.Messages.Count);
        Assert.Equal(0, history.Saves);
    }

    [Fact]
    public void A_message_from_another_chat_is_not_found()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        var stranger = new ChatMessage { Role = "user", Content = "not here" };
        Assert.Equal(RewindStatus.NotFound, ConversationRewind.Rewind(chat, stranger, history.Save).Status);
        Assert.Equal(6, chat.Messages.Count);
        Assert.Equal(RewindStatus.NotFound, ConversationRewind.Rewind(chat, (ChatMessage)null!, history.Save).Status);
    }

    [Fact]
    public void A_streaming_reply_blocks_the_rewind()
    {
        Conversation chat = Sample();
        chat.Messages[^1].IsStreaming = true;
        var history = new FakeHistory();

        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.Equal(RewindStatus.Busy, result.Status);
        Assert.Equal(6, chat.Messages.Count);
        Assert.Equal(0, history.Saves);
    }

    [Fact]
    public void When_history_cannot_save_the_longer_chat_nothing_is_removed()
    {
        Conversation chat = Sample();
        var history = new FakeHistory { FailOnSave = 1 };
        ChatMessage[] before = chat.Messages.ToArray();

        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.Equal(RewindStatus.SaveFailed, result.Status);
        Assert.Equal(before, chat.Messages);
        Assert.Null(history.Stored);
        Assert.Empty(chat.Branches);
    }

    [Fact]
    public void When_history_cannot_save_the_shorter_chat_the_removed_messages_come_back_in_order()
    {
        Conversation chat = Sample();
        var history = new FakeHistory { FailOnSave = 2 };
        ChatMessage[] before = chat.Messages.ToArray();

        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.Equal(RewindStatus.SaveFailed, result.Status);
        Assert.Equal(before, chat.Messages);                       // the same objects, the same order
        Assert.Equal(6, history.Stored!.Messages.Count);           // history still has the whole chat
    }

    [Fact]
    public void An_exception_while_saving_restores_the_chat_and_propagates()
    {
        Conversation chat = Sample();
        var history = new FakeHistory { ThrowOnSave = 2 };
        ChatMessage[] before = chat.Messages.ToArray();

        Assert.Throws<InvalidOperationException>(() => ConversationRewind.Rewind(chat, chat.Messages[1], history.Save));

        Assert.Equal(before, chat.Messages);
    }

    [Fact]
    public void If_the_store_does_not_keep_the_longer_chat_the_rewind_is_undone_instead_of_losing_it()
    {
        Conversation chat = Sample();
        var history = new FakeHistory { RetainSupersededPaths = false };
        ChatMessage[] before = chat.Messages.ToArray();

        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.Equal(RewindStatus.NotRetained, result.Status);
        Assert.Equal(before, chat.Messages);
        Assert.Equal(6, history.Stored!.Messages.Count);            // and history has the whole chat again
        Assert.Equal(3, history.Saves);
    }

    [Fact]
    public void An_incognito_chat_is_never_saved_but_can_be_undone_while_it_is_open()
    {
        Conversation chat = Sample();
        chat.IsIncognito = true;
        var history = new FakeHistory();

        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.Equal(RewindStatus.Rewound, result.Status);
        Assert.False(result.Retained);
        Assert.Equal(0, history.Saves);
        Assert.Equal(2, chat.Messages.Count);
        Assert.Equal(4, result.Removed.Count);
        Assert.True(ConversationRewind.CanUndo(chat, result));

        Assert.True(ConversationRewind.Undo(chat, result, history.RestoreBranch));
        Assert.Equal(new[] { "question 1", "answer 1", "question 2", "answer 2", "question 3", "answer 3" }, Contents(chat));
        Assert.Equal(0, history.Saves);
    }

    [Fact]
    public void An_incognito_undo_is_refused_once_the_chat_has_moved_on()
    {
        Conversation chat = Sample();
        chat.IsIncognito = true;
        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], new FakeHistory().Save);

        chat.Messages.Add(new ChatMessage { Role = "user", Content = "new line" });

        Assert.False(ConversationRewind.CanUndo(chat, result));
        Assert.False(ConversationRewind.Undo(chat, result, (_, _) => true));
        Assert.Equal(3, chat.Messages.Count);
    }

    [Fact]
    public void Undo_is_refused_for_another_chat_a_failed_rewind_or_a_pruned_version()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.False(ConversationRewind.CanUndo(new Conversation(), result));
        Assert.False(ConversationRewind.CanUndo(chat, null));
        Assert.False(ConversationRewind.CanUndo(chat, new RewindResult { Status = RewindStatus.SaveFailed, ConversationId = chat.Id }));

        chat.Branches.Clear(); // the store pruned the retained version
        Assert.False(ConversationRewind.CanUndo(chat, result));
        Assert.False(ConversationRewind.Undo(chat, result, history.RestoreBranch));
        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public void A_failed_restore_leaves_the_rewound_chat_alone()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save);

        Assert.False(ConversationRewind.Undo(chat, result, (_, _) => false));
        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public void Coding_messages_in_the_removed_tail_are_counted_because_their_file_changes_are_not_rewound()
    {
        Conversation chat = Sample();
        chat.Messages[3].CodeTaskId = "task-1";
        chat.Messages[5].CodeTaskId = "task-2";
        chat.Messages[1].CodeTaskId = "task-0"; // kept, so not counted

        RewindResult result = ConversationRewind.Rewind(chat, chat.Messages[1], new FakeHistory().Save);

        Assert.Equal(2, result.CodingMessagesRemoved);
    }

    [Fact]
    public void Rewinding_twice_keeps_both_longer_versions()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();
        ConversationRewind.Rewind(chat, chat.Messages[3], history.Save);   // 6 -> 4
        RewindResult second = ConversationRewind.Rewind(chat, chat.Messages[1], history.Save); // 4 -> 2

        Assert.Equal(RewindStatus.Rewound, second.Status);
        Assert.Equal(2, chat.Branches.Count);
        Assert.Contains(chat.Branches, b => b.Messages.Count == 6);
        Assert.Contains(chat.Branches, b => b.Messages.Count == 4);
        Assert.True(ConversationRewind.Undo(chat, second, history.RestoreBranch));
        Assert.Equal(4, chat.Messages.Count);
    }

    [Fact]
    public void Rewinding_to_the_very_first_message_keeps_exactly_that_message()
    {
        Conversation chat = Sample();
        var history = new FakeHistory();

        RewindResult result = ConversationRewind.Rewind(chat, 0, history.Save);

        Assert.Equal(RewindStatus.Rewound, result.Status);
        Assert.Equal(new[] { "question 1" }, Contents(chat));
        Assert.Equal(5, result.RemovedCount);
    }

    [Fact]
    public void The_message_clone_used_for_the_in_memory_copy_does_not_share_objects()
    {
        Conversation chat = Sample();
        chat.IsIncognito = true;
        ChatMessage original = chat.Messages[3];
        RewindResult result = ConversationRewind.Rewind(chat, 1, new FakeHistory().Save);
        original.Content = "mutated after the rewind";

        ConversationRewind.Undo(chat, result, (_, _) => true);

        Assert.Equal("answer 2", chat.Messages[3].Content);
    }
}
