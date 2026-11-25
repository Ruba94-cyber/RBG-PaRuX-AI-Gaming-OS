using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Local game guide: the owner picks a saved local game and specific indexed document folders, Core verifies them
/// again, and the question is only PREFILLED in the existing Chat composer (never sent, no background inference).
/// Extracted unchanged from the former Gaming home (removed in gaming-removal step 4); hosted by the slim Games tools page;
/// every AutomationId (GameHome_LocalGuide_*) is preserved.
/// </summary>
internal static class LocalGameGuideFlow
{
    private static string T(string en, string it) => GameUi.T(en, it);

    /// <summary>True when the active setup profile has local AI at all (the flow is an AI feature).</summary>
    internal static bool IsAvailableForSetupProfile() =>
        SetupProfilePolicy.Plan(SetupProfilePolicy.Parse(SettingsStore.Current.Data.SetupProfileId)).StartLocalAi;

    /// <param name="root">XamlRoot that hosts the dialog.</param>
    /// <param name="status">Receives every user-facing status line.</param>
    /// <param name="chatRequested">Returns true only if the existing Chat accepted the read-only draft.</param>
    internal static async Task RunAsync(XamlRoot root, Action<string> status,
        Func<string, IReadOnlyList<string>, bool>? chatRequested)
    {
        if (!IsAvailableForSetupProfile()) return;
        // Do not even read document folder metadata while a game owns
        // the host. The server repeats the hard-off and AI-ready checks.
        if (GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive())
        {
            status(T("Leave Gaming before preparing local guide Chat.",
                "Esci da Gaming prima di preparare la guida locale."));
            return;
        }
        GamingCallResult<List<ManualGameDto>> gamesResult =
            await GamingLibraryClient.Current.GetManualGamesForModsAsync();
        if (!gamesResult.Succeeded || gamesResult.Value is null)
        {
            status(T("Could not read saved local games.", "Impossibile leggere i giochi locali."));
            return;
        }
        List<ManualGameDto> games = gamesResult.Value
            .Where(g => g.Kind == ManualEntryKinds.Game).OrderBy(g => g.Name).ToList();
        List<RagSourceDto> sources;
        try
        {
            sources = (await RagClient.Current.ListSourcesAsync(CancellationToken.None))
                .Where(s => s.Enabled).OrderBy(s => s.FolderPath).ToList();
        }
        catch (Exception)
        {
            status(T("Could not read local document folders. Check the RAG index.",
                "Impossibile leggere le cartelle locali. Controlla l'indice RAG."));
            return;
        }

        var gamePicker = new ComboBox { Header = T("Saved local game", "Gioco locale salvato"), MinWidth = 260 };
        AutomationProperties.SetAutomationId(gamePicker, "GameHome_LocalGuide_Game");
        foreach (ManualGameDto game in games)
            gamePicker.Items.Add(new ComboBoxItem { Content = game.Name, Tag = game });
        if (gamePicker.Items.Count > 0) gamePicker.SelectedIndex = 0;

        var question = new TextBox
        {
            Header = T("Question for the local guide", "Domanda per la guida locale"),
            MaxLength = 3000, MinHeight = 70, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(question, "GameHome_LocalGuide_Question");
        var allGuideSources = sources
            .Select(s => new LocalGameGuideSourceOption(s.Id, s.FolderPath))
            .ToArray();
        var selectedGuideIds = new HashSet<string>(StringComparer.Ordinal);
        var sourceFilter = new TextBox
        {
            Header = T("Find an indexed folder", "Cerca una cartella indicizzata"),
            PlaceholderText = T("Filter by folder path or source ID", "Filtra per percorso o ID"),
            MaxLength = 100
        };
        AutomationProperties.SetAutomationId(sourceFilter, "GameHome_LocalGuide_SourceFilter");
        var sourceSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(sourceSummary, "GameHome_LocalGuide_SourceSummary");
        var chosenSources = new StackPanel { Spacing = 4 };
        AutomationProperties.SetAutomationId(chosenSources, "GameHome_LocalGuide_Sources");
        AutomationProperties.SetName(chosenSources, T("Indexed folders", "Cartelle indicizzate"));

        // Only 64 checkboxes are realised at once, but every indexed folder
        // remains discoverable and selected IDs survive changes to the filter.
        // Core still validates current consent, membership and indexed chunks.
        void RefreshSourceChoices()
        {
            LocalGameGuideSourcePage page =
                LocalGameGuideSourcePicker.Filter(allGuideSources, sourceFilter.Text);
            chosenSources.Children.Clear();
            int ordinal = 0;
            foreach (LocalGameGuideSourceOption source in page.Visible)
            {
                string name = System.IO.Path.GetFileName(source.FolderPath.TrimEnd('\\', '/'));
                var box = new CheckBox
                {
                    Content = string.IsNullOrWhiteSpace(name) ? source.FolderPath : name,
                    Tag = source.Id,
                    IsChecked = selectedGuideIds.Contains(source.Id)
                };
                AutomationProperties.SetAutomationId(box, "GameHome_LocalGuide_Source_" + ordinal++);
                AutomationProperties.SetName(box, source.FolderPath);
                ToolTipService.SetToolTip(box, source.FolderPath);
                box.Checked += (_, _) =>
                {
                    if (!LocalGameGuideSourcePicker.TrySetSelected(selectedGuideIds, source.Id, true))
                    {
                        box.IsChecked = false;
                        UpdateSourceSummary();
                        sourceSummary.Text += " " + T("Maximum reached: deselect a folder to choose another.",
                            "Limite raggiunto: deseleziona una cartella per sceglierne un'altra.");
                        return;
                    }
                    UpdateSourceSummary();
                };
                box.Unchecked += (_, _) =>
                {
                    LocalGameGuideSourcePicker.TrySetSelected(selectedGuideIds, source.Id, false);
                    UpdateSourceSummary();
                };
                chosenSources.Children.Add(box);
            }
            if (page.Visible.Count == 0)
                chosenSources.Children.Add(new TextBlock
                {
                    Text = T("No indexed folders match. Add and index a document folder first.",
                        "Nessuna cartella indicizzata corrispondente. Aggiungi e indicizza prima una cartella."),
                    TextWrapping = TextWrapping.Wrap
                });
            UpdateSourceSummary();

            void UpdateSourceSummary()
            {
                sourceSummary.Text = T(
                    selectedGuideIds.Count + " of 64 selected; showing " + page.Visible.Count +
                        " of " + page.TotalMatches + " matching folders. Search to find others.",
                    selectedGuideIds.Count + " di 64 selezionate; visualizzate " + page.Visible.Count +
                        " di " + page.TotalMatches + " cartelle corrispondenti. Cerca per trovarne altre.");
            }
        }
        sourceFilter.TextChanged += (_, _) => RefreshSourceChoices();
        RefreshSourceChoices();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = T("Choose the game and specific folders. The question will be prepared for Chat but NOT sent automatically. Gaming must already be closed.",
                "Seleziona gioco e cartelle precise. La domanda sarà preparata in Chat ma NON inviata automaticamente. Gaming deve essere già chiuso."),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(gamePicker);
        panel.Children.Add(question);
        panel.Children.Add(sourceFilter);
        panel.Children.Add(new TextBlock
        {
            Text = T("Indexed folders (select at least one)", "Cartelle indicizzate (selezionane almeno una)")
        });
        panel.Children.Add(sourceSummary);
        panel.Children.Add(new ScrollViewer
        {
            Content = chosenSources, MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        if (games.Count == 0 || sources.Count == 0)
            panel.Children.Add(new TextBlock
            {
                Text = T("Add a local game and index a guide folder before using this feature.",
                    "Aggiungi un gioco locale e indicizza una cartella di guide prima di usare questa funzione."),
                TextWrapping = TextWrapping.Wrap
            });
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = T("Local game guide", "Guida giochi locale"),
            Content = panel,
            PrimaryButtonText = games.Count > 0 && sources.Count > 0
                ? T("Prepare in Chat", "Prepara in Chat") : string.Empty,
            CloseButtonText = T("Cancel", "Annulla"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (gamePicker.SelectedItem is not ComboBoxItem { Tag: ManualGameDto selectedGame })
            return;
        string[] chosen = allGuideSources
            .Where(source => selectedGuideIds.Contains(source.Id))
            .Select(source => source.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (chosen.Length == 0 || string.IsNullOrWhiteSpace(question.Text))
        {
            status(T("Enter a question and select at least one guide folder.",
                "Inserisci una domanda e seleziona almeno una cartella di guide."));
            return;
        }
        GamingCallResult<LocalGameGuidePreparationDto> result =
            await GamingLibraryClient.Current.PrepareLocalGameGuideAsync(
                new LocalGameGuideRequest(selectedGame.Id, question.Text, chosen, true));
        if (!result.Succeeded || result.Value is not { } prepared ||
            !prepared.Documents.Enabled || prepared.Documents.SourceIds.Count == 0)
        {
            status(T("Guide preparation was blocked: verify the index and leave Gaming first.",
                "Preparazione guida bloccata: verifica l'indice e termina prima Gaming."));
            return;
        }
        bool accepted = chatRequested?.Invoke(
            prepared.QuestionForChat, prepared.Documents.SourceIds) == true;
        status(accepted
            ? T("Guide question prepared in Chat. Review it and press Send.",
                "Domanda preparata in Chat. Controllala e premi Invia.")
            : T("Chat has an unsent draft, is busy, or is not available. No question was sent.",
                "Chat contiene una bozza, è occupata o non disponibile. Nessuna domanda è stata inviata."));
    }
}
