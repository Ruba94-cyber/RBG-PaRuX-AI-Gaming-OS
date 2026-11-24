using PaRuX.Intelligence;

namespace PaRuX.Services
{
    /// <summary>Loads/saves the owner's chat appearance theme. Stored text is re-validated on every read, so a hand-edited settings file can never inject anything the importer would refuse.</summary>
    public static class ChatAppearanceState
    {
        public static ChatAppearanceTheme? Load()
        {
            string? json = SettingsStore.Current.Data.ChatAppearanceThemeJson;
            if (string.IsNullOrWhiteSpace(json)) return null;
            ChatAppearanceImportResult result = ChatAppearancePolicy.Import(json);
            return result.Ok ? result.Theme : null;
        }

        public static void Save(ChatAppearanceTheme? theme)
        {
            SettingsStore.Current.Data.ChatAppearanceThemeJson = theme == null || theme.IsEmpty ? null : ChatAppearancePolicy.Export(theme);
            SettingsStore.Current.Save();
        }
    }
}
