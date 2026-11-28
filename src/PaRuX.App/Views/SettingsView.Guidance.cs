using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// "What this does" headers, starting templates and plain-language tooltips for the Settings sections that host an
    /// advanced workspace (Model Compare, Group Chat). Only adds content to named hosts; no existing control changes.
    /// </summary>
    public sealed partial class SettingsView
    {
        private void ApplyAdvancedGuidance()
        {
            SettingsContentPanel.Children.Insert(0, GuidanceUi.Intro(WorkspaceGuideIds.Settings));
            CompareGuidanceHost.Children.Add(GuidanceUi.Intro(WorkspaceGuideIds.Compare));
            CompareGuidanceHost.Children.Add(GuidanceUi.TemplateRow("Settings_Compare_Template",
                WorkspaceTemplateCatalog.For(TemplateWorkspaces.Compare), t => FillPrompt(ComparePromptBox, t)));
            GuidanceUi.Help(CompareModelACombo, ControlHelpIds.CompareModels);
            GuidanceUi.Help(CompareModelBCombo, ControlHelpIds.CompareModels);

            GroupChatGuidanceHost.Children.Add(GuidanceUi.Intro(WorkspaceGuideIds.GroupChat));
            GroupChatGuidanceHost.Children.Add(GuidanceUi.TemplateRow("Settings_GroupChat_Template",
                WorkspaceTemplateCatalog.For(TemplateWorkspaces.GroupChat), t => FillPrompt(GroupChatPromptBox, t)));
            foreach (ComboBox combo in new[] { GroupChatModel1Combo, GroupChatModel2Combo, GroupChatModel3Combo, GroupChatModel4Combo })
                GuidanceUi.Help(combo, ControlHelpIds.GroupSpeakers);
        }

        private static void FillPrompt(TextBox box, WorkspaceTemplate template)
        {
            box.Text = template.Prompt.Pick();
            box.Focus(FocusState.Programmatic);
        }
    }
}
