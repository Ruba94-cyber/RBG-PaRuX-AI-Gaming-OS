using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PaRuX.Services
{
    /// <summary>
    /// A small, self-contained Markdown to XAML renderer. No third-party package:
    /// it covers exactly what assistant replies actually use — headings, bold,
    /// italic, inline code, fenced code blocks, lists and links.
    /// </summary>
    public static class MarkdownRenderer
    {
        private static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Courier New");
        private static readonly SolidColorBrush InlineCodeBrush =
            new SolidColorBrush(Color.FromArgb(255, 0x9B, 0x6D, 0xF3));

        private sealed class StreamState
        {
            public readonly MarkdownIncrementalSplitter Splitter = new MarkdownIncrementalSplitter();
            public int Lead;
            public int StableElements;
            public readonly List<UIElement> Owned = new List<UIElement>();
            public TextBlock? TailCodeText;
            public int TailCodeStart = -1;
            public string TailCodeLanguage = string.Empty;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Panel, StreamState> StreamStates = new();

        /// <summary>True while <paramref name="target"/> is being maintained by <see cref="RenderStreaming"/>.</summary>
        public static bool HasStreamingState(Panel target) => StreamStates.TryGetValue(target, out _);

        /// <summary>Replaces the contents of <paramref name="target"/> with rendered Markdown.</summary>
        public static void Render(string markdown, Panel target)
        {
            if (target == null) return;
            StreamStates.Remove(target);
            target.Children.Clear();
            if (string.IsNullOrEmpty(markdown)) return;
            foreach (MarkdownBlock block in MarkdownBlockSplitter.Split(markdown))
                BuildBlock(block, target.Children, out _);
        }

        /// <summary>
        /// Streaming render: children before index <paramref name="lead"/> belong to the caller and are left alone.
        /// Completed blocks keep their elements between calls; only the open last block is rebuilt (an open code
        /// fence is updated in place). Call <see cref="Render"/> once at the end for a guaranteed-correct final pass.
        /// </summary>
        public static void RenderStreaming(string markdown, Panel target, int lead)
        {
            if (target == null) return;
            UIElementCollection children = target.Children;
            StreamStates.TryGetValue(target, out StreamState? state);

            bool intact = state != null && state.Lead == lead
                && children.Count == lead + state.Owned.Count
                && (state.Owned.Count == 0
                    || (ReferenceEquals(children[lead], state.Owned[0])
                        && ReferenceEquals(children[children.Count - 1], state.Owned[state.Owned.Count - 1])));
            if (!intact)
            {
                while (children.Count > lead) children.RemoveAt(children.Count - 1);
                state = new StreamState { Lead = lead };
                StreamStates.Remove(target);
                StreamStates.Add(target, state);
            }
            state = state!;

            MarkdownIncrementalSplitter.Update update = state.Splitter.Apply(markdown);
            if (update.Reset)
            {
                while (children.Count > lead) children.RemoveAt(children.Count - 1);
                state.Owned.Clear();
                state.StableElements = 0;
                state.TailCodeText = null;
            }

            IReadOnlyList<MarkdownBlock> blocks = update.Blocks;

            // Open code fence that merely grew: update its text in place.
            if (!update.Reset && blocks.Count == 1 && state.TailCodeText != null
                && blocks[0].Kind == MarkdownBlockKind.Code
                && blocks[0].StartOffset == state.TailCodeStart
                && blocks[0].Language == state.TailCodeLanguage)
            {
                string code = string.Join(Environment.NewLine, blocks[0].Lines);
                if (!string.Equals(state.TailCodeText.Text, code, StringComparison.Ordinal))
                    state.TailCodeText.Text = code;
                return;
            }

            // Drop the previous open tail, keep everything stable.
            while (state.Owned.Count > state.StableElements)
            {
                children.RemoveAt(children.Count - 1);
                state.Owned.RemoveAt(state.Owned.Count - 1);
            }
            state.TailCodeText = null;

            for (int b = 0; b < blocks.Count; b++)
            {
                int before = children.Count;
                BuildBlock(blocks[b], children, out TextBlock? codeText);
                for (int c = before; c < children.Count; c++) state.Owned.Add(children[c]);
                if (b < update.NewlyStableBlocks) state.StableElements = state.Owned.Count;
                else if (codeText != null && b == blocks.Count - 1)
                {
                    state.TailCodeText = codeText;
                    state.TailCodeStart = blocks[b].StartOffset;
                    state.TailCodeLanguage = blocks[b].Language;
                }
            }
        }

        private static void BuildBlock(MarkdownBlock block, UIElementCollection target, out TextBlock? codeText)
        {
            codeText = null;
            switch (block.Kind)
            {
                case MarkdownBlockKind.Code:
                    target.Add(BuildCodeBlock(string.Join(Environment.NewLine, block.Lines), block.Language, out codeText));
                    break;
                case MarkdownBlockKind.Table:
                    target.Add(BuildTable(block.Table!));
                    break;
                case MarkdownBlockKind.Heading:
                {
                    Match h = MarkdownBlockSplitter.HeadingRx.Match(block.Lines[0]);
                    target.Add(BuildHeading(h.Groups[2].Value, h.Groups[1].Value.Length));
                    break;
                }
                case MarkdownBlockKind.List:
                    foreach (string item in block.Lines)
                    {
                        Match bullet = MarkdownBlockSplitter.BulletItemRx.Match(item);
                        if (bullet.Success)
                        {
                            target.Add(BuildListItem("\u2022", bullet.Groups[2].Value, bullet.Groups[1].Value.Length));
                            continue;
                        }
                        Match ordered = MarkdownBlockSplitter.OrderedItemRx.Match(item);
                        if (ordered.Success)
                            target.Add(BuildListItem(ordered.Groups[2].Value + ".", ordered.Groups[3].Value, ordered.Groups[1].Value.Length));
                    }
                    break;
                default:
                    AddParagraph(block.Lines, target);
                    break;
            }
        }

        private static void AddParagraph(IReadOnlyList<string> lines, UIElementCollection target)
        {
            string text = string.Join(" ", lines).Trim();
            if (text.Length == 0) return;

            var rtb = new RichTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, 0, 0, 8),
                LineHeight = 21
            };
            var p = new Paragraph();
            AddInlines(p.Inlines, text);
            rtb.Blocks.Add(p);
            target.Add(rtb);
        }

        private static UIElement BuildTable(MarkdownTableParser.Table table)
        {
            var grid = new Grid
            {
                Margin = new Thickness(0, 4, 0, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 160, 160, 170))
            };
            int columns = table.Header.Count;
            for (int c = 0; c < columns; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r <= table.Rows.Count; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void AddCell(string text, int row, int column, bool header)
            {
                var cell = new Border
                {
                    BorderThickness = new Thickness(0, 0, column < columns - 1 ? 1 : 0, row < table.Rows.Count ? 1 : 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(50, 160, 160, 170)),
                    Padding = new Thickness(10, 6, 10, 6),
                    Background = header ? new SolidColorBrush(Color.FromArgb(28, 160, 160, 190)) : null
                };
                var rtb = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                var p = new Paragraph
                {
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    TextAlignment = table.Alignments[column] switch
                    {
                        'r' => TextAlignment.Right,
                        'c' => TextAlignment.Center,
                        _ => TextAlignment.Left
                    }
                };
                AddInlines(p.Inlines, text);
                rtb.Blocks.Add(p);
                cell.Child = rtb;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }

            for (int c = 0; c < columns; c++) AddCell(table.Header[c], 0, c, header: true);
            for (int r = 0; r < table.Rows.Count; r++)
                for (int c = 0; c < columns; c++)
                    AddCell(table.Rows[r][c], r + 1, c, header: false);
            return grid;
        }

        private static UIElement BuildHeading(string text, int level)
        {
            var rtb = new RichTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 6)
            };
            var p = new Paragraph
            {
                FontSize = level == 1 ? 20 : (level == 2 ? 17 : 15),
                FontWeight = FontWeights.SemiBold
            };
            AddInlines(p.Inlines, text);
            rtb.Blocks.Add(p);
            return rtb;
        }

        private static UIElement BuildListItem(string marker, string text, int indent)
        {
            int level = Math.Min(indent / 2, 3);

            var grid = new Grid { Margin = new Thickness(level * 16, 1, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bullet = new TextBlock
            {
                Text = marker,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetColumn(bullet, 0);
            grid.Children.Add(bullet);

            var rtb = new RichTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                LineHeight = 21
            };
            var p = new Paragraph();
            AddInlines(p.Inlines, text);
            rtb.Blocks.Add(p);
            Grid.SetColumn(rtb, 1);
            grid.Children.Add(rtb);

            return grid;
        }

        /// <summary>Set by the chat view: opens the sandboxed Canvas preview for (language, code). Null in hosts without Canvas.</summary>
        public static Action<string, string>? CodePreviewHandler { get; set; }

        private static UIElement BuildCodeBlock(string code, string language, out TextBlock codeText)
        {
            var stack = new StackPanel();
            var text = new TextBlock
            {
                Text = code,
                FontFamily = Mono,
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true
            };
            codeText = text;

            bool previewable = CodePreviewHandler != null
                && PaRuX.Intelligence.CanvasPreviewPolicy.Classify(language) != PaRuX.Intelligence.CanvasPreviewKind.None;
            if (!string.IsNullOrWhiteSpace(language) || previewable)
            {
                var header = new Grid { Margin = new Thickness(2, 0, 0, 4) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock
                {
                    Text = language ?? string.Empty,
                    FontSize = 11,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (previewable)
                {
                    bool italian = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
                    var preview = new Button
                    {
                        Content = italian ? "Anteprima" : "Preview",
                        FontSize = 11,
                        Padding = new Thickness(8, 2, 8, 2)
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(preview, "Chat_CodeBlock_Preview_Button");
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview,
                        italian ? "Anteprima isolata del blocco di codice" : "Sandboxed preview of this code block");
                    string lang = language ?? string.Empty;
                    preview.Click += (_, _) => CodePreviewHandler?.Invoke(lang, text.Text);
                    Grid.SetColumn(preview, 1);
                    header.Children.Add(preview);
                }
                stack.Children.Add(header);
            }

            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = text
            };
            stack.Children.Add(scroller);

            var border = new Border { Child = stack };

            object style;
            if (Application.Current.Resources.TryGetValue("PaRuXCodeBlockStyle", out style) && style is Style)
            {
                border.Style = (Style)style;
            }
            else
            {
                border.Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
                border.CornerRadius = new CornerRadius(8);
                border.Padding = new Thickness(14, 12, 14, 12);
                border.Margin = new Thickness(0, 8, 0, 10);
            }

            return border;
        }

        // ------------------------------------------------------------------
        // Inline scanning: **bold**, *italic*, `code`, [text](url)
        // ------------------------------------------------------------------

        private static void AddInlines(InlineCollection target, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var literal = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                // inline code
                if (c == '`')
                {
                    int close = text.IndexOf('`', i + 1);
                    if (close > i)
                    {
                        Flush(target, literal);
                        target.Add(new Run
                        {
                            Text = text.Substring(i + 1, close - i - 1),
                            FontFamily = Mono,
                            Foreground = InlineCodeBrush
                        });
                        i = close + 1;
                        continue;
                    }
                }

                // bold  ** ** or __ __
                if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c
                    && !IsIntraWordUnderscore(text, i, c))
                {
                    string marker = new string(c, 2);
                    int close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                    if (close > i + 1 && !(c == '_' && FollowedByWordChar(text, close + 2)))
                    {
                        Flush(target, literal);
                        var bold = new Bold();
                        AddInlines(bold.Inlines, text.Substring(i + 2, close - i - 2));
                        target.Add(bold);
                        i = close + 2;
                        continue;
                    }
                }

                // italic  * * or _ _
                if ((c == '*' || c == '_') && !IsIntraWordUnderscore(text, i, c))
                {
                    int close = text.IndexOf(c, i + 1);
                    if (close > i + 1 && !(c == '_' && FollowedByWordChar(text, close + 1)))
                    {
                        Flush(target, literal);
                        var italic = new Italic();
                        AddInlines(italic.Inlines, text.Substring(i + 1, close - i - 1));
                        target.Add(italic);
                        i = close + 1;
                        continue;
                    }
                }

                // link  [text](url)
                if (c == '[')
                {
                    int closeBracket = text.IndexOf(']', i + 1);
                    if (closeBracket > i
                        && closeBracket + 1 < text.Length
                        && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket + 2);
                        if (closeParen > closeBracket)
                        {
                            string label = text.Substring(i + 1, closeBracket - i - 1);
                            string url = text.Substring(closeBracket + 2, closeParen - closeBracket - 2).Trim();

                            Uri? uri = null;
                            Uri.TryCreate(url, UriKind.Absolute, out uri);
                            if (uri != null && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto"))
                            {
                                Flush(target, literal);
                                var link = new Hyperlink { NavigateUri = uri };
                                link.Inlines.Add(new Run { Text = label.Length > 0 ? label : url });
                                target.Add(link);
                                i = closeParen + 1;
                                continue;
                            }
                        }
                    }
                }

                literal.Append(c);
                i++;
            }

            Flush(target, literal);
        }

        /// <summary>
        /// CommonMark does not treat underscores inside a word as emphasis, so
        /// snake_case identifiers survive intact.
        /// </summary>
        private static bool IsIntraWordUnderscore(string text, int index, char marker)
        {
            if (marker != '_') return false;
            return index > 0 && char.IsLetterOrDigit(text[index - 1]);
        }

        private static bool FollowedByWordChar(string text, int index)
        {
            return index < text.Length && char.IsLetterOrDigit(text[index]);
        }

        private static void Flush(InlineCollection target, StringBuilder buffer)
        {
            if (buffer.Length == 0) return;
            target.Add(new Run { Text = buffer.ToString() });
            buffer.Clear();
        }
    }
}
