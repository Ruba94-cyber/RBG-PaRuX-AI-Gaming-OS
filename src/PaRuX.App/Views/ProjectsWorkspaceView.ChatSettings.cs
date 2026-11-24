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
    /// Per-workspace (Project) chat settings: a system prompt with safe variables ({date}, {workspace}, ...) and
    /// pinned documents under a token budget. Core owns the values: the dialog only edits the template and the pin
    /// list, and shows the server's own preview (expanded prompt, which pins fit, honest warning when they do not).
    /// </summary>
    public sealed partial class ProjectsWorkspaceView
    {
        private static bool ItalianUi => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";

        private async Task ShowChatSettingsAsync(ProjectDto project)
        {
            ProjectChatSettingsResponse loaded;
            try { loaded = await ProjectKnowledgeClient.Current.GetChatSettingsAsync(project.Id, CancellationToken.None); }
            catch (Exception ex)
            {
                _status.Text = T("Could not load the chat settings: ", "Impossibile caricare le impostazioni di chat: ") + ex.Message;
                return;
            }

            ProjectChatSettingsDto settings = loaded.Settings;
            var template = new TextBox
            {
                Header = T("System prompt for this workspace", "System prompt di questo workspace"),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 160,
                MaxLength = PromptVariablePolicy.MaxTemplateCharacters, Text = settings.SystemPromptTemplate,
                PlaceholderText = T("Today is {date}. You are helping with {workspace}.", "Oggi è {data}. Stai aiutando con {nome workspace}.")
            };
            AutomationProperties.SetAutomationId(template, "Projects_ChatSettings_SystemPrompt_Box");
            string variableHelp = string.Join("   ", PromptVariablePolicy.Catalog.Select(v =>
                "{" + (ItalianUi && v.Aliases.Length > 1 ? v.Aliases[1] : v.Key) + "} = " + (ItalianUi ? v.DescriptionIt : v.DescriptionEn)));
            var help = new TextBlock { Text = variableHelp, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
            var budget = new NumberBox
            {
                Header = T("Pinned documents token budget", "Budget di token dei documenti fissati"),
                Minimum = DocumentPinPolicy.MinBudgetTokens, Maximum = DocumentPinPolicy.MaxBudgetTokens, Value = settings.PinBudgetTokens,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
            };
            AutomationProperties.SetAutomationId(budget, "Projects_ChatSettings_PinBudget_Box");
            var pinsPanel = new StackPanel { Spacing = 4 };
            var search = new TextBox { PlaceholderText = T("Find an indexed document by name…", "Cerca un documento indicizzato per nome…") };
            AutomationProperties.SetAutomationId(search, "Projects_ChatSettings_PinSearch_Box");
            var findButton = new Button { Content = T("Find", "Cerca") };
            var results = new StackPanel { Spacing = 4 };
            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.9 };
            AutomationProperties.SetAutomationId(preview, "Projects_ChatSettings_Preview_Text");

            void RenderPins()
            {
                pinsPanel.Children.Clear();
                foreach (PinnedDocumentRef pin in settings.PinnedDocuments.ToList())
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = pin.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                    var remove = new Button { Content = T("Unpin", "Rimuovi") };
                    AutomationProperties.SetName(remove, T("Unpin ", "Rimuovi ") + pin.Name);
                    remove.Click += (_, _) => { settings.PinnedDocuments.Remove(pin); RenderPins(); };
                    Grid.SetColumn(remove, 1); row.Children.Add(remove);
                    pinsPanel.Children.Add(row);
                }
                if (settings.PinnedDocuments.Count == 0)
                    pinsPanel.Children.Add(new TextBlock { Text = T("No pinned documents.", "Nessun documento fissato."), Opacity = 0.7 });
            }
            RenderPins();

            void ShowPreview(ProjectChatSettingsPreviewDto p)
            {
                var lines = new List<string>();
                if (p.ExpandedSystemPrompt.Length > 0) lines.Add(T("Prompt as the model sees it: ", "Prompt come lo vede il modello: ") + p.ExpandedSystemPrompt);
                if (p.UnknownVariables.Count > 0)
                    lines.Add(T("Unknown variables kept as plain text: ", "Variabili sconosciute lasciate come testo: ") + string.Join(" ", p.UnknownVariables));
                if (p.Pins.Count > 0)
                {
                    var plan = new DocumentPinPlan(
                        p.Pins.Select(x => new PinnedDocumentPlan(x.Name, x.Included, x.Truncated, x.Tokens, string.Empty, x.Problem)).ToList(),
                        p.PinBudgetTokens, p.PinUsedTokens, p.PinExceedsBudget, p.PinTokensEstimated);
                    lines.Add((p.PinTokensEstimated ? "~" : string.Empty) + p.PinUsedTokens.ToString("N0") + " / " + p.PinBudgetTokens.ToString("N0") + T(" tokens pinned.", " token fissati."));
                    string? warning = DocumentPinPolicy.FormatWarning(plan, ItalianUi);
                    if (warning != null) lines.Add(T("Warning: ", "Attenzione: ") + warning);
                }
                preview.Text = string.Join("\n", lines);
            }
            ShowPreview(loaded.Preview);

            findButton.Click += async (_, _) =>
            {
                results.Children.Clear();
                try
                {
                    List<PinnableDocumentDto> found = await ProjectKnowledgeClient.Current.SearchPinnableDocumentsAsync(project.Id, search.Text, CancellationToken.None);
                    if (found.Count == 0) results.Children.Add(new TextBlock { Text = T("No indexed document matches.", "Nessun documento indicizzato corrisponde."), Opacity = 0.7 });
                    foreach (PinnableDocumentDto doc in found)
                    {
                        var pinButton = new Button { Content = T("Pin ", "Fissa ") + doc.Name, HorizontalAlignment = HorizontalAlignment.Left };
                        pinButton.IsEnabled = settings.PinnedDocuments.Count < DocumentPinPolicy.MaxPins
                            && !settings.PinnedDocuments.Any(p => p.SourceId == doc.SourceId && p.Name.Equals(doc.Name, StringComparison.OrdinalIgnoreCase));
                        pinButton.Click += (_, _) =>
                        {
                            settings.PinnedDocuments.Add(new PinnedDocumentRef { Name = doc.Name, SourceId = doc.SourceId });
                            RenderPins();
                            pinButton.IsEnabled = false;
                        };
                        results.Children.Add(pinButton);
                    }
                }
                catch (Exception ex) { results.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }); }
            };

            var content = new StackPanel { Spacing = 8, MinWidth = 520 };
            foreach (UIElement element in new UIElement[]
            {
                template, help,
                new TextBlock { Text = T("Pinned documents (always included, within the budget)", "Documenti fissati (sempre inclusi, nel budget)"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                pinsPanel, budget, search, findButton, results, preview
            }) content.Children.Add(element);

            var dialog = new ContentDialog
            {
                Title = T("Chat settings: ", "Impostazioni di chat: ") + (project.Goal.Length > 40 ? project.Goal[..40] + "…" : project.Goal),
                Content = new ScrollViewer { Content = content, MaxHeight = 520 },
                PrimaryButtonText = T("Save", "Salva"),
                CloseButtonText = T("Close", "Chiudi"),
                XamlRoot = XamlRoot
            };
            AutomationProperties.SetAutomationId(dialog, "Projects_ChatSettings_Dialog");
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true; // stay open so the honest preview/warning is visible after saving
                settings.SystemPromptTemplate = template.Text;
                settings.PinBudgetTokens = double.IsNaN(budget.Value) ? DocumentPinPolicy.DefaultBudgetTokens : (int)budget.Value;
                try
                {
                    ProjectChatSettingsResponse saved = await ProjectKnowledgeClient.Current.SaveChatSettingsAsync(project.Id, settings, CancellationToken.None);
                    settings = saved.Settings;
                    RenderPins();
                    ShowPreview(saved.Preview);
                    preview.Text = T("Saved. ", "Salvato. ") + preview.Text;
                }
                catch (Exception ex) { preview.Text = T("Not saved: ", "Non salvato: ") + ex.Message; }
            };
            await dialog.ShowAsync();
        }
    }
}
