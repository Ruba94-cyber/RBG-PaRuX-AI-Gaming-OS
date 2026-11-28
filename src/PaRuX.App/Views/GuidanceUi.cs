using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Small shared building blocks that make the advanced workspaces approachable: a "What this does" header, an empty
    /// state with one first action, one-click template chips, plain-language tooltips and a folded "Advanced" group.
    /// All text comes from the pure catalogs (<see cref="WorkspaceGuideCatalog"/>, <see cref="WorkspaceTemplateCatalog"/>,
    /// <see cref="ControlHelpCatalog"/>), which have their own English/Italian tests.
    /// </summary>
    internal static class GuidanceUi
    {
        private static string T(string en, string it) => UiText.Pick(en, it);

        /// <summary>Compact "What this does" card: one sentence and one example.</summary>
        public static UIElement Intro(string guideId)
        {
            WorkspaceGuide guide = WorkspaceGuideCatalog.Get(guideId);
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock
            {
                Text = T("What this does", "A cosa serve") + ": " + guide.WhatItDoes.Pick(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            });
            panel.Children.Add(new TextBlock
            {
                Text = T("Example", "Esempio") + ": " + guide.Example.Pick(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75
            });
            var card = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = panel
            };
            AutomationProperties.SetAutomationId(card, "Guide_" + guideId + "_Intro");
            AutomationProperties.SetName(card, guide.WhatItDoes.Pick());
            return card;
        }

        /// <summary>Empty-state message with exactly one clear first action. The caller decides when it is shown.</summary>
        public static StackPanel EmptyState(string guideId, Action firstAction, bool showMessage = true)
        {
            WorkspaceGuide guide = WorkspaceGuideCatalog.Get(guideId);
            var panel = new StackPanel { Spacing = 6 };
            if (showMessage) panel.Children.Add(new TextBlock { Text = guide.EmptyState.Pick(), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            var button = new Button { Content = guide.FirstAction.Pick() };
            AutomationProperties.SetAutomationId(button, "Guide_" + guideId + "_FirstAction");
            button.Click += (_, _) => firstAction();
            panel.Children.Add(button);
            AutomationProperties.SetAutomationId(panel, "Guide_" + guideId + "_EmptyState");
            return panel;
        }

        /// <summary>A wrapping row of template buttons. Clicking one only fills the form; it never starts anything.</summary>
        public static UIElement TemplateRow(string idPrefix, IEnumerable<WorkspaceTemplate> templates, Action<WorkspaceTemplate> apply)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = T("Start from a template", "Parti da un modello"), FontSize = 12, Opacity = 0.7 });
            var chips = new WrapRowPanel { ColumnSpacing = 6, RowSpacing = 6 };
            foreach (WorkspaceTemplate template in templates)
            {
                var chip = new Button { Content = template.Title.Pick(), Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
                AutomationProperties.SetAutomationId(chip, idPrefix + "_" + template.Id.Replace('.', '_'));
                ToolTipService.SetToolTip(chip, template.Description.Pick());
                WorkspaceTemplate captured = template;
                chip.Click += (_, _) => apply(captured);
                chips.Children.Add(chip);
            }
            row.Children.Add(chips);
            return row;
        }

        /// <summary>Plain-language tooltip and screen-reader help from the catalog. Existing names and automation ids are left alone.</summary>
        public static TElement Help<TElement>(TElement element, string controlId) where TElement : FrameworkElement
        {
            ControlHelp help = ControlHelpCatalog.Get(controlId);
            ToolTipService.SetToolTip(element, help.Tooltip.Pick());
            AutomationProperties.SetHelpText(element, help.Tooltip.Pick());
            return element;
        }

        /// <summary>Folds technical controls away; they stay one click and one keypress away.</summary>
        public static Expander Advanced(UIElement content, string automationId)
        {
            var expander = new Expander
            {
                Header = T("Advanced options", "Opzioni avanzate"),
                Content = content,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(expander, automationId);
            return expander;
        }
    }
}
