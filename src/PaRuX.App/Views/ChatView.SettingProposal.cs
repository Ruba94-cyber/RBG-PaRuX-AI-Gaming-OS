using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Confirmation card for "change my settings from chat". The model can only PROPOSE a change from a short allow-list
    /// (ChatSettingsAllowList); this card is the only place a proposal is applied, and only when the owner presses Apply.
    /// The proposal is re-validated against the allow-list here, so a tampered response cannot widen it.
    /// </summary>
    public sealed partial class ChatView
    {
        private ChatSettingProposalDto? _settingProposal;
        private string? _settingProposalConversation;
        private bool _settingProposalBusy;
        private bool _settingProposalWasBusy;
        private int _settingProposalGeneration;

        private void UpdateSettingProposalTurnState(bool busy)
        {
            if (busy && !_settingProposalWasBusy)
            {
                ++_settingProposalGeneration;
                SettingProposalApply.IsEnabled = false;
                SettingProposalCancel.IsEnabled = false;
            }
            bool finished = _settingProposalWasBusy && !busy;
            _settingProposalWasBusy = busy;
            if (finished) _ = RefreshSettingProposalAsync();
        }

        private async Task RefreshSettingProposalAsync()
        {
            string? conversation = ChatSession.Current.Conversation?.Id;
            if (_settingProposalBusy) return;
            int generation = ++_settingProposalGeneration;
            _settingProposal = null;
            _settingProposalConversation = null;
            SettingProposalPanel.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(conversation) || ChatSession.Current.IsBusy) return;
            try
            {
                var proposals = await DesktopIntegrationClient.Current.GetChatSettingProposalsAsync(conversation, CancellationToken.None);
                if (generation != _settingProposalGeneration || conversation != ChatSession.Current.Conversation?.Id || ChatSession.Current.IsBusy) return;
                if (proposals.Count == 0) return;
                ChatSettingProposalDto proposal = proposals[0];
                // Defence in depth: the card never shows or applies anything outside the allow-list.
                if (!ChatSettingsAllowList.TryValidate(proposal.Key, proposal.Value, CorporateMode.IsActive, out _, out _))
                {
                    await DesktopIntegrationClient.Current.DismissChatSettingProposalAsync(proposal.Id, conversation, CancellationToken.None);
                    return;
                }
                _settingProposal = proposal;
                _settingProposalConversation = conversation;
                SettingProposalText.Text = DescribeSettingProposal(proposal);
                SettingProposalStatus.Text = T("Nothing changes until you press Apply.", "Non cambia nulla finché non premi Applica.");
                SettingProposalApply.IsEnabled = true;
                SettingProposalCancel.IsEnabled = true;
                SettingProposalPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshSettingProposalAsync failed (non-fatal): " + ex.Message);
            }
        }

        internal static string DescribeSettingProposal(ChatSettingProposalDto p)
        {
            string value = p.Value;
            string text = p.Key switch
            {
                "theme" => T("Set the theme to ", "Imposta il tema su ") + value switch
                {
                    "dark" => T("Dark", "Scuro"), "light" => T("Light", "Chiaro"), _ => T("System", "Sistema")
                },
                "language" => T("Set the interface language to ", "Imposta la lingua dell'interfaccia su ") + (value == "it" ? T("Italian", "italiano") : T("English", "inglese")),
                "interface" => T("Switch to the ", "Passa all'interfaccia ") + (value == "advanced" ? T("Advanced interface", "avanzata") : T("Simple interface", "semplice")),
                "textProfile" => T("Use the ", "Usa il profilo ") + (value == "uncensored" ? T("Uncensored", "Senza censura") : T("Standard", "Standard"))
                    + T(" chat model profile (this can reload the model)", " per la chat (può ricaricare il modello)"),
                "webSearch" => value == "on" ? T("Turn web search on", "Attiva la ricerca sul web") : T("Turn web search off", "Disattiva la ricerca sul web"),
                "voiceAutoSend" => value == "on"
                    ? T("Send voice messages automatically after transcription", "Invia automaticamente i messaggi vocali dopo la trascrizione")
                    : T("Stop sending voice messages automatically", "Non inviare più automaticamente i messaggi vocali"),
                _ => p.Description + ": " + value
            };
            return text;
        }

        private async void SettingProposalApply_Click(object sender, RoutedEventArgs e) => await ResolveSettingProposalAsync(apply: true);
        private async void SettingProposalCancel_Click(object sender, RoutedEventArgs e) => await ResolveSettingProposalAsync(apply: false);

        private async Task ResolveSettingProposalAsync(bool apply)
        {
            ChatSettingProposalDto? proposal = _settingProposal;
            string? conversation = _settingProposalConversation;
            if (_settingProposalBusy || ChatSession.Current.IsBusy || proposal == null || conversation == null
                || conversation != ChatSession.Current.Conversation?.Id) return;
            _settingProposalBusy = true;
            SettingProposalApply.IsEnabled = false;
            SettingProposalCancel.IsEnabled = false;
            try
            {
                string message;
                if (apply)
                {
                    ChatSettingApplyResult result = await ChatSettingApplier.ApplyAsync(proposal, CancellationToken.None);
                    message = result.Ok ? T("Done: ", "Fatto: ") + DescribeSettingProposal(proposal) : T("Not applied: ", "Non applicato: ") + result.Error;
                    if (!result.Ok)
                    {
                        // Keep the card so the owner can retry or cancel.
                        SettingProposalStatus.Text = message;
                        SettingProposalApply.IsEnabled = true;
                        SettingProposalCancel.IsEnabled = true;
                        return;
                    }
                }
                else message = T("Cancelled. Nothing was changed.", "Annullato. Non è stato cambiato nulla.");
                try { await DesktopIntegrationClient.Current.DismissChatSettingProposalAsync(proposal.Id, conversation, CancellationToken.None); }
                catch (Exception ex) { Log.Write("Dismissing a setting proposal failed (non-fatal): " + ex.Message); }
                _settingProposal = null;
                _settingProposalConversation = null;
                SettingProposalStatus.Text = message;
                SettingProposalText.Text = string.Empty;
                SettingProposalPanel.Visibility = Visibility.Collapsed;
                ChatSession.Current.AppendAssistantNotice(message);
            }
            catch (Exception ex)
            {
                SettingProposalStatus.Text = T("Could not apply: ", "Impossibile applicare: ") + ex.Message;
                SettingProposalApply.IsEnabled = true;
                SettingProposalCancel.IsEnabled = true;
            }
            finally
            {
                _settingProposalBusy = false;
            }
        }
    }
}
