using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Intelligence;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// The /usage chat command: totals of the token usage the model server reported for this conversation.
    /// Local only (nothing is sent to the model) and never estimated: replies without server data are counted
    /// as "without data" instead of being guessed.
    /// </summary>
    public sealed partial class ChatView
    {
        private async Task ShowUsageReportAsync()
        {
            Conversation? conversation = ChatSession.Current.Conversation;
            var replies = conversation?.Messages
                .Where(m => m.Role == "assistant" && !m.IsError && !m.IsStreaming)
                .ToList() ?? new System.Collections.Generic.List<ChatMessage>();
            ChatUsageSummary summary = ChatTokenUsageFormatter.Summarize(replies.Select(m => m.Usage));
            ChatTokenUsage? last = replies.LastOrDefault(m => m.Usage != null)?.Usage;
            string report = ChatTokenUsageFormatter.BuildReport(summary, last, ChatItalian);

            var body = new TextBlock { Text = report, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, IsTextSelectionEnabled = true };
            AutomationProperties.SetAutomationId(body, "Chat_Usage_Report_Text");
            var dialog = new ContentDialog
            {
                Title = T("Token usage", "Uso dei token"),
                Content = body,
                CloseButtonText = T("Close", "Chiudi"),
                XamlRoot = XamlRoot
            };
            AutomationProperties.SetAutomationId(dialog, "Chat_Usage_Dialog");
            await dialog.ShowAsync();
        }
    }
}
