using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Models;

namespace PaRuX.Services
{
    public enum RewindStatus
    {
        Rewound,
        /// <summary>The chosen message is already the last one.</summary>
        NothingToRewind,
        /// <summary>The message is not part of this conversation.</summary>
        NotFound,
        /// <summary>A reply is still being written; rewinding under it would orphan the stream.</summary>
        Busy,
        /// <summary>History could not be written, so the conversation was left exactly as it was.</summary>
        SaveFailed,
        /// <summary>History accepted the shorter chat but did not keep the longer one, so the change was undone.</summary>
        NotRetained
    }

    public sealed class RewindResult
    {
        public RewindStatus Status { get; init; }
        public string ConversationId { get; init; } = string.Empty;
        public int KeptCount { get; init; }
        public int RemovedCount { get; init; }

        /// <summary>Messages removed that came from a coding task (their file changes are NOT rewound).</summary>
        public int CodingMessagesRemoved { get; init; }

        /// <summary>True when the removed messages were saved with the conversation (everything except incognito).</summary>
        public bool Retained { get; init; }

        /// <summary>The retained version that holds the longer chat; restoring it undoes the rewind.</summary>
        public string? RetainedBranchId { get; init; }

        /// <summary>Only for an incognito chat, which is never saved: the removed messages, kept in memory for Undo.</summary>
        public IReadOnlyList<ChatMessage> Removed { get; init; } = Array.Empty<ChatMessage>();

        public string? LastKeptMessageId { get; init; }
    }

    /// <summary>
    /// Rewinds a chat to a chosen message: everything after it is removed from the live chat, in place.
    ///
    /// History-safe by construction:
    /// - the full chat is written to history first, so the longer path is the "previous durable path";
    /// - the history store keeps any shorter path's predecessor as a retained version, and this class refuses to
    ///   finish unless it can find that retained version (otherwise it puts everything back);
    /// - a failed write leaves the live chat unchanged;
    /// - nothing is rewound while a reply is streaming;
    /// - an incognito chat (never saved) keeps the removed messages in memory so Undo still works until it is left.
    /// Files changed by coding tasks are not touched; the result says how many such messages were removed.
    /// </summary>
    public static class ConversationRewind
    {
        public static RewindResult Rewind(Conversation conversation, ChatMessage message, Func<Conversation, bool> save)
        {
            if (conversation == null || message == null) return Failed(RewindStatus.NotFound, conversation);
            return Rewind(conversation, conversation.Messages.IndexOf(message), save);
        }

        /// <param name="save">Writes the conversation to history; true only when it is durable (HistoryStore.Save).</param>
        public static RewindResult Rewind(Conversation conversation, int keepThroughIndex, Func<Conversation, bool> save)
        {
            if (conversation == null) return Failed(RewindStatus.NotFound, null);
            ArgumentNullException.ThrowIfNull(save);
            List<ChatMessage> messages = conversation.Messages;
            if (keepThroughIndex < 0 || keepThroughIndex >= messages.Count) return Failed(RewindStatus.NotFound, conversation);
            if (keepThroughIndex == messages.Count - 1) return Failed(RewindStatus.NothingToRewind, conversation);
            if (messages.Any(m => m.IsStreaming)) return Failed(RewindStatus.Busy, conversation);

            bool retained = !conversation.IsIncognito;
            int keptCount = keepThroughIndex + 1;
            List<ChatMessage> tail = messages.GetRange(keptCount, messages.Count - keptCount);
            // The kept message gets its id first so the copy of the full path below carries it too.
            string? lastKeptId = ConversationBranching.EnsureMessageId(messages[keepThroughIndex]);
            List<ChatMessage> fullBefore = messages.Select(ConversationBranching.CloneMessage).ToList();

            // 1. The longer chat must be durable before anything is removed from the live one.
            if (retained && !save(conversation)) return Failed(RewindStatus.SaveFailed, conversation);

            // 2. Shorten it, then write the shorter chat. A failure puts the removed messages back.
            messages.RemoveRange(keptCount, tail.Count);
            string? branchId = null;
            if (retained)
            {
                bool saved;
                try { saved = save(conversation); }
                catch { messages.AddRange(tail); throw; }
                if (!saved)
                {
                    messages.AddRange(tail);
                    return Failed(RewindStatus.SaveFailed, conversation);
                }

                ConversationBranch? kept = FindRetainedBranch(conversation, fullBefore);
                if (kept == null)
                {
                    // The store did not keep the longer chat. Never leave it only in the caller's memory: restore it
                    // in the live chat and write it back to history.
                    messages.AddRange(tail);
                    save(conversation);
                    return Failed(RewindStatus.NotRetained, conversation);
                }
                branchId = kept.Id;
            }

            return new RewindResult
            {
                Status = RewindStatus.Rewound,
                ConversationId = conversation.Id,
                KeptCount = keptCount,
                RemovedCount = tail.Count,
                CodingMessagesRemoved = tail.Count(m => !string.IsNullOrEmpty(m.CodeTaskId)),
                Retained = retained,
                RetainedBranchId = branchId,
                Removed = retained ? Array.Empty<ChatMessage>() : tail.Select(ConversationBranching.CloneMessage).ToList(),
                LastKeptMessageId = lastKeptId
            };
        }

        /// <summary>Whether Undo can still bring the removed messages back.</summary>
        public static bool CanUndo(Conversation conversation, RewindResult? rewind)
        {
            if (conversation == null || rewind == null || rewind.Status != RewindStatus.Rewound) return false;
            if (!string.Equals(conversation.Id, rewind.ConversationId, StringComparison.Ordinal)) return false;
            if (rewind.Retained)
                return rewind.RetainedBranchId != null && conversation.Branches?.Any(b => b.Id == rewind.RetainedBranchId) == true;

            // Incognito: only while the chat still ends at the message the rewind kept.
            return conversation.Messages.Count == rewind.KeptCount
                && string.Equals(conversation.Messages[^1].MessageId, rewind.LastKeptMessageId, StringComparison.Ordinal)
                && rewind.Removed.Count > 0;
        }

        /// <param name="restoreBranch">Restores a retained version into the conversation and saves it (HistoryStore.RestoreBranch).</param>
        public static bool Undo(Conversation conversation, RewindResult? rewind, Func<Conversation, string, bool> restoreBranch)
        {
            if (!CanUndo(conversation, rewind)) return false;
            ArgumentNullException.ThrowIfNull(restoreBranch);
            if (rewind!.Retained) return restoreBranch(conversation, rewind.RetainedBranchId!);

            foreach (ChatMessage message in rewind.Removed) conversation.Messages.Add(ConversationBranching.CloneMessage(message));
            return true;
        }

        private static ConversationBranch? FindRetainedBranch(Conversation conversation, IReadOnlyList<ChatMessage> fullPath) =>
            conversation.Branches?
                .Where(b => b.Messages != null && SamePath(b.Messages, fullPath))
                .OrderByDescending(b => b.CreatedUtc)
                .FirstOrDefault();

        private static bool SamePath(IReadOnlyList<ChatMessage> left, IReadOnlyList<ChatMessage> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                ChatMessage a = left[i], b = right[i];
                if (!string.Equals(a.Role, b.Role, StringComparison.Ordinal)
                    || !string.Equals(a.Content, b.Content, StringComparison.Ordinal)
                    || !string.Equals(a.Reasoning, b.Reasoning, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static RewindResult Failed(RewindStatus status, Conversation? conversation) => new()
        {
            Status = status,
            ConversationId = conversation?.Id ?? string.Empty
        };
    }
}
