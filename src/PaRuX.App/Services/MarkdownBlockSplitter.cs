using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    public enum MarkdownBlockKind { Paragraph, Code, Table, Heading, List }

    /// <summary>One top-level Markdown block: the source lines it covers plus what the splitter learned about it.</summary>
    public sealed class MarkdownBlock
    {
        public MarkdownBlockKind Kind { get; init; }
        /// <summary>Character offset (in the original string) of the first line of this block.</summary>
        public int StartOffset { get; init; }
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
        /// <summary>Code blocks: text after the opening fence.</summary>
        public string Language { get; init; } = string.Empty;
        /// <summary>Code blocks: true once the closing fence has arrived.</summary>
        public bool Closed { get; init; } = true;
        public MarkdownTableParser.Table? Table { get; init; }
    }

    /// <summary>
    /// Pure (UI-free) block splitter for the chat Markdown renderer. It is the single source of truth for block
    /// boundaries, so the full render and the streaming (incremental) render cannot disagree.
    /// </summary>
    public static class MarkdownBlockSplitter
    {
        internal static readonly Regex OrderedItemRx = new Regex(@"^(\s*)(\d+)[\.\)]\s+(.*)$", RegexOptions.Compiled);
        internal static readonly Regex BulletItemRx = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        internal static readonly Regex HeadingRx = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);

        public static List<MarkdownBlock> Split(string markdown, int startOffset = 0)
            => Split(markdown, startOffset, out _);

        /// <summary>Splits <paramref name="markdown"/> from <paramref name="startOffset"/> (which must be a line start).
        /// <paramref name="linesScanned"/> reports how many lines were examined, for cost accounting.</summary>
        public static List<MarkdownBlock> Split(string markdown, int startOffset, out int linesScanned)
        {
            var blocks = new List<MarkdownBlock>();
            linesScanned = 0;
            if (string.IsNullOrEmpty(markdown) || startOffset >= markdown.Length) return blocks;

            var lines = new List<string>();
            var starts = new List<int>();
            ScanLines(markdown, startOffset, lines, starts);
            linesScanned = lines.Count;

            var paragraph = new List<string>();
            int paragraphStart = 0;
            int i = 0;

            void FlushParagraph()
            {
                if (paragraph.Count == 0) return;
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Paragraph, StartOffset = paragraphStart, Lines = paragraph.ToArray() });
                paragraph.Clear();
            }

            while (i < lines.Count)
            {
                string line = lines[i];

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    int blockStart = starts[i];
                    string language = line.TrimStart().Substring(3).Trim();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Count && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        code.Add(lines[i]);
                        i++;
                    }
                    bool closed = i < lines.Count;
                    if (closed) i++;   // consume the closing fence (absent while streaming)
                    blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Code, StartOffset = blockStart, Lines = code, Language = language, Closed = closed });
                    continue;
                }

                MarkdownTableParser.Table? table = MarkdownTableParser.TryParse(lines, i);
                if (table != null)
                {
                    FlushParagraph();
                    blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Table, StartOffset = starts[i], Table = table });
                    i += table.LinesConsumed;
                    continue;
                }

                if (HeadingRx.IsMatch(line))
                {
                    FlushParagraph();
                    blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Heading, StartOffset = starts[i], Lines = new[] { line } });
                    i++;
                    continue;
                }

                if (BulletItemRx.IsMatch(line) || OrderedItemRx.IsMatch(line))
                {
                    FlushParagraph();
                    int blockStart = starts[i];
                    var items = new List<string>();
                    while (i < lines.Count && (BulletItemRx.IsMatch(lines[i]) || OrderedItemRx.IsMatch(lines[i])))
                    {
                        items.Add(lines[i]);
                        i++;
                    }
                    blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.List, StartOffset = blockStart, Lines = items });
                    continue;
                }

                if (line.Trim().Length == 0)
                {
                    FlushParagraph();
                    i++;
                    continue;
                }

                if (paragraph.Count == 0) paragraphStart = starts[i];
                paragraph.Add(line);
                i++;
            }

            FlushParagraph();
            return blocks;
        }

        /// <summary>Splits on \n, \r\n and lone \r (the old renderer normalised all three) while remembering original offsets.</summary>
        private static void ScanLines(string text, int offset, List<string> lines, List<int> starts)
        {
            int lineStart = offset;
            int n = text.Length;
            for (int p = offset; p < n; p++)
            {
                char c = text[p];
                if (c != '\n' && c != '\r') continue;
                lines.Add(text.Substring(lineStart, p - lineStart));
                starts.Add(lineStart);
                if (c == '\r' && p + 1 < n && text[p + 1] == '\n') p++;
                lineStart = p + 1;
            }
            // Trailing segment (also the empty line after a final terminator, as string.Split produced).
            lines.Add(text.Substring(lineStart));
            starts.Add(lineStart);
        }
    }

    /// <summary>
    /// Tracks the already-complete prefix of a growing Markdown string so each streaming update only re-splits
    /// from the open tail onward. Every block before the tail is stable: a later append cannot change it
    /// (block ends are decided by following lines that already exist; an unclosed code fence always runs to the end).
    /// </summary>
    public sealed class MarkdownIncrementalSplitter
    {
        private string _markdown = string.Empty;
        private int _stableOffset;

        public int StableBlockCount { get; private set; }

        /// <summary>Total lines examined across all updates (linear-cost accounting for tests).</summary>
        public long TotalLinesScanned { get; private set; }

        public sealed class Update
        {
            /// <summary>True when the previous stable prefix no longer matches: the caller must discard everything it built.</summary>
            public bool Reset { get; init; }
            /// <summary>Stable blocks retained from earlier updates (0 after a reset). The caller keeps their output.</summary>
            public int KeptStableBlocks { get; init; }
            /// <summary>Leading blocks of <see cref="Blocks"/> that became stable; the rest are the open tail (rebuilt next time).</summary>
            public int NewlyStableBlocks { get; init; }
            /// <summary>Blocks re-split this time.</summary>
            public IReadOnlyList<MarkdownBlock> Blocks { get; init; } = Array.Empty<MarkdownBlock>();
        }

        public Update Apply(string markdown)
        {
            markdown ??= string.Empty;
            bool reset = _stableOffset > markdown.Length
                || string.CompareOrdinal(markdown, 0, _markdown, 0, _stableOffset) != 0;
            if (reset)
            {
                _stableOffset = 0;
                StableBlockCount = 0;
            }

            int kept = StableBlockCount;
            List<MarkdownBlock> blocks = MarkdownBlockSplitter.Split(markdown, _stableOffset, out int scanned);
            TotalLinesScanned += scanned;
            _markdown = markdown;

            // The open tail is the last block, plus the one before it when the last is a plain paragraph: an
            // unrecognised trailing line can later turn into a list item or table row and extend that previous block.
            int open = blocks.Count > 0 && blocks[blocks.Count - 1].Kind == MarkdownBlockKind.Paragraph ? 2 : 1;
            int newlyStable = Math.Max(0, blocks.Count - open);
            if (newlyStable > 0)
            {
                StableBlockCount += newlyStable;
                _stableOffset = blocks[newlyStable].StartOffset;
            }
            return new Update { Reset = reset, KeptStableBlocks = kept, Blocks = blocks, NewlyStableBlocks = newlyStable };
        }

        public void Clear()
        {
            _markdown = string.Empty;
            _stableOffset = 0;
            StableBlockCount = 0;
        }
    }
}
