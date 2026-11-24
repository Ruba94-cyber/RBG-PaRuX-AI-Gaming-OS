using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Group chat extras: regenerate ONE speaker of a finished run, and reorder the speakers before a run.
    /// Regeneration is a Core operation (it reloads that speaker's model); replies after it are shown as outdated
    /// and the reconciled verdict is withdrawn instead of being left standing over replies it no longer matches.
    /// </summary>
    public sealed partial class SettingsView
    {
        private string? _groupChatLastId;
        private bool _groupChatRegenerating;

        private static bool IsTerminalGroupChat(GroupChatStatus status) => status.Status is "completed" or "failed" or "cancelled";

        /// <summary>Rebuilds the reply list from the Core status (cheap: at most 4 replies and a verdict).</summary>
        private void RenderGroupChatTurns(GroupChatStatus status)
        {
            GroupChatResultsList.Children.Clear();
            bool italian = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
            bool canRegenerate = IsTerminalGroupChat(status) && !_groupChatRegenerating;
            for (int i = 0; i < status.Turns.Count; i++)
            {
                GroupChatTurn turn = status.Turns[i];
                var block = new StackPanel { Spacing = 4 };
                string heading = turn.Stale ? turn.Label + (italian ? "  (superata: uno speaker precedente è stato rigenerato)" : "  (outdated: an earlier speaker was regenerated)") : turn.Label;
                block.Children.Add(new TextBlock { Text = heading, Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"], TextWrapping = TextWrapping.Wrap });
                block.Children.Add(new TextBlock
                {
                    Text = turn.Response, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                    Opacity = turn.Stale ? 0.6 : 1, Style = (Style)Application.Current.Resources["PaRuXBodyTextStyle"]
                });
                if (canRegenerate)
                {
                    int index = i;
                    var again = new Button { Content = T("Regenerate this speaker", "Rigenera questo speaker"), HorizontalAlignment = HorizontalAlignment.Left };
                    AutomationProperties.SetAutomationId(again, "Settings_GroupChat_Regenerate_" + i + "_Button");
                    AutomationProperties.SetName(again, T("Regenerate speaker ", "Rigenera lo speaker ") + turn.Label);
                    again.Click += async (_, _) => await RegenerateGroupChatSpeakerAsync(index);
                    block.Children.Add(again);
                }
                GroupChatResultsList.Children.Add(block);
            }
            if (status.Verdict != null)
            {
                var verdict = new StackPanel { Spacing = 4 };
                verdict.Children.Add(new TextBlock { Text = T("Panel answer", "Risposta del gruppo"), Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"] });
                verdict.Children.Add(new TextBlock { Text = status.Verdict.Answer, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = (Style)Application.Current.Resources["PaRuXBodyTextStyle"] });
                if (status.Verdict.Disagreements.Count > 0)
                    verdict.Children.Add(new TextBlock
                    {
                        Text = T("Not agreed: ", "Non concordi: ") + string.Join("; ", status.Verdict.Disagreements),
                        TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["PaRuXBodyTextStyle"]
                    });
                GroupChatResultsList.Children.Add(verdict);
            }
            else if (IsTerminalGroupChat(status) && status.Turns.Any(t => t.Stale))
            {
                GroupChatResultsList.Children.Add(new TextBlock
                {
                    Text = T("The panel answer was withdrawn because a speaker was regenerated. Start a new group chat for a fresh reconciliation.",
                             "La risposta del gruppo è stata ritirata perché uno speaker è stato rigenerato. Avvia una nuova chat di gruppo per una nuova sintesi."),
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"]
                });
            }
        }

        private async Task RegenerateGroupChatSpeakerAsync(int index)
        {
            string? id = _groupChatLastId;
            if (id == null || _groupChatRegenerating) return;
            _groupChatRegenerating = true;
            GroupChatRunButton.IsEnabled = false;
            GroupChatProgress.IsActive = true;
            GroupChatProgress.Visibility = Visibility.Visible;
            _groupChatRunId = id;
            GroupChatCancelButton.IsEnabled = true;
            GroupChatCancelButton.Visibility = Visibility.Visible;
            GroupChatStageText.Text = T("Starting…", "Avvio…");
            try
            {
                GroupChatStatus started = await GroupChatClient.Current.RegenerateSpeakerAsync(id, index, CancellationToken.None);
                RenderGroupChatTurns(started);
                await PollGroupChatAsync(id);
            }
            catch (Exception ex)
            {
                Log.Write("GroupChat regenerate failed: " + ex.Message);
                GroupChatStageText.Text = T("Could not regenerate: ", "Impossibile rigenerare: ") + ex.Message;
            }
            finally
            {
                _groupChatRegenerating = false;
                _groupChatRunId = null;
                GroupChatCancelButton.Visibility = Visibility.Collapsed;
                GroupChatRunButton.IsEnabled = true;
                GroupChatProgress.IsActive = false;
                GroupChatProgress.Visibility = Visibility.Collapsed;
                try
                {
                    GroupChatStatus latest = await GroupChatClient.Current.GetStatusAsync(id, CancellationToken.None);
                    RenderGroupChatTurns(latest);
                }
                catch (Exception) { /* the stage text already says what happened */ }
            }
        }

        private ComboBox[] GroupChatCombos => new[] { GroupChatModel1Combo, GroupChatModel2Combo, GroupChatModel3Combo, GroupChatModel4Combo };

        private void GroupChatReorder_Click(object sender, RoutedEventArgs e)
        {
            List<ModelCompareAvailableModel> chosen = GroupChatCombos
                .Select(c => (c.SelectedItem as ComboBoxItem)?.Tag as ModelCompareAvailableModel)
                .Where(m => m != null).Select(m => m!).ToList();
            if (chosen.Count < 2)
            {
                GroupChatStageText.Text = T("Choose at least two models first.", "Scegli prima almeno due modelli.");
                return;
            }

            var panel = new StackPanel { Spacing = 6, MinWidth = 280 };
            void Rebuild()
            {
                panel.Children.Clear();
                for (int i = 0; i < chosen.Count; i++)
                {
                    int index = i;
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = (i + 1) + ". " + chosen[i].Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                    Button Move(string glyph, int delta, string id, string name)
                    {
                        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, IsEnabled = GroupChatOrderPolicy.CanMove(chosen, index, delta) };
                        AutomationProperties.SetAutomationId(b, id + index);
                        AutomationProperties.SetName(b, name + " " + chosen[index].Label);
                        b.Click += (_, _) => { chosen = GroupChatOrderPolicy.Move(chosen, index, delta); Rebuild(); };
                        return b;
                    }
                    Button up = Move("", -1, "Settings_GroupChat_MoveUp_", T("Move up", "Sposta su"));
                    Button down = Move("", +1, "Settings_GroupChat_MoveDown_", T("Move down", "Sposta giù"));
                    Grid.SetColumn(up, 1); Grid.SetColumn(down, 2);
                    row.Children.Add(up); row.Children.Add(down);
                    panel.Children.Add(row);
                }
            }
            Rebuild();

            var apply = new Button { Content = T("Use this order", "Usa questo ordine") };
            AutomationProperties.SetAutomationId(apply, "Settings_GroupChat_ApplyOrder_Button");
            var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { panel, apply } } };
            apply.Click += (_, _) =>
            {
                ApplyGroupChatOrder(chosen);
                flyout.Hide();
            };
            flyout.ShowAt((FrameworkElement)sender);
        }

        /// <summary>Puts the models into the combos in the new speaking order (same set, only the order changes).</summary>
        private void ApplyGroupChatOrder(IReadOnlyList<ModelCompareAvailableModel> ordered)
        {
            ComboBox[] combos = GroupChatCombos;
            for (int i = 0; i < combos.Length; i++)
            {
                ModelCompareAvailableModel? model = i < ordered.Count ? ordered[i] : null;
                ComboBoxItem? match = model == null
                    ? combos[i].Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag == null)
                    : combos[i].Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag is ModelCompareAvailableModel m
                        && string.Equals(m.Kind, model.Kind, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.ModelPath, model.ModelPath, StringComparison.OrdinalIgnoreCase));
                if (match != null) combos[i].SelectedItem = match;
            }
            GroupChatStageText.Text = T("Speaking order updated.", "Ordine di intervento aggiornato.");
        }
    }
}
