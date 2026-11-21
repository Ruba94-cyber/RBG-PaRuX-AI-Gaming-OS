using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        private bool _accentUiReady;

        /// <summary>Fills the accent picker (names in the chosen language) and selects the saved accent.</summary>
        private void LoadAccentUi(AppSettings s)
        {
            if (AccentCombo == null) return;
            if (!_accentUiReady)
            {
                _accentUiReady = true;
                foreach (AccentPreset preset in AccentPalette.Presets)
                    AccentCombo.Items.Add(new ComboBoxItem { Content = T(preset.EnglishName, preset.ItalianName), Tag = preset.Id });
                AccentCombo.Items.Add(new ComboBoxItem { Content = T("Custom color…", "Colore personalizzato…"), Tag = AccentPalette.CustomId });
            }
            SelectByTag(AccentCombo, AccentPalette.Find(s.AccentId).Id == AccentPalette.OrbitId && !string.Equals(s.AccentId, AccentPalette.OrbitId, StringComparison.OrdinalIgnoreCase)
                ? (string.Equals(s.AccentId, AccentPalette.CustomId, StringComparison.OrdinalIgnoreCase) ? AccentPalette.CustomId : AccentPalette.OrbitId)
                : AccentPalette.Find(s.AccentId).Id);
            AccentCustomBox.Text = s.AccentCustomHex ?? string.Empty;
            AccentCustomBox.Visibility = string.Equals(s.AccentId, AccentPalette.CustomId, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            UpdateAccentStatus(s);
        }

        private void UpdateAccentStatus(AppSettings s)
        {
            bool custom = string.Equals(s.AccentId, AccentPalette.CustomId, StringComparison.OrdinalIgnoreCase);
            if (custom && !AccentPalette.TryParse(s.AccentCustomHex, out _))
                AccentStatusText.Text = T("Enter a color like #2563EB. Until then the default PRX Orbit colors are used.",
                    "Inserisci un colore come #2563EB. Nel frattempo si usano i colori PRX Orbit predefiniti.");
            else if (AccentPalette.Resolve(s.AccentId, s.AccentCustomHex) == null)
                AccentStatusText.Text = T("PRX Orbit, the default PaRuX look. Pick another color to change buttons and highlights.",
                    "PRX Orbit, l'aspetto predefinito di PaRuX. Scegli un altro colore per cambiare pulsanti ed evidenziazioni.");
            else
                AccentStatusText.Text = T("Buttons and highlights use this color. Text on it is adjusted automatically so it stays easy to read.",
                    "Pulsanti ed evidenziazioni usano questo colore. Il testo sopra viene regolato automaticamente per restare leggibile.");
        }

        private void AccentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string? tag = SelectedTag(AccentCombo);
            if (tag == null) return;
            AppSettings s = SettingsStore.Current.Data;
            s.AccentId = tag;
            AccentCustomBox.Visibility = tag == AccentPalette.CustomId ? Visibility.Visible : Visibility.Collapsed;
            SettingsStore.Current.Save();
            App.ApplyTheme();
            UpdateAccentStatus(s);
        }

        private void AccentCustomBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            AppSettings s = SettingsStore.Current.Data;
            s.AccentCustomHex = AccentCustomBox.Text.Trim();
            // Applied only once it is a complete colour; a half-typed value never changes the look.
            if (AccentPalette.TryParse(s.AccentCustomHex, out _) || s.AccentCustomHex.Length == 0)
            {
                SettingsStore.Current.Save();
                App.ApplyTheme();
            }
            UpdateAccentStatus(s);
        }
    }
}
