using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PaRuX.Services
{
    public enum CodingRevertDecision { NotNeeded, Offer, Perform }

    public enum CodingRevertOutcome { Restored, Deleted, SkippedModifiedByUser, SkippedNotWritten, SkippedUnsafePath }

    public sealed record CodingRevertItem(string Path, CodingRevertOutcome Outcome);

    /// <summary>What a revert changed, kept so the revert itself can be undone (see <see cref="CodingOwnChangeReverter.ApplyUndo"/>).</summary>
    public sealed class CodingRevertUndo
    {
        internal sealed record Item(string RelPath, byte[]? Before, string AfterHash, byte[]? ContentBeforeRevert, string RestoredHash);

        private readonly List<Item> _items = new();
        internal IReadOnlyList<Item> Items => _items;
        internal void Add(Item item) => _items.Add(item);
        public int Count => _items.Count;
    }

    /// <summary>
    /// Records exactly the files the agent wrote (before-state snapshot plus hash after the write) so a failed
    /// verification can undo only those changes. A file whose content no longer matches what the agent wrote
    /// was changed by someone else and is never touched. Paths outside the root or inside .git are refused.
    /// </summary>
    public sealed class CodingOwnChangeReverter
    {
        private sealed record Entry(string RelPath, byte[]? Before, string AfterHash);

        private readonly string _root;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public CodingOwnChangeReverter(string workspaceRoot) => _root = Path.GetFullPath(workspaceRoot);

        public int TrackedCount => _entries.Count;
        public IReadOnlyCollection<string> TrackedPaths => _entries.Keys.ToArray();

        /// <summary>Call immediately before the agent writes <paramref name="relativePath"/>.</summary>
        public void BeforeWrite(string relativePath)
        {
            string? full = Resolve(relativePath);
            if (full == null) throw new InvalidOperationException("Path escapes the workspace.");
            string key = Key(relativePath);
            if (_entries.ContainsKey(key)) return; // keep the original pre-agent state
            _entries[key] = new Entry(key, File.Exists(full) ? File.ReadAllBytes(full) : null, string.Empty);
        }

        /// <summary>Call after the write completed so later user edits can be told apart.</summary>
        public void AfterWrite(string relativePath)
        {
            string key = Key(relativePath);
            string? full = Resolve(relativePath);
            if (full == null || !_entries.TryGetValue(key, out Entry? e)) return;
            _entries[key] = e with { AfterHash = File.Exists(full) ? HashFile(full) : "<deleted>" };
        }

        public static CodingRevertDecision DecideAfterVerification(bool verificationPassed, bool hasOwnChanges, bool autoRevert)
        {
            if (verificationPassed || !hasOwnChanges) return CodingRevertDecision.NotNeeded;
            return autoRevert ? CodingRevertDecision.Perform : CodingRevertDecision.Offer;
        }

        public IReadOnlyList<CodingRevertItem> Revert() => RevertCore(null);

        /// <summary>Reverts like <see cref="Revert"/> and also returns what is needed to put the agent's changes back.</summary>
        public IReadOnlyList<CodingRevertItem> RevertWithUndo(out CodingRevertUndo undo)
        {
            var captured = new CodingRevertUndo();
            IReadOnlyList<CodingRevertItem> items = RevertCore(captured);
            undo = captured;
            return items;
        }

        private IReadOnlyList<CodingRevertItem> RevertCore(CodingRevertUndo? undo)
        {
            var items = new List<CodingRevertItem>();
            foreach (Entry e in _entries.Values.ToArray())
            {
                string? full = Resolve(e.RelPath);
                if (full == null) { items.Add(new(e.RelPath, CodingRevertOutcome.SkippedUnsafePath)); continue; }
                if (e.AfterHash.Length == 0) { items.Add(new(e.RelPath, CodingRevertOutcome.SkippedNotWritten)); continue; }
                bool exists = File.Exists(full);
                string current = exists ? HashFile(full) : "<deleted>";
                if (current != e.AfterHash) { items.Add(new(e.RelPath, CodingRevertOutcome.SkippedModifiedByUser)); continue; }
                byte[]? currentBytes = exists ? File.ReadAllBytes(full) : null;
                if (e.Before == null)
                {
                    if (exists) File.Delete(full);
                    items.Add(new(e.RelPath, CodingRevertOutcome.Deleted));
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, e.Before);
                    items.Add(new(e.RelPath, CodingRevertOutcome.Restored));
                }
                undo?.Add(new CodingRevertUndo.Item(e.RelPath, e.Before, e.AfterHash, currentBytes,
                    e.Before == null ? "<deleted>" : Convert.ToHexString(SHA256.HashData(e.Before))));
                _entries.Remove(e.RelPath);
            }
            return items;
        }

        /// <summary>
        /// Puts back the agent's changes undone by <see cref="RevertWithUndo"/>. A file is only rewritten while it still
        /// holds exactly what the revert left there; anything the user changed since is skipped.
        /// </summary>
        public IReadOnlyList<CodingRevertItem> ApplyUndo(CodingRevertUndo undo)
        {
            var items = new List<CodingRevertItem>();
            foreach (CodingRevertUndo.Item item in undo.Items)
            {
                string? full = Resolve(item.RelPath);
                if (full == null) { items.Add(new(item.RelPath, CodingRevertOutcome.SkippedUnsafePath)); continue; }
                bool exists = File.Exists(full);
                string current = exists ? HashFile(full) : "<deleted>";
                if (current != item.RestoredHash) { items.Add(new(item.RelPath, CodingRevertOutcome.SkippedModifiedByUser)); continue; }
                if (item.ContentBeforeRevert == null)
                {
                    if (exists) File.Delete(full);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, item.ContentBeforeRevert);
                }
                _entries[item.RelPath] = new Entry(item.RelPath, item.Before, item.AfterHash);
                items.Add(new(item.RelPath, CodingRevertOutcome.Restored));
            }
            return items;
        }

        private string? Resolve(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
            string full = Path.GetFullPath(Path.Combine(_root, relativePath));
            string prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string rel = full.Substring(prefix.Length).Replace('\\', '/');
            if (rel.Split('/').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))) return null;
            return full;
        }

        private static string Key(string relativePath) => relativePath.Replace('\\', '/').Trim();

        private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
