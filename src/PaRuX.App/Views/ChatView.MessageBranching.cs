using System;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        public const string MessageBranchAutomationId = "Chat_Message_Branch_Button";

        public const string DuplicateChatAutomationId = "Chat_Message_Duplicate_Item";

        /// <summary>Right-click menu on any message bubble (user or assistant): "Branch from here".</summary>
        private MenuFlyout BuildMessageBranchFlyout(ChatMessage message)
        {
            var item = new MenuFlyoutItem { Text = ChatItalian ? "Crea un ramo da qui" : "Branch from here" };
            AutomationProperties.SetAutomationId(item, MessageBranchAutomationId);
            AutomationProperties.SetName(item, ChatItalian ? "Crea un ramo da qui" : "Branch from here");
            item.Click += (_, _) => BranchFromMessage(message);
            var flyout = new MenuFlyout();
            flyout.Items.Add(item);

            var duplicate = new MenuFlyoutItem { Text = ChatItalian ? "Duplica chat" : "Duplicate chat" };
            AutomationProperties.SetAutomationId(duplicate, DuplicateChatAutomationId);
            AutomationProperties.SetName(duplicate, ChatItalian ? "Duplica chat" : "Duplicate chat");
            duplicate.Click += (_, _) => ShowTransientStatus(ChatSession.Current.DuplicateConversation()
                ? (ChatItalian ? "Aperta una copia di questa chat." : "Opened a copy of this chat.")
                : (ChatItalian ? "Ancora niente da duplicare." : "Nothing to duplicate yet."));
            flyout.Items.Add(duplicate);

            // Rewind removes the later messages from THIS chat (the longer chat stays in Versions); branching leaves it untouched.
            var rewind = new MenuFlyoutItem { Text = ChatItalian ? "Riavvolgi la chat fino a qui…" : "Rewind chat to here…" };
            AutomationProperties.SetAutomationId(rewind, MessageRewindAutomationId);
            AutomationProperties.SetName(rewind, rewind.Text);
            rewind.Click += async (_, _) => await RewindToMessageAsync(message);
            flyout.Items.Add(rewind);

            // Same, and also restores the files the agent changed in the removed coding tasks (never the user's own edits).
            var rewindFiles = new MenuFlyoutItem { Text = ChatItalian ? "Riavvolgi chat e file dell'agente fino a qui…" : "Rewind chat and agent file changes to here…" };
            AutomationProperties.SetAutomationId(rewindFiles, MessageRewindFilesAutomationId);
            AutomationProperties.SetName(rewindFiles, rewindFiles.Text);
            rewindFiles.Click += async (_, _) => await RewindToMessageAsync(message, rewindFiles: true);
            flyout.Items.Add(rewindFiles);

            var undoRewind = new MenuFlyoutItem { Text = ChatItalian ? "Annulla l'ultimo riavvolgimento" : "Undo last rewind", Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(undoRewind, UndoRewindAutomationId);
            AutomationProperties.SetName(undoRewind, undoRewind.Text);
            undoRewind.Click += (_, _) => UndoLastRewind();
            flyout.Items.Add(undoRewind);
            flyout.Opening += (_, _) =>
                undoRewind.Visibility = ChatSession.Current.CanUndoRewind ? Visibility.Visible : Visibility.Collapsed;
            return flyout;
        }

        public const string MessageRewindAutomationId = "Chat_Message_Rewind_Item";
        public const string UndoRewindAutomationId = "Chat_Message_UndoRewind_Item";
        public const string MessageRewindFilesAutomationId = "Chat_Message_RewindFiles_Item";

        private async System.Threading.Tasks.Task RewindToMessageAsync(ChatMessage message, bool rewindFiles = false)
        {
            if (message.IsStreaming || ChatSession.Current.IsBusy)
            {
                ShowTransientStatus(ChatItalian ? "Attendi che la risposta o l'attività di programmazione sia completata prima di riavvolgere." : "Wait for the reply or coding task to finish before rewinding.");
                return;
            }
            var messages = ChatSession.Current.Conversation.Messages;
            int index = messages.IndexOf(message);
            if (index < 0 || index >= messages.Count - 1)
            {
                ShowTransientStatus(ChatItalian ? "Questo è già l'ultimo messaggio: niente da riavvolgere." : "This is already the last message: nothing to rewind.");
                return;
            }

            int removed = messages.Count - index - 1;
            bool incognito = ChatSession.Current.Conversation.IsIncognito;
            bool hasCoding = messages.Skip(index + 1).Any(m => !string.IsNullOrEmpty(m.CodeTaskId));
            var text = new StringBuilder();
            text.Append(ChatItalian
                ? $"I {removed} messaggi dopo questo verranno tolti da questa chat. "
                : $"The {removed} messages after this one will be removed from this chat. ");
            text.Append(incognito
                ? (ChatItalian ? "Questa è una chat in incognito e non viene salvata: potrai annullare solo finché non la lasci." : "This is an incognito chat and is not saved: you can undo only until you leave it.")
                : (ChatItalian ? "La chat completa resta salvata in Versioni, quindi puoi ripristinarla." : "The full chat stays saved in Versions, so you can restore it."));
            if (hasCoding && rewindFiles)
                text.Append(ChatItalian
                    ? " Verranno ripristinati anche i file modificati dall'agente in quei messaggi, ma solo quelli che non hai cambiato dopo di lui; potrai annullare anche questo."
                    : " The files the agent changed in those messages are restored too, but only those you have not edited since; you can undo that as well.");
            else if (hasCoding)
                text.Append(ChatItalian
                    ? " Le modifiche ai file fatte dalle attività di programmazione in quei messaggi NON vengono annullate."
                    : " File changes made by coding tasks in those messages are NOT undone.");

            var dialog = new ContentDialog
            {
                Title = ChatItalian ? "Riavvolgere la chat fino a qui?" : "Rewind the chat to here?",
                Content = new TextBlock { Text = text.ToString(), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = ChatItalian ? "Riavvolgi" : "Rewind",
                CloseButtonText = ChatItalian ? "Annulla" : "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            try
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            catch (Exception ex)
            {
                Log.Write("Rewind dialog failed: " + ex.Message);
                ShowTransientStatus(ChatItalian ? "Impossibile aprire la conferma." : "Couldn't open the confirmation.");
                return;
            }

            RewindResult result;
            try { result = ChatSession.Current.RewindToMessage(message, rewindFiles); }
            catch (Exception ex)
            {
                Log.Write("Rewind failed: " + ex.Message);
                ShowTransientStatus(ChatItalian ? "Impossibile riavvolgere la chat. Non è stato cambiato nulla." : "Couldn't rewind the chat. Nothing was changed.");
                return;
            }
            RefreshConversationBranchNavigation();
            string fileNote = string.Empty;
            if (result.FileRewind is { } files)
            {
                fileNote = ChatItalian
                    ? $" File ripristinati: {files.RestoredFiles}; lasciati com'erano: {files.SkippedFiles}" + (files.TasksWithoutSnapshot > 0 ? $"; {files.TasksWithoutSnapshot} attività senza istantanea (app riavviata)." : ".")
                    : $" Files restored: {files.RestoredFiles}; left as they were: {files.SkippedFiles}" + (files.TasksWithoutSnapshot > 0 ? $"; {files.TasksWithoutSnapshot} task(s) without a snapshot (app restarted)." : ".");
            }
            else if (rewindFiles && result.Status == RewindStatus.Rewound && result.RemovedCodeTaskIds.Count == 0)
                fileNote = ChatItalian ? " Nessuna attività di programmazione da riavvolgere." : " No coding task to rewind.";
            ShowTransientStatus(result.Status switch
            {
                RewindStatus.Rewound => result.Retained
                    ? (ChatItalian ? $"Chat riavvolta: tolti {result.RemovedCount} messaggi. La versione completa è in Versioni." : $"Chat rewound: removed {result.RemovedCount} messages. The full chat is in Versions.")
                    : (ChatItalian ? $"Chat riavvolta: tolti {result.RemovedCount} messaggi. Puoi annullare dal menu del messaggio." : $"Chat rewound: removed {result.RemovedCount} messages. You can undo it from a message's menu."),
                RewindStatus.Busy => ChatItalian ? "Attendi che la risposta o l'attività di programmazione sia completata prima di riavvolgere." : "Wait for the reply or coding task to finish before rewinding.",
                RewindStatus.NothingToRewind => ChatItalian ? "Questo è già l'ultimo messaggio: niente da riavvolgere." : "This is already the last message: nothing to rewind.",
                RewindStatus.NotRetained => ChatItalian ? "Non è stato possibile conservare la chat completa, quindi non è stato cambiato nulla." : "The full chat could not be kept, so nothing was changed.",
                _ => ChatItalian ? "Non è stato possibile salvare la cronologia, quindi la chat è rimasta com'era." : "History could not be saved, so the chat was left as it was."
            } + fileNote);
        }

        private void UndoLastRewind()
        {
            bool restored;
            try { restored = ChatSession.Current.UndoLastRewind(); }
            catch (Exception ex)
            {
                Log.Write("Undo rewind failed: " + ex.Message);
                restored = false;
            }
            RefreshConversationBranchNavigation();
            ShowTransientStatus(restored
                ? (ChatItalian ? "Riavvolgimento annullato: i messaggi sono tornati." : "Rewind undone: the messages are back.")
                : (ChatItalian ? "Non è più possibile annullare il riavvolgimento." : "The rewind can no longer be undone."));
        }

        /// <summary>Visible per-message button next to Copy/Regenerate on assistant replies.</summary>
        private Button BuildBranchActionButton(ChatMessage message)
        {
            Button branch = BuildActionButton("", ChatItalian ? "Crea un ramo da qui" : "Branch from here");
            AutomationProperties.SetAutomationId(branch, MessageBranchAutomationId);
            branch.Click += (_, _) => BranchFromMessage(message);
            return branch;
        }

        private void BranchFromMessage(ChatMessage message)
        {
            if (message.IsStreaming) { ShowTransientStatus(ChatItalian ? "Attendi che la risposta sia completata prima di creare un ramo." : "Wait for the reply to finish before branching."); return; }
            try
            {
                ShowTransientStatus(ChatSession.Current.BranchFromMessage(message)
                    ? (ChatItalian ? "Aperto un nuovo ramo di questa chat." : "Opened a new branch of this chat.")
                    : (ChatItalian ? "Impossibile creare un ramo da questo messaggio." : "Couldn't branch from this message."));
            }
            catch (Exception ex)
            {
                Log.Write("Branch failed: " + ex.Message);
                ShowTransientStatus(ChatItalian ? "Impossibile creare un ramo da questo messaggio." : "Couldn't branch from this message.");
            }
        }
    }
}
