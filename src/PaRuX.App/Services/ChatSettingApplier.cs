using System;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record ChatSettingApplyResult(bool Ok, string Error);

    /// <summary>
    /// Applies ONE owner-confirmed setting from the chat card. Called only from the Apply button. It re-checks the allow-list
    /// (and Corporate locks) and then uses the same code paths Settings uses; nothing outside ChatSettingsAllowList is reachable.
    /// </summary>
    public static class ChatSettingApplier
    {
        public static async Task<ChatSettingApplyResult> ApplyAsync(ChatSettingProposalDto proposal, CancellationToken ct)
        {
            if (!ChatSettingsAllowList.TryValidate(proposal.Key, proposal.Value, CorporateMode.IsActive, out ChatSettingChange? change, out string error))
                return new ChatSettingApplyResult(false, error);
            try
            {
                switch (change!.Key)
                {
                    case "theme":
                        SettingsStore.Current.Data.Theme = change.Value switch { "dark" => "Dark", "light" => "Light", _ => "System" };
                        SettingsStore.Current.Save();
                        App.ApplyTheme();
                        break;
                    case "language":
                        string language = UiLanguage.Normalize(change.Value);
                        SettingsStore.Current.Data.UiLanguage = language;
                        SettingsStore.Current.Save();
                        UiLanguage.Apply(language);
                        break;
                    case "interface":
                        if (App.MainWin == null || !App.MainWin.TrySetInterfaceMode(change.Value == "advanced"))
                            return new ChatSettingApplyResult(false, "The Simple/Advanced switch is not available right now.");
                        break;
                    case "textProfile":
                        await AiProfileClient.Current.SetTextProfileAsync(change.Value.ToUpperInvariant(), ct);
                        break;
                    case "webSearch":
                        await InternetClient.Current.SetEnabledAsync(change.Value == "on", ct);
                        break;
                    case "voiceAutoSend":
                        SettingsStore.Current.Data.SendVoiceMessagesAutomatically = change.Value == "on";
                        SettingsStore.Current.Save();
                        break;
                    default:
                        return new ChatSettingApplyResult(false, "Unknown setting.");
                }
                return new ChatSettingApplyResult(true, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("ChatSettingApplier failed: " + ex.Message);
                return new ChatSettingApplyResult(false, ex.Message);
            }
        }
    }
}
