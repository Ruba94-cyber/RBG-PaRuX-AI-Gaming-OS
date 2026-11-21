using System;
using H.NotifyIcon.Core;
using PaRuX.Services;

namespace PaRuX
{
    /// <summary>Wires the local achievement unlock toast to the tray balloon. The service decides whether anything is announced at all.</summary>
    public sealed partial class MainWindow
    {
        private void InitializeAchievementToasts()
        {
            AchievementToastService.Current.Announce = (title, message) =>
                _dispatcher.TryEnqueue(() =>
                {
                    if (_trayDisposed) return;
                    try { TrayIcon.ShowNotification(title, message, NotificationIcon.Info); }
                    catch (Exception ex) { Log.Write("Achievement notification failed: " + ex.Message); }
                });
            AchievementToastService.Current.Start();
        }
    }
}
