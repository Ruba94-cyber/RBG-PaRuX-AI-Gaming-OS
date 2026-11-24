using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Intelligence;
using PaRuX.Models;
using PaRuX.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Chat appearance (Advanced): bubble/background colours, font and size for every chat, plus an optional
    /// per-chat background. The theme is a validated closed shape (ChatAppearancePolicy), never CSS or a path;
    /// import accepts a small JSON file and rejects anything unknown. Windows High Contrast always wins: with it on
    /// nothing custom is painted. Text colours that would be unreadable are replaced by a readable black/white.
    /// </summary>
    public sealed partial class ChatView
    {
        private ResolvedChatAppearance? _appearance;

        private static bool HighContrastActive()
        {
            try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
            catch (Exception) { return false; }
        }

        private static Windows.UI.Color? ParseHex(string? hex)
        {
            if (!ChatAppearancePolicy.IsValidColor(hex)) return null;
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex!.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
        }

        private static Brush? BrushOf(string? hex) => ParseHex(hex) is { } c ? new SolidColorBrush(c) : null;

        /// <summary>Re-reads the theme and the chat's own background and repaints. Safe to call at any time.</summary>
        internal void ApplyChatAppearance()
        {
            try
            {
                if (HighContrastActive())
                {
                    _appearance = null;
                }
                else
                {
                    ChatAppearanceTheme? theme = ChatAppearanceState.Load();
                    string? chatBackground = ChatSession.Current.Conversation?.BackgroundColor;
                    _appearance = theme == null && chatBackground == null ? null : ChatAppearancePolicy.Resolve(theme, chatBackground);
                }

                ResolvedChatAppearance? a = _appearance;
                SetOrClear(MessagesScroll, Control.BackgroundProperty, BrushOf(a?.Background));
                SetOrClear(MessagesScroll, Control.ForegroundProperty, BrushOf(a?.AssistantText));
                SetOrClear(MessagesScroll, Control.FontFamilyProperty, a?.FontFamily == null ? null : new FontFamily(a.FontFamily));
                SetOrClear(MessagesScroll, Control.FontSizeProperty, a?.FontSize is { } size ? size : null);

                foreach (var pair in _visuals) ApplyBubbleAppearance(pair.Key, pair.Value);
                foreach (var pair in _contentPanels) if (pair.Key.Role == "user") ApplyUserTextAppearance(pair.Value);
            }
            catch (Exception ex)
            {
                Log.Write("ApplyChatAppearance failed (non-fatal): " + ex.Message);
            }
        }

        private static void SetOrClear(DependencyObject target, DependencyProperty property, object? value)
        {
            if (value == null) target.ClearValue(property); else target.SetValue(property, value);
        }

        private void ApplyBubbleAppearance(ChatMessage message, MessageVisuals visuals)
        {
            if (visuals.Bubble == null || message.IsError) return;
            ResolvedChatAppearance? a = _appearance;
            string? color = message.Role == "user" ? a?.UserBubble : a?.AssistantBubble;
            SetOrClear(visuals.Bubble, Border.BackgroundProperty, BrushOf(color));
        }

        /// <summary>User text carries its own style colour, so the user's text colour is set on the TextBlock itself.</summary>
        private void ApplyUserTextAppearance(Panel content)
        {
            Brush? foreground = BrushOf(_appearance?.UserText);
            double? size = _appearance?.FontSize;
            foreach (TextBlock text in content.Children.OfType<TextBlock>())
            {
                SetOrClear(text, TextBlock.ForegroundProperty, foreground);
                SetOrClear(text, TextBlock.FontSizeProperty, size is { } s ? s : null);
                SetOrClear(text, TextBlock.FontFamilyProperty, _appearance?.FontFamily is { } family ? new FontFamily(family) : null);
            }
        }

        private void AppearanceButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout { Content = BuildAppearanceFlyoutContent() };
            flyout.ShowAt((FrameworkElement)sender);
        }

        private UIElement BuildAppearanceFlyoutContent()
        {
            ChatAppearanceTheme theme = ChatAppearanceState.Load() ?? new ChatAppearanceTheme();
            var panel = new StackPanel { Spacing = 8, MinWidth = 320, MaxWidth = 400 };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 12 };
            AutomationProperties.SetAutomationId(status, "Chat_Appearance_Status_Text");

            if (HighContrastActive())
                panel.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Text = T("Windows High Contrast is on, so custom chat colours are not applied.", "Il contrasto elevato di Windows è attivo, quindi i colori personalizzati della chat non vengono applicati.")
                });

            TextBox Hex(string header, string id, string? value)
            {
                var box = new TextBox { Header = header, PlaceholderText = "#RRGGBB", MaxLength = 7, Text = value ?? string.Empty };
                AutomationProperties.SetAutomationId(box, id);
                return box;
            }
            TextBox userBubble = Hex(T("Your bubble", "Il tuo fumetto"), "Chat_Appearance_UserBubble_Box", theme.UserBubbleColor);
            TextBox userText = Hex(T("Your text", "Il tuo testo"), "Chat_Appearance_UserText_Box", theme.UserTextColor);
            TextBox assistantBubble = Hex(T("Reply bubble", "Fumetto della risposta"), "Chat_Appearance_AssistantBubble_Box", theme.AssistantBubbleColor);
            TextBox assistantText = Hex(T("Reply text", "Testo della risposta"), "Chat_Appearance_AssistantText_Box", theme.AssistantTextColor);
            TextBox background = Hex(T("Background (all chats)", "Sfondo (tutte le chat)"), "Chat_Appearance_Background_Box", theme.BackgroundColor);
            TextBox chatBackground = Hex(T("Background of this chat only", "Sfondo solo di questa chat"), "Chat_Appearance_ChatBackground_Box",
                ChatSession.Current.Conversation?.BackgroundColor);

            var font = new ComboBox { Header = T("Font", "Carattere"), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(font, "Chat_Appearance_Font_ComboBox");
            font.Items.Add(new ComboBoxItem { Content = T("PaRuX default", "Predefinito di PaRuX"), Tag = null });
            foreach (string name in ChatAppearancePolicy.AllowedFonts) font.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            font.SelectedIndex = theme.FontFamily == null ? 0
                : Math.Max(0, ChatAppearancePolicy.AllowedFonts.ToList().FindIndex(f => f.Equals(theme.FontFamily, StringComparison.OrdinalIgnoreCase)) + 1);
            var size = new NumberBox
            {
                Header = T("Text size (empty = default)", "Dimensione del testo (vuoto = predefinita)"),
                Minimum = ChatAppearancePolicy.MinFontSize, Maximum = ChatAppearancePolicy.MaxFontSize,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = theme.FontSize ?? double.NaN
            };
            AutomationProperties.SetAutomationId(size, "Chat_Appearance_FontSize_Box");

            string? Blank(TextBox b) => string.IsNullOrWhiteSpace(b.Text) ? null : b.Text.Trim();
            ChatAppearanceTheme Collect() => new()
            {
                Name = string.IsNullOrWhiteSpace(theme.Name) ? "Custom" : theme.Name,
                UserBubbleColor = Blank(userBubble), UserTextColor = Blank(userText),
                AssistantBubbleColor = Blank(assistantBubble), AssistantTextColor = Blank(assistantText),
                BackgroundColor = Blank(background),
                FontFamily = (font.SelectedItem as ComboBoxItem)?.Tag as string,
                FontSize = double.IsNaN(size.Value) ? null : size.Value
            };

            var apply = new Button { Content = T("Apply", "Applica") };
            AutomationProperties.SetAutomationId(apply, "Chat_Appearance_Apply_Button");
            apply.Click += (_, _) =>
            {
                ChatAppearanceTheme candidate = Collect();
                IReadOnlyList<string> errors = ChatAppearancePolicy.Validate(candidate);
                string? chatBg = Blank(chatBackground);
                if (chatBg != null && !ChatAppearancePolicy.IsValidColor(chatBg)) errors = errors.Append("chat background must be #RRGGBB").ToList();
                if (errors.Count > 0) { status.Text = T("Not applied: ", "Non applicato: ") + string.Join("; ", errors); return; }
                ChatAppearanceState.Save(candidate);
                if (ChatSession.Current.Conversation is { } conversation && !string.Equals(conversation.BackgroundColor, chatBg, StringComparison.OrdinalIgnoreCase))
                {
                    conversation.BackgroundColor = chatBg?.ToUpperInvariant();
                    ChatSession.Current.Persist();
                }
                ApplyChatAppearance();
                status.Text = HighContrastActive()
                    ? T("Saved. It will show when Windows High Contrast is off.", "Salvato. Si vedrà quando il contrasto elevato di Windows sarà disattivato.")
                    : T("Applied.", "Applicato.");
            };
            var reset = new Button { Content = T("Reset to PaRuX look", "Ripristina l'aspetto di PaRuX") };
            AutomationProperties.SetAutomationId(reset, "Chat_Appearance_Reset_Button");
            reset.Click += (_, _) =>
            {
                ChatAppearanceState.Save(null);
                if (ChatSession.Current.Conversation is { BackgroundColor: not null } conversation)
                {
                    conversation.BackgroundColor = null;
                    ChatSession.Current.Persist();
                }
                foreach (TextBox b in new[] { userBubble, userText, assistantBubble, assistantText, background, chatBackground }) b.Text = string.Empty;
                font.SelectedIndex = 0; size.Value = double.NaN;
                ApplyChatAppearance();
                status.Text = T("Back to the default look.", "Tornato all'aspetto predefinito.");
            };
            var export = new Button { Content = T("Export theme…", "Esporta tema…") };
            AutomationProperties.SetAutomationId(export, "Chat_Appearance_Export_Button");
            export.Click += async (_, _) =>
            {
                ChatAppearanceTheme candidate = Collect();
                IReadOnlyList<string> errors = ChatAppearancePolicy.Validate(candidate);
                if (errors.Count > 0) { status.Text = T("Fix first: ", "Correggi prima: ") + string.Join("; ", errors); return; }
                try
                {
                    var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "parux-chat-theme" };
                    picker.FileTypeChoices.Add("PaRuX chat theme", new System.Collections.Generic.List<string> { ".json" });
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                    StorageFile? file = await picker.PickSaveFileAsync();
                    if (file == null) return;
                    await FileIO.WriteTextAsync(file, ChatAppearancePolicy.Export(candidate));
                    status.Text = T("Theme exported.", "Tema esportato.");
                }
                catch (Exception ex) { status.Text = T("Export failed: ", "Esportazione non riuscita: ") + ex.Message; }
            };
            var import = new Button { Content = T("Import theme…", "Importa tema…") };
            AutomationProperties.SetAutomationId(import, "Chat_Appearance_Import_Button");
            import.Click += async (_, _) =>
            {
                try
                {
                    var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                    picker.FileTypeFilter.Add(".json");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                    StorageFile? file = await picker.PickSingleFileAsync();
                    if (file == null) return;
                    var props = await file.GetBasicPropertiesAsync();
                    if (props.Size > ChatAppearanceTheme.MaxJsonBytes)
                    {
                        status.Text = T("Not imported: the file is larger than 4 KB.", "Non importato: il file supera 4 KB.");
                        return;
                    }
                    ChatAppearanceImportResult result = ChatAppearancePolicy.Import(await FileIO.ReadTextAsync(file));
                    if (!result.Ok || result.Theme == null)
                    {
                        status.Text = T("Not imported: ", "Non importato: ") + string.Join("; ", result.Errors);
                        return;
                    }
                    ChatAppearanceState.Save(result.Theme);
                    ApplyChatAppearance();
                    status.Text = T("Theme imported and applied. Re-open this panel to see its values.", "Tema importato e applicato. Riapri questo pannello per vederne i valori.");
                }
                catch (Exception ex) { status.Text = T("Import failed: ", "Importazione non riuscita: ") + ex.Message; }
            };

            foreach (UIElement element in new UIElement[]
            {
                new TextBlock { Text = T("Colours are #RRGGBB. Text that would be hard to read is replaced by a readable colour. No CSS or links are accepted.",
                    "I colori sono #RRGGBB. Il testo poco leggibile viene sostituito da un colore leggibile. Non sono accettati CSS né link."), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 },
                userBubble, userText, assistantBubble, assistantText, background, chatBackground, font, size,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, reset } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { export, import } },
                status
            })
                panel.Children.Add(element);
            return new ScrollViewer { Content = panel, MaxHeight = 560 };
        }
    }
}
