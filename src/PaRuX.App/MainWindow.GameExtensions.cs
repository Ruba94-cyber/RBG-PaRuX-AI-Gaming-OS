using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX
{
    /// <summary>Re-applies the accent when a game extension theme changes and restores the owner's chosen theme at start (data only, no code runs).</summary>
    public sealed partial class MainWindow
    {
        private void InitializeGameExtensionTheme()
        {
            LibraryThemeState.Changed += () => _dispatcher.TryEnqueue(App.ApplyTheme);
            string id = (SettingsStore.Current.Data.ActiveLibraryThemeExtensionId ?? string.Empty).Trim();
            if (id.Length == 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    GamingCallResult<List<GameExtensionInfo>> list = await GamingOrchestratorClient.Current.ListExtensionsAsync(CancellationToken.None).ConfigureAwait(false);
                    GameExtensionInfo? ext = list.Value?.FirstOrDefault(e => e.Id == id && e.Valid && e.Enabled && e.Theme is not null);
                    // A disabled, changed, removed or invalid extension never keeps theming the app.
                    _dispatcher.TryEnqueue(() => LibraryThemeState.Set(ext?.Theme));
                }
                catch (Exception ex) { Log.Write("Game extension theme restore failed: " + ex.GetType().Name); }
            });
        }
    }
}
