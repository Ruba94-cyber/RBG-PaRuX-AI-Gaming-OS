using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX
{
    public static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Headless MSI uninstall cleanup: runs before WinUI/XAML/AppPaths initialization so it works both in the
            // user session (Impersonate="yes") and in Session 0 as SYSTEM (Impersonate="no") without starting UI or Core.
            if (LogonStartupArguments.IsUninstallCleanupRequest(Environment.GetCommandLineArgs()))
            {
                RunUninstallLogonCleanup();
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }

        private static void RunUninstallLogonCleanup()
        {
            try
            {
                string? exePath = StartupService.LaunchExecutable;
                if (string.IsNullOrWhiteSpace(exePath)) return;
                LogonRegistration.RemoveOwnOnUninstall(
                    new RegistryRunKeyStore(),
                    new ScheduledTaskLogonStore(log: false),
                    exePath);
            }
            catch
            {
                // Best-effort cleanup: never fail or block MSI uninstallation.
            }
        }
    }
}
