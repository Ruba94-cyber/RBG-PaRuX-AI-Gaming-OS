using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PaRuX.Services
{
    public enum CodingSteeringOutcome { Accepted, NoActiveRun, Empty, TooLong, QueueFull }

    /// <summary>
    /// Lets the person add an instruction to a coding task that is already running. The instruction waits in a small
    /// bounded in-memory queue owned by the live run and is delivered at the next step boundary (before the next model
    /// call), as an observation that is clearly marked as a newer user instruction. Nothing is injected into a model
    /// call that is already in flight, and an instruction for a run that is not live is refused instead of being
    /// silently kept for a later, unrelated run.
    /// </summary>
    public static class CodingAgentSteering
    {
        public const int MaxPending = 8;
        public const int MaxCharacters = 2_000;

        private sealed class Slot
        {
            public readonly object Gate = new();
            public readonly List<string> Pending = new();
        }

        private static readonly ConcurrentDictionary<string, Slot> Active = new(StringComparer.Ordinal);

        /// <summary>Raised outside the queue lock whenever a session activates, queues, cancels, delivers, or ends.</summary>
        public static event Action<string>? StateChanged;

        /// <summary>Called by the run for its whole lifetime; disposing it discards undelivered instructions.</summary>
        public static IDisposable Activate(string sessionId)
        {
            var slot = new Slot();
            Active[sessionId] = slot;
            NotifyStateChanged(sessionId);
            return new Scope(sessionId, slot);
        }

        public static bool IsActive(string? sessionId) => !string.IsNullOrWhiteSpace(sessionId) && Active.ContainsKey(sessionId);

        public static int PendingCount(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !Active.TryGetValue(sessionId, out Slot? slot)) return 0;
            lock (slot.Gate) return slot.Pending.Count;
        }

        /// <summary>Returns a thread-safe snapshot of currently queued, undelivered instructions (oldest first).</summary>
        public static IReadOnlyList<string> PeekPending(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !Active.TryGetValue(sessionId, out Slot? slot)) return Array.Empty<string>();
            lock (slot.Gate) return slot.Pending.Count == 0 ? Array.Empty<string>() : slot.Pending.ToArray();
        }

        /// <summary>Cancels all queued, undelivered steering instructions for <paramref name="sessionId"/> and returns how many were removed.</summary>
        public static int ClearPending(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !Active.TryGetValue(sessionId, out Slot? slot)) return 0;
            int removed;
            lock (slot.Gate)
            {
                removed = slot.Pending.Count;
                slot.Pending.Clear();
            }
            if (removed > 0) NotifyStateChanged(sessionId);
            return removed;
        }

        public static CodingSteeringOutcome Submit(string? sessionId, string? text)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !Active.TryGetValue(sessionId, out Slot? slot)) return CodingSteeringOutcome.NoActiveRun;
            string clean = Clean(text);
            if (clean.Length == 0) return CodingSteeringOutcome.Empty;
            if (clean.Length > MaxCharacters) return CodingSteeringOutcome.TooLong;
            lock (slot.Gate)
            {
                if (slot.Pending.Count >= MaxPending) return CodingSteeringOutcome.QueueFull;
                slot.Pending.Add(clean);
            }
            NotifyStateChanged(sessionId);
            return CodingSteeringOutcome.Accepted;
        }

        /// <summary>
        /// Moves every pending instruction into the run's observation log, oldest first, and returns how many were
        /// delivered. Called once per step, before the model is asked for the next action.
        /// </summary>
        public static int DeliverAtStepBoundary(string sessionId, int step, IList<string> log, Action<string>? onStep)
        {
            if (!Active.TryGetValue(sessionId, out Slot? slot)) return 0;
            List<string> batch;
            lock (slot.Gate)
            {
                if (slot.Pending.Count == 0) return 0;
                batch = slot.Pending.ToList();
                slot.Pending.Clear();
            }
            foreach (string instruction in batch)
            {
                log.Add("NEW USER INSTRUCTION (sent while this task was running; delivered before step " + step
                    + "). It comes from the user and takes priority over the earlier plan wherever they conflict. Acknowledge it in your next plan or action: "
                    + instruction);
                onStep?.Invoke("Step " + step + ": delivered a new user instruction (" + Truncate(instruction, 120) + ").");
            }
            NotifyStateChanged(sessionId);
            return batch.Count;
        }

        private static void NotifyStateChanged(string sessionId)
        {
            Action<string>? handlers = StateChanged;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { ((Action<string>)handler)(sessionId); }
                catch { /* observers must never break the agent step loop */ }
            }
        }

        private static string Clean(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.Trim())
                sb.Append(char.IsControl(c) && c is not ('\n' or '\t') ? ' ' : c);
            return sb.ToString().Trim();
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

        private sealed class Scope : IDisposable
        {
            private readonly string _id;
            private readonly Slot _slot;
            private int _disposed;
            public Scope(string id, Slot slot) { _id = id; _slot = slot; }
            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
                // Only remove this run's slot; a newer run for the same id must not lose its queue.
                if (Active.TryRemove(new KeyValuePair<string, Slot>(_id, _slot)))
                    NotifyStateChanged(_id);
            }
        }
    }
}
