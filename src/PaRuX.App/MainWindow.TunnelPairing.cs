using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX
{
    /// <summary>
    /// Tells the owner, wherever they are in PaRuX, that a phone is asking to pair through the Internet tunnel. Two channels, both
    /// needing a real click on this PC: an in-app InfoBar on the shell (always) and a Windows notification with Approve / Deny
    /// (best effort, when Windows allows it). Nothing is approved by timing out; an unanswered request simply expires on the Core.
    /// </summary>
    public sealed partial class MainWindow
    {
        private DispatcherTimer? _tunnelPairingTimer;
        private readonly HashSet<string> _tunnelToastShown = new(StringComparer.Ordinal);
        private string? _tunnelPromptId;
        private bool _tunnelPollBusy;
        private bool _tunnelToastReady;

        private void StartTunnelPairingWatch()
        {
            if (CorporateMode.IsActive) return; // Corporate never runs the tunnel.
            TryRegisterTunnelToast();
            _tunnelPairingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _tunnelPairingTimer.Tick += async (_, _) => await PollTunnelPairingAsync();
            _tunnelPairingTimer.Start();
        }

        private async Task PollTunnelPairingAsync()
        {
            if (_tunnelPollBusy) return;
            _tunnelPollBusy = true;
            try
            {
                List<RemoteTunnelPendingPairing> pending = await RemoteAccessClient.Current.GetTunnelPendingAsync(CancellationToken.None);
                ShowTunnelPairingPrompt(pending.FirstOrDefault());
                foreach (RemoteTunnelPendingPairing p in pending)
                    if (_tunnelToastShown.Add(p.Id)) TryShowTunnelToast(p);
            }
            catch { ShowTunnelPairingPrompt(null); } // Core not up yet or restarting: nothing to ask.
            finally { _tunnelPollBusy = false; }
        }

        private void ShowTunnelPairingPrompt(RemoteTunnelPendingPairing? pending)
        {
            if (TunnelPairingBanner == null) return;
            _tunnelPromptId = pending?.Id;
            TunnelPairingBanner.IsOpen = pending != null;
            if (pending != null)
                TunnelPairingBanner.Message = pending.DeviceName
                    + T(" wants to pair with this PC through the Internet. Only approve a phone that is yours.",
                        " vuole associarsi a questo PC tramite Internet. Approva solo un telefono che è tuo.");
        }

        private async void TunnelPairingApprove_Click(object sender, RoutedEventArgs e) => await DecideTunnelPairingAsync(true);
        private async void TunnelPairingDeny_Click(object sender, RoutedEventArgs e) => await DecideTunnelPairingAsync(false);

        private async Task DecideTunnelPairingAsync(bool approve)
        {
            string? id = _tunnelPromptId;
            if (id == null) return;
            try { await RemoteAccessClient.Current.DecideTunnelPairingAsync(id, approve, CancellationToken.None); }
            catch (Exception ex) { Log.Write("Tunnel pairing decision failed: " + ex.Message); }
            TunnelPairingBanner.IsOpen = false;
            await PollTunnelPairingAsync();
        }

        // ---- Windows notification (best effort) -----------------------------------------------------------------------

        private void TryRegisterTunnelToast()
        {
            try
            {
                var manager = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
                manager.NotificationInvoked += (_, args) =>
                {
                    if (!args.Arguments.TryGetValue("tunnelPairing", out string? id) || !args.Arguments.TryGetValue("decision", out string? decision)) return;
                    _dispatcher.TryEnqueue(async () =>
                    {
                        _tunnelPromptId = id;
                        await DecideTunnelPairingAsync(decision == "approve");
                    });
                };
                manager.Register();
                _tunnelToastReady = true;
            }
            catch (Exception ex) { Log.Write("Windows notifications unavailable; the in-app prompt is used: " + ex.Message); }
        }

        private void TryShowTunnelToast(RemoteTunnelPendingPairing p)
        {
            if (!_tunnelToastReady) return;
            try
            {
                var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                    .AddArgument("tunnelPairing", p.Id)
                    .AddText(T("A phone wants to pair", "Un telefono vuole associarsi"))
                    .AddText(p.DeviceName + T(" is asking to connect through the Internet. Approve only if it is yours.", " chiede di collegarsi tramite Internet. Approva solo se è tuo."))
                    .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton(T("Approve", "Approva"))
                        .AddArgument("tunnelPairing", p.Id).AddArgument("decision", "approve"))
                    .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton(T("Deny", "Rifiuta"))
                        .AddArgument("tunnelPairing", p.Id).AddArgument("decision", "deny"));
                Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
            }
            catch (Exception ex) { Log.Write("Tunnel pairing toast failed: " + ex.Message); }
        }
    }
}
