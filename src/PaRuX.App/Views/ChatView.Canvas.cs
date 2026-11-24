using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PaRuX.Intelligence;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Canvas: previews an HTML/SVG code block from a chat reply in a sandboxed WebView2 frame.
    /// The frame has no network and no file access (see <see cref="CanvasPreviewPolicy"/>): strict CSP in the
    /// document, every navigation except the in-memory document cancelled, every sub-resource that is not data:
    /// answered with a 403, no new windows, downloads, permissions, host objects, web messages or dev tools.
    /// Scripts are OFF; Advanced mode may switch them on for one preview (they still cannot reach anything).
    /// </summary>
    public sealed partial class ChatView
    {
        private bool _canvasBusy;

        private void HookCanvasPreview()
        {
            MarkdownRenderer.CodePreviewHandler = (language, code) => _ = ShowCanvasPreviewAsync(language, code);
        }

        private async Task ShowCanvasPreviewAsync(string language, string code)
        {
            if (_canvasBusy) return;
            _canvasBusy = true;
            try
            {
                bool advanced = !_isSimpleMode;
                var allowScripts = new CheckBox
                {
                    Content = T("Allow scripts in this preview (still no network or files)", "Consenti gli script in questa anteprima (sempre senza rete né file)"),
                    IsChecked = false,
                    Visibility = advanced ? Visibility.Visible : Visibility.Collapsed
                };
                AutomationProperties.SetAutomationId(allowScripts, "Chat_Canvas_AllowScripts_Check");
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
                AutomationProperties.SetAutomationId(status, "Chat_Canvas_Status_Text");
                var web = new WebView2 { Height = 380, MinWidth = 480 };
                AutomationProperties.SetAutomationId(web, "Chat_Canvas_Frame");
                AutomationProperties.SetName(web, T("Sandboxed preview", "Anteprima isolata"));

                var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
                panel.Children.Add(new TextBlock
                {
                    Text = T("Runs in an isolated frame: no network, no access to your files.", "Si esegue in un frame isolato: nessuna rete, nessun accesso ai tuoi file."),
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.8
                });
                panel.Children.Add(allowScripts);
                panel.Children.Add(web);
                panel.Children.Add(status);

                var dialog = new ContentDialog
                {
                    Title = T("Canvas preview", "Anteprima Canvas"),
                    Content = panel,
                    CloseButtonText = T("Close", "Chiudi"),
                    XamlRoot = XamlRoot
                };
                AutomationProperties.SetAutomationId(dialog, "Chat_Canvas_Dialog");

                bool ready = false;
                async Task RenderAsync()
                {
                    CanvasPreviewDocument doc = CanvasPreviewPolicy.Build(code, language, allowScripts.IsChecked == true);
                    if (!doc.Ok)
                    {
                        status.Text = CanvasPreviewPolicy.RefusalMessage(doc.RefusalCode, ChatItalian);
                        web.Visibility = Visibility.Collapsed;
                        return;
                    }
                    try
                    {
                        await web.EnsureCoreWebView2Async();
                        if (!ready) { LockDownCanvasFrame(web.CoreWebView2); ready = true; }
                        web.CoreWebView2.Settings.IsScriptEnabled = doc.ScriptsEnabled;
                        web.Visibility = Visibility.Visible;
                        status.Text = doc.RemovedElements > 0
                            ? T(doc.RemovedElements + " element(s) that could load external content were removed.", doc.RemovedElements + " elemento/i che potevano caricare contenuti esterni sono stati rimossi.")
                            : string.Empty;
                        web.NavigateToString(doc.Document);
                    }
                    catch (Exception ex)
                    {
                        // No fake preview: say the frame could not start.
                        Log.Write("Canvas preview could not start: " + ex.GetType().Name + ": " + ex.Message);
                        web.Visibility = Visibility.Collapsed;
                        status.Text = T("The preview frame could not start on this PC (WebView2 runtime missing or blocked).",
                                        "Il frame di anteprima non si è potuto avviare su questo PC (runtime WebView2 mancante o bloccato).");
                    }
                }
                allowScripts.Click += async (_, _) => await RenderAsync();
                dialog.Opened += async (_, _) => await RenderAsync();
                await dialog.ShowAsync();
                try { web.Close(); } catch (Exception) { }
            }
            finally { _canvasBusy = false; }
        }

        /// <summary>Applies every restriction of the sandbox. Pinned by CanvasPreviewSourceContractTests.</summary>
        internal static void LockDownCanvasFrame(CoreWebView2 core)
        {
            CoreWebView2Settings s = core.Settings;
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = false;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;

            core.NavigationStarting += (_, e) =>
            {
                if (!CanvasPreviewPolicy.IsAllowedNavigation(e.Uri)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!CanvasPreviewPolicy.IsAllowedResourceRequest(e.Request.Uri))
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by the preview sandbox", string.Empty);
            };
        }
    }
}
