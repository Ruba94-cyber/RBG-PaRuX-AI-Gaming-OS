using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        /// <summary>
        /// Coding task starters (Fix a bug, Explain this repo, ...). Choosing one only puts the request in the message box so it
        /// can be edited; nothing is sent, and the usual code-folder choice and approval cards still apply.
        /// </summary>
        private void InitCodingTemplates()
        {
            var flyout = new MenuFlyout();
            foreach (WorkspaceTemplate template in WorkspaceTemplateCatalog.For(TemplateWorkspaces.Coding))
            {
                var item = new MenuFlyoutItem { Text = template.Title.Pick() };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "Chat_CodeTemplate_" + template.Id.Replace('.', '_'));
                ToolTipService.SetToolTip(item, template.Description.Pick());
                WorkspaceTemplate captured = template;
                item.Click += (_, _) =>
                {
                    InputBox.Text = captured.Prompt.Pick();
                    InputBox.Focus(FocusState.Programmatic);
                };
                flyout.Items.Add(item);
            }
            CodeTemplatesButton.Flyout = flyout;
        }
    }
}
