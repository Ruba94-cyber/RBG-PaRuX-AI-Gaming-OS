using Microsoft.UI.Xaml;

namespace PaRuX.Views
{
    /// <summary>Entry points the window's palette and accelerators use; each only opens the same control a mouse user would click.</summary>
    public sealed partial class ChatView
    {
        public void QuickOpenAttachPicker() => Attach_Click(AttachButton, new RoutedEventArgs());

        public void QuickOpenModelPicker()
        {
            ModelSelector.Focus(FocusState.Keyboard);
            if (ModelSelector.IsEnabled) ModelSelector.IsDropDownOpen = true;
        }

        public void QuickOpenGenerationSettings() => SamplingButton_Click(SamplingButton, new RoutedEventArgs());

        public void QuickFocusComposer() => InputBox.Focus(FocusState.Keyboard);
    }
}
