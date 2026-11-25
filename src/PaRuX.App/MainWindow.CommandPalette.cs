using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Services;
using Windows.System;

namespace PaRuX
{
    /// <summary>One entry of the Ctrl+K palette. Keywords are extra search words (English and Italian) that are not part of the title.</summary>
    internal sealed record PaletteCommand(string Id, string Title, string Keywords, Action Run);

    /// <summary>
    /// Ctrl+K command palette: type a few letters to jump to a workspace, switch AI/Gaming, start a new chat or toggle Console/TV.
    /// It reuses the sidebar's own navigation (same profile gate), so it can never open a surface the sidebar would hide.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _commandPaletteOpen;

        private void InitializeCommandPalette()
        {
            var accelerator = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += async (_, args) =>
            {
                args.Handled = true;
                await ShowCommandPaletteAsync();
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
            foreach (QuickAction qa in QuickActionCatalog.All)
            {
                if (qa.AcceleratorKey is not char key) continue;
                string id = qa.Id;
                var quick = new KeyboardAccelerator { Key = (VirtualKey)key, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
                quick.Invoked += (_, args) => { args.Handled = true; RunQuickAction(id); };
                RootGrid.KeyboardAccelerators.Add(quick);
            }
        }

        /// <summary>Runs a quick action by opening the same control the mouse path uses, after going to the right workspace.</summary>
        private void RunQuickAction(string id)
        {
            if (!CurrentSetupPlan.ShowAiWorkspaces || App.IsShuttingDown) return;
            if (IsGamingModeTab) SelectModeTab(ModeKeyAi, navigate: false);
            if (id == QuickActionCatalog.CreateImage)
            {
                if (!TryNavigateWorkspace("__create__")) return;
                if (EnsurePage(StudioWorkspace.Create) is PaRuX.Views.CreateWorkspaceView create) create.ShowImageMode();
                return;
            }
            if (!TryNavigateWorkspace("__chat__")) return;
            _dispatcher.TryEnqueue(() =>
            {
                switch (id)
                {
                    case QuickActionCatalog.SearchChats: SearchBox.Focus(FocusState.Keyboard); break;
                    case QuickActionCatalog.ChooseModel: ChatPage.QuickOpenModelPicker(); break;
                    case QuickActionCatalog.GenerationSettings: ChatPage.QuickOpenGenerationSettings(); break;
                    case QuickActionCatalog.AttachFile: ChatPage.QuickOpenAttachPicker(); break;
                }
            });
        }

        private List<PaletteCommand> BuildPaletteCommands()
        {
            var list = new List<PaletteCommand>();
            void Add(string id, string en, string it, string keywords, Action run) =>
                list.Add(new PaletteCommand(id, ConsoleText(en, it), keywords, run));

            void Workspace(string tag, string en, string it, string keywords)
            {
                if (!IsWorkspaceAllowedForSetup(tag)) return;
                Add("go-" + tag.Trim('_'), "Go to " + en, "Vai a " + it, keywords, () => TryNavigateWorkspace(tag));
            }
            Workspace("__chat__", "Chat", "Chat", "conversation talk message conversazione");
            Workspace("__create__", "Create", "Crea", "image video music speech immagine musica voce 3d");
            Workspace("__projects__", "Projects", "Progetti", "artifacts gallery repository artefatti");
            Workspace("__modelhub__", "Model Hub", "Model Hub", "models download install modelli scarica");
            Workspace("__gamestools__", "Games tools", "Strumenti giochi", "playnite mods downloads controller pad streaming guide mod giochi scarica");
            Workspace("__documents__", "Documents", "Documenti", "search files rag cerca file");
            Workspace("__settings__", "Settings", "Impostazioni", "preferences options language theme lingua tema opzioni");
            Workspace("__help__", "Help", "Aiuto", "guide support how to supporto");
            if (StudioModeToggle.IsOn)
            {
                Workspace("__modelrouting__", "Model routing", "Instradamento dei modelli", "routes roles ruoli");
                Workspace("__runtime__", "Runtime", "Runtime", "engine llama backend motore");
                Workspace("__lifecycle__", "Lifecycle Lab", "Lifecycle Lab", "lab test");
                Workspace("__workflows__", "Workflows", "Workflow", "automation automazione");
                Workspace("__tools__", "Tools", "Strumenti", "mcp skills agents agenti");
                Workspace("__observability__", "Observability", "Osservabilità", "logs metrics log metriche");
                Workspace("__updates__", "Updates", "Aggiornamenti", "update version versione");
            }
            if (CurrentSetupPlan.ShowAiWorkspaces)
                foreach (QuickAction qa in QuickActionCatalog.All)
                    list.Add(new PaletteCommand(qa.Id, QuickActionCatalog.PaletteTitle(qa, UiText.IsItalian), qa.Keywords, () => RunQuickAction(qa.Id)));
            Add("new-chat", "New chat", "Nuova chat", "start conversation clear nuova conversazione", () => NewChat_Click(NewChatButton, new RoutedEventArgs()));
            // Console mode is Playnite Fullscreen (the native PaRuX Console/TV shell was removed in gaming-removal step 3).
            if (IsWorkspaceAllowedForSetup(WorkspaceNavigationOrder.GamesWorkspaceTag))
                Add("console", "Open Playnite Console (Fullscreen)", "Apri Playnite Console (schermo intero)", "console fullscreen tv controller big picture playnite schermo intero",
                    () => _ = ShowPlayniteLauncherAsync(PaRuX.Contracts.PlayniteLauncherAction.Console, gamepad: false, fromRequest: true));
            return list;
        }

        private async System.Threading.Tasks.Task ShowCommandPaletteAsync()
        {
            if (_commandPaletteOpen || App.IsShuttingDown || Content?.XamlRoot is null) return;
            _commandPaletteOpen = true;
            try
            {
                List<PaletteCommand> all = BuildPaletteCommands();
                var search = new TextBox
                {
                    PlaceholderText = ConsoleText("Type a command or a workspace…", "Scrivi un comando o un'area…"),
                    MinWidth = 380,
                };
                AutomationProperties.SetAutomationId(search, "CommandPalette_Search");
                AutomationProperties.SetName(search, ConsoleText("Search commands", "Cerca comandi"));
                var list = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
                AutomationProperties.SetAutomationId(list, "CommandPalette_List");
                var empty = new TextBlock { Text = ConsoleText("No matching command", "Nessun comando corrispondente"), Opacity = 0.7, Visibility = Visibility.Collapsed, Margin = new Thickness(4) };
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(search);
                panel.Children.Add(list);
                panel.Children.Add(empty);
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = ConsoleText("Command palette", "Tavolozza dei comandi"),
                    Content = panel,
                    CloseButtonText = ConsoleText("Close", "Chiudi"),
                };
                AutomationProperties.SetAutomationId(dialog, "CommandPalette_Dialog");

                PaletteCommand? chosen = null;
                void Refresh()
                {
                    IReadOnlyList<PaletteCommand> shown = CommandPaletteFilter.Filter(all, search.Text, c => c.Title, c => c.Keywords);
                    list.Items.Clear();
                    foreach (PaletteCommand command in shown)
                    {
                        var item = new ListViewItem { Content = command.Title, Tag = command };
                        AutomationProperties.SetAutomationId(item, "CommandPalette_Item_" + command.Id);
                        AutomationProperties.SetName(item, command.Title);
                        list.Items.Add(item);
                    }
                    empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (shown.Count > 0) list.SelectedIndex = 0;
                }
                void Run(PaletteCommand? command)
                {
                    if (command is null) return;
                    chosen = command;
                    dialog.Hide();
                }
                search.TextChanged += (_, _) => Refresh();
                search.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; }
                    else if (e.Key == VirtualKey.Up && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
                    else if (e.Key == VirtualKey.Enter) { Run((list.SelectedItem as ListViewItem)?.Tag as PaletteCommand); e.Handled = true; }
                };
                list.ItemClick += (_, e) => Run((e.ClickedItem as ListViewItem)?.Tag as PaletteCommand);
                dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
                Refresh();
                await dialog.ShowAsync();
                chosen?.Run();
            }
            finally { _commandPaletteOpen = false; }
        }
    }
}
