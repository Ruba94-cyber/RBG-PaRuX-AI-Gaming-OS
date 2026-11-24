using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Approval card for the chat "run_python" tool. While the chat is on screen it asks the Core which code is waiting for
    /// the owner and shows each request with the exact code, the sandbox limits and Approve / Deny. Nothing is approved by
    /// timeout, by a permission mode or by this card on its own: only the click reaches the Core, and a failed call leaves
    /// the card visible with the reason (the Core's request then expires unanswered and the code is not run).
    /// </summary>
    public sealed class ChatCodeApprovalCard : StackPanel
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
        private readonly DispatcherQueueTimer? _timer;
        private readonly Dictionary<string, Border> _cards = new Dictionary<string, Border>(StringComparer.Ordinal);
        private CancellationTokenSource? _poll;
        private bool _polling;
        private bool _unreachableNoted;

        public ChatCodeApprovalCard()
        {
            Spacing = 8;
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(this, "Chat_CodeApproval_Host");
            _timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
            if (_timer != null)
            {
                _timer.Interval = PollInterval;
                _timer.IsRepeating = true;
                _timer.Tick += async (_, _) => await PollAsync().ConfigureAwait(true);
            }
            Loaded += (_, _) => _timer?.Start();
            Unloaded += (_, _) => { _timer?.Stop(); _poll?.Cancel(); };
        }

        private static string T(string en, string it) => UiText.Pick(en, it);

        private async Task PollAsync()
        {
            if (_polling) return;
            _polling = true;
            _poll?.Cancel();
            _poll = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                IReadOnlyList<ChatCodeApprovalDto> items = await ChatCodeClient.Current.GetApprovalsAsync(_poll.Token).ConfigureAwait(true);
                _unreachableNoted = false;
                Render(ChatCodeApprovalPresenter.Pending(items));
            }
            catch (Exception) when (!_unreachableNoted)
            {
                // The Core is not reachable right now (starting, restarting). No request can be answered
                // meanwhile; the model's wait simply expires. Stay quiet rather than flash an error every 1.5 s.
                _unreachableNoted = true;
            }
            catch (Exception) { }
            finally { _polling = false; }
        }

        private void Render(IReadOnlyList<ChatCodeApprovalView> pending)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChatCodeApprovalView view in pending)
            {
                keep.Add(view.Id);
                if (_cards.ContainsKey(view.Id)) continue;
                Border card = BuildCard(view);
                _cards[view.Id] = card;
                Children.Add(card);
            }
            foreach (string stale in new List<string>(_cards.Keys))
            {
                if (keep.Contains(stale)) continue;
                Children.Remove(_cards[stale]);
                _cards.Remove(stale);
            }
            Visibility = _cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private Border BuildCard(ChatCodeApprovalView view)
        {
            bool italian = UiText.IsItalian;
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock
            {
                Text = ChatCodeApprovalPresenter.Title(italian),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock { Text = ChatCodeApprovalPresenter.SafetyNote(italian), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            var code = new TextBox
            {
                Text = view.Code,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                MaxHeight = 220
            };
            ScrollViewer.SetVerticalScrollBarVisibility(code, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(code, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(code, T("Python code awaiting approval", "Codice Python in attesa di approvazione"));
            content.Children.Add(code);
            if (view.Truncated)
                content.Children.Add(new TextBlock { Text = ChatCodeApprovalPresenter.TruncationNote(italian), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var approve = new Button { Content = ChatCodeApprovalPresenter.Approve(italian), Style = TryAccentStyle() };
            var deny = new Button { Content = ChatCodeApprovalPresenter.Deny(italian) };
            AutomationProperties.SetAutomationId(approve, "Chat_CodeApproval_Approve_Button");
            AutomationProperties.SetAutomationId(deny, "Chat_CodeApproval_Deny_Button");
            approve.Click += async (_, _) => await DecideAsync(view.Id, true, approve, deny, status).ConfigureAwait(true);
            deny.Click += async (_, _) => await DecideAsync(view.Id, false, approve, deny, status).ConfigureAwait(true);
            content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { approve, deny } });
            content.Children.Add(status);

            var card = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = content };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke) && stroke is Brush brush)
                card.BorderBrush = brush;
            if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out object? fill) && fill is Brush bg)
                card.Background = bg;
            return card;
        }

        private static Style? TryAccentStyle() =>
            Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? s) ? s as Style : null;

        private async Task DecideAsync(string id, bool approve, Button approveButton, Button denyButton, TextBlock status)
        {
            approveButton.IsEnabled = false;
            denyButton.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await ChatCodeClient.Current.DecideAsync(id, approve, cts.Token).ConfigureAwait(true);
                // Re-read the Core rather than assume: the card disappears only when the request is really answered.
                await PollAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                status.Text = ChatCodeApprovalPresenter.DecisionFailed(approve, ex.Message, UiText.IsItalian);
                status.Visibility = Visibility.Visible;
                approveButton.IsEnabled = true;
                denyButton.IsEnabled = true;
            }
        }
    }
}
