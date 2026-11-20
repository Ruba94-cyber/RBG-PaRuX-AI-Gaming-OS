using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Settings card for the optional "local OpenAI-compatible server" provider (vLLM, KoboldCpp, the LM Studio server,
/// text-generation-webui, ...). The server is USER_MANAGED: PaRuX only talks to it. Normal shows the address, a test
/// button, the model and the "use for chat" switch; Advanced holds the name, the API key and removal. Nothing is
/// sent anywhere until the user tests or saves, and chat only uses the server while the switch is on.
/// </summary>
internal sealed class OpenAiCompatibleProviderPanel : StackPanel
{
    private static string T(string en, string it) => UiText.Pick(en, it);

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _address = new();
    private readonly ComboBox _models = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch _useForChat = new();
    private readonly Button _test = new();
    private readonly Button _save = new();
    private readonly TextBox _name = new();
    private readonly PasswordBox _key = new();
    private readonly Button _remove = new();
    private readonly TextBlock _keyNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private bool _busy;
    private bool _loading;
    private CancellationTokenSource? _operation;

    internal OpenAiCompatibleProviderPanel()
    {
        Spacing = 8;
        AutomationProperties.SetAutomationId(this, "Settings_OpenAiCompatible_Panel");

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Text = T(
                "Use a server you already run on this PC or your home network (vLLM, KoboldCpp, the LM Studio server, text-generation-webui) for chat. "
                + "PaRuX never starts, stops or updates it. Only a local address is accepted and nothing is sent until you turn it on. "
                + "Documents, images and tools stay on PaRuX's own model.",
                "Usa per la chat un server che esegui già su questo PC o sulla rete di casa (vLLM, KoboldCpp, il server di LM Studio, text-generation-webui). "
                + "PaRuX non lo avvia, non lo ferma e non lo aggiorna. Si accetta solo un indirizzo locale e non viene inviato nulla finché non lo attivi. "
                + "Documenti, immagini e strumenti restano sul modello di PaRuX."),
        };
        _status.Text = T("No server configured. Chat uses PaRuX's own model.", "Nessun server configurato. La chat usa il modello di PaRuX.");
        AutomationProperties.SetAutomationId(_status, "Settings_OpenAiCompatible_Status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _address.Header = T("Server address", "Indirizzo del server");
        _address.PlaceholderText = "http://127.0.0.1:8000";
        AutomationProperties.SetAutomationId(_address, "Settings_OpenAiCompatible_Address");

        _models.Header = T("Model", "Modello");
        _models.PlaceholderText = T("Test the connection to list models", "Prova la connessione per elencare i modelli");
        AutomationProperties.SetAutomationId(_models, "Settings_OpenAiCompatible_Model");

        _useForChat.Header = T("Use this server for chat", "Usa questo server per la chat");
        _useForChat.OnContent = T("On: chat is sent to this server", "Attivo: la chat va a questo server");
        _useForChat.OffContent = T("Off: chat uses PaRuX's own model", "Spento: la chat usa il modello di PaRuX");
        AutomationProperties.SetAutomationId(_useForChat, "Settings_OpenAiCompatible_UseForChat");

        _test.Content = T("Test connection", "Prova la connessione");
        _test.Click += async (_, _) => await TestAsync();
        AutomationProperties.SetAutomationId(_test, "Settings_OpenAiCompatible_Test");
        _save.Content = T("Save", "Salva");
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += async (_, _) => await SaveAsync();
        AutomationProperties.SetAutomationId(_save, "Settings_OpenAiCompatible_Save");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_test);
        buttons.Children.Add(_save);

        _name.Header = T("Name", "Nome");
        _name.PlaceholderText = T("Shown in the status line, for example vLLM", "Mostrato nella riga di stato, ad esempio vLLM");
        AutomationProperties.SetAutomationId(_name, "Settings_OpenAiCompatible_Name");
        _key.Header = T("API key (optional)", "Chiave API (facoltativa)");
        _key.PlaceholderText = T("Leave empty if the server needs none", "Lascia vuoto se il server non ne richiede");
        AutomationProperties.SetAutomationId(_key, "Settings_OpenAiCompatible_ApiKey");
        _keyNote.Text = T(
            "The key is stored encrypted for your Windows account and sent only to the address above.",
            "La chiave è cifrata per il tuo account Windows e inviata solo all'indirizzo qui sopra.");
        _remove.Content = T("Remove this server", "Rimuovi questo server");
        _remove.Visibility = Visibility.Collapsed;
        _remove.Click += async (_, _) => await RemoveAsync();
        AutomationProperties.SetAutomationId(_remove, "Settings_OpenAiCompatible_Remove");

        var advanced = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        advanced.Children.Add(_name);
        advanced.Children.Add(_key);
        advanced.Children.Add(_keyNote);
        advanced.Children.Add(_remove);
        var expander = new Expander
        {
            Header = T("Advanced", "Avanzate"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = advanced,
        };
        AutomationProperties.SetAutomationId(expander, "Settings_OpenAiCompatible_Advanced");

        Children.Add(intro);
        Children.Add(_status);
        Children.Add(_address);
        Children.Add(_models);
        Children.Add(_useForChat);
        Children.Add(buttons);
        Children.Add(expander);

        Loaded += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) => { _operation?.Cancel(); _operation?.Dispose(); _operation = null; };
    }

    internal async Task RefreshAsync()
    {
        try
        {
            _loading = true;
            OpenAiCompatibleProviderStatus status = await OpenAiCompatibleProviderClient.Current.GetStatusAsync(CancellationToken.None);
            ShowStatus(status);
        }
        catch (Exception ex)
        {
            Log.Write("OpenAiCompatibleProviderPanel.RefreshAsync failed: " + ex.Message);
            _status.Text = T("Couldn't load the server settings: ", "Impossibile caricare le impostazioni del server: ") + ex.Message;
        }
        finally { _loading = false; }
    }

    private void ShowStatus(OpenAiCompatibleProviderStatus status)
    {
        _remove.Visibility = status.Configured ? Visibility.Visible : Visibility.Collapsed;
        if (!status.Configured)
        {
            _status.Text = T("No server configured. Chat uses PaRuX's own model.", "Nessun server configurato. La chat usa il modello di PaRuX.");
            _useForChat.IsOn = false;
            return;
        }

        _address.Text = status.BaseUrl;
        _name.Text = status.DisplayName;
        _useForChat.IsOn = status.UseForChat;
        _key.Password = string.Empty;
        _keyNote.Text = status.HasApiKey
            ? T("An API key is stored (encrypted). Type a new one to replace it.", "Una chiave API è memorizzata (cifrata). Digitane una nuova per sostituirla.")
            : T("No API key stored.", "Nessuna chiave API memorizzata.");
        SetModels(string.IsNullOrEmpty(status.SelectedModel) ? Array.Empty<string>() : new[] { status.SelectedModel }, status.SelectedModel);

        string where = status.IsLanHost ? T("on your network", "sulla tua rete") : T("on this PC", "su questo PC");
        _status.Text = status.UseForChat
            ? T($"Chat is sent to {status.DisplayName} ({status.BaseUrl}, {where}). You manage that server.",
                $"La chat va a {status.DisplayName} ({status.BaseUrl}, {where}). Quel server lo gestisci tu.")
            : T($"{status.DisplayName} is saved ({status.BaseUrl}, {where}) but off. Chat uses PaRuX's own model.",
                $"{status.DisplayName} è salvato ({status.BaseUrl}, {where}) ma spento. La chat usa il modello di PaRuX.");
    }

    private void SetModels(IReadOnlyCollection<string> models, string? selected)
    {
        _models.Items.Clear();
        foreach (string model in models) _models.Items.Add(model);
        if (!string.IsNullOrEmpty(selected) && _models.Items.Contains(selected)) _models.SelectedItem = selected;
        else if (_models.Items.Count > 0) _models.SelectedIndex = 0;
    }

    private OpenAiCompatibleProviderRequest BuildRequest() => new()
    {
        DisplayName = _name.Text?.Trim() ?? string.Empty,
        BaseUrl = _address.Text?.Trim() ?? string.Empty,
        // Null keeps the stored key, a typed value replaces it; the field is never pre-filled with the stored key.
        ApiKey = string.IsNullOrEmpty(_key.Password) ? null : _key.Password,
        SelectedModel = _models.SelectedItem as string,
        UseForChat = _useForChat.IsOn,
    };

    private async Task TestAsync()
    {
        if (!TryBegin(out CancellationToken ct)) return;
        _status.Text = T("Testing…", "Verifica in corso…");
        try
        {
            OpenAiCompatibleTestResult result = await OpenAiCompatibleProviderClient.Current.TestAsync(BuildRequest(), ct);
            string? keep = _models.SelectedItem as string;
            if (result.Models.Count > 0) SetModels(result.Models, keep);
            _status.Text = result.Message + (string.IsNullOrEmpty(result.Warning) ? string.Empty : " " + result.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("OpenAiCompatibleProviderPanel.TestAsync failed: " + ex.Message);
            _status.Text = T("The test failed: ", "La verifica non è riuscita: ") + ex.Message;
        }
        finally { End(); }
    }

    private async Task SaveAsync()
    {
        if (!TryBegin(out CancellationToken ct)) return;
        try
        {
            OpenAiCompatibleProviderStatus status = await OpenAiCompatibleProviderClient.Current.SaveAsync(BuildRequest(), ct);
            ShowStatus(status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("OpenAiCompatibleProviderPanel.SaveAsync failed: " + ex.Message);
            _status.Text = T("Couldn't save: ", "Impossibile salvare: ") + ex.Message;
        }
        finally { End(); }
    }

    private async Task RemoveAsync()
    {
        if (!TryBegin(out CancellationToken ct)) return;
        try
        {
            await OpenAiCompatibleProviderClient.Current.ClearAsync(ct);
            _address.Text = string.Empty;
            _name.Text = string.Empty;
            _key.Password = string.Empty;
            _models.Items.Clear();
            ShowStatus(new OpenAiCompatibleProviderStatus());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("OpenAiCompatibleProviderPanel.RemoveAsync failed: " + ex.Message);
            _status.Text = T("Couldn't remove it: ", "Impossibile rimuoverlo: ") + ex.Message;
        }
        finally { End(); }
    }

    private bool TryBegin(out CancellationToken ct)
    {
        ct = default;
        if (_busy || _loading) return false;
        _busy = true;
        _test.IsEnabled = _save.IsEnabled = _remove.IsEnabled = false;
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        ct = _operation.Token;
        return true;
    }

    private void End()
    {
        _busy = false;
        _test.IsEnabled = _save.IsEnabled = _remove.IsEnabled = true;
    }
}
