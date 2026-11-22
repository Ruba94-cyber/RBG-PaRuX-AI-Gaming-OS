using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;

namespace PaRuX.Views
{
    /// <summary>
    /// Builds the card list of the Optional components page. Shared by Settings and the first-run setup step so both show
    /// the same state, size, purpose and buttons. The cards only render <see cref="OptionalComponentRow"/> decisions;
    /// every button calls back into the caller, which uses the Runtime Manager contracts.
    /// </summary>
    internal static class OptionalComponentCards
    {
        internal static void Render(
            StackPanel host,
            IReadOnlyList<OptionalComponentRow> rows,
            string automationPrefix,
            bool italian,
            bool busy,
            Func<OptionalComponentRow, OptionalComponentButton, Task> onButton,
            Action<Exception> onError,
            bool allowRemove = true)
        {
            host.Children.Clear();
            if (rows.Count == 0)
            {
                host.Children.Add(new TextBlock
                {
                    Text = italian ? "Nessun componente opzionale da mostrare." : "No optional components to show.",
                    FontSize = 12,
                    Opacity = 0.8
                });
                return;
            }

            foreach (OptionalComponentRow row in rows)
            {
                var card = new Border
                {
                    Padding = new Thickness(12),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                AutomationProperties.SetAutomationId(card, automationPrefix + row.Id + "_Row");
                var body = new StackPanel { Spacing = 4 };
                card.Child = body;

                var header = new Grid { ColumnSpacing = 8 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = row.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetAutomationId(title, automationPrefix + row.Id + "_Title");
                header.Children.Add(title);
                var state = new TextBlock { Text = row.StateLabel, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                AutomationProperties.SetAutomationId(state, automationPrefix + row.Id + "_State");
                Grid.SetColumn(state, 1);
                header.Children.Add(state);
                body.Children.Add(header);

                body.Children.Add(Line(row.Purpose, 12, 0.85, automationPrefix + row.Id + "_Purpose"));
                string detail = row.SizeLabel + " · " + row.OwnershipLabel + (string.IsNullOrWhiteSpace(row.Version) ? string.Empty : " · " + row.Version);
                body.Children.Add(Line(detail, 11, 0.75, automationPrefix + row.Id + "_Size"));
                if (!string.IsNullOrWhiteSpace(row.Path)) body.Children.Add(Line(row.Path!, 11, 0.7, automationPrefix + row.Id + "_Path"));
                if (!string.IsNullOrWhiteSpace(row.Note)) body.Children.Add(Line(row.Note!, 11, 0.7, automationPrefix + row.Id + "_Note"));

                if (row.Buttons.Count > 0)
                {
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
                    foreach (OptionalComponentButton kind in row.Buttons)
                    {
                        if (kind == OptionalComponentButton.Remove && !allowRemove) continue;
                        string label = OptionalComponentPolicy.ButtonLabel(kind, italian);
                        var button = new Button { Content = label, FontSize = 12, Padding = new Thickness(8, 3, 8, 3), IsEnabled = !busy };
                        AutomationProperties.SetAutomationId(button, automationPrefix + row.Id + "_" + OptionalComponentPolicy.AutomationSuffix(kind));
                        AutomationProperties.SetName(button, label + " " + row.Name);
                        OptionalComponentRow captured = row;
                        OptionalComponentButton capturedKind = kind;
                        button.Click += async (s, e) =>
                        {
                            try { await onButton(captured, capturedKind); }
                            catch (Exception ex) { onError(ex); }
                        };
                        actions.Children.Add(button);
                    }
                    body.Children.Add(actions);
                }
                host.Children.Add(card);
            }
        }

        private static TextBlock Line(string text, double size, double opacity, string automationId)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Opacity = opacity };
            AutomationProperties.SetAutomationId(block, automationId);
            return block;
        }
    }
}
