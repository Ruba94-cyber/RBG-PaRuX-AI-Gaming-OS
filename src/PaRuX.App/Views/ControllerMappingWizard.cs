using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>One thing to map: a Windows-visible label, the box that receives the index, and whether it is a button or an axis.</summary>
    /// <remarks>
    /// <see cref="Control"/> selects what is detected (button, axis, stick-up, trigger as axis OR button, D-pad as hat, axes or buttons).
    /// <see cref="Target"/> (optional) receives the detected index directly; <see cref="OnDetected"/> (optional) receives the full detection
    /// and returns how many of the FOLLOWING steps to skip (a hat covers all four D-pad directions at once).
    /// </remarks>
    internal sealed record MappingStep(string English, string Italian, NumberBox? Target, bool IsButton)
    {
        public WizardControl Control { get; init; } = IsButton ? WizardControl.Button : WizardControl.Axis;
        public Func<DetectedControl, int>? OnDetected { get; init; }
    }

    /// <summary>
    /// Guided mapping in the style of XOutput and Steam's auto-configure: "Press A on your controller", then B, and so on. The index of
    /// the control the owner touches is written into the mapping box for that step; nothing is saved until the owner presses Save
    /// mapping. It only reads the pad (no virtual pad, nothing sent to games) and stops when finished, cancelled or unloaded.
    /// </summary>
    internal sealed class ControllerMappingWizard : StackPanel
    {
        private readonly IReadOnlyList<MappingStep> _steps;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly TextBlock _prompt = new() { FontSize = 18, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _progress = new() { Opacity = 0.7 };
        private readonly Button _skip;
        private readonly Button _cancel;
        private readonly Button _usePreset;
        private PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.PadMappingPreset? _preset;
        private Action<PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.PadMappingPreset>? _applyPreset;
        private WindowsRawGamepadSource? _source;
        private int _index;
        private PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.RawGamepadState? _baseline;
        private int _stableTicks;
        private DetectedControl _candidate;
        private DateTime _pauseUntil;

        public ControllerMappingWizard(IReadOnlyList<MappingStep> steps)
        {
            _steps = steps;
            Spacing = 8;
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(this, "GameDevices_Wizard_Panel");
            AutomationProperties.SetAutomationId(_prompt, "GameDevices_Wizard_Prompt_Text");
            _skip = new Button { Content = T("Skip this one", "Salta questo") };
            _cancel = new Button { Content = T("Stop", "Interrompi") };
            AutomationProperties.SetAutomationId(_skip, "GameDevices_Wizard_Skip_Button");
            AutomationProperties.SetAutomationId(_cancel, "GameDevices_Wizard_Cancel_Button");
            _skip.Click += (_, _) => Advance(null);
            _cancel.Click += (_, _) => Finish(T("Stopped. Nothing is saved until you press Save mapping.", "Interrotto. Nulla viene salvato finché non premi Salva mappatura."));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _usePreset = new Button { Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(_usePreset, "GameDevices_Wizard_Preset_Button");
            _usePreset.Click += (_, _) =>
            {
                if (_preset is null || _applyPreset is null) return;
                _applyPreset(_preset);
                Finish(T("Preset \"", "Preset \"") + _preset.Name + T("\" loaded into the mapping boxes. It is a starting point, not yet verified: turn on Live view or Read once, press the buttons, fix any number that is wrong, then press Save mapping.",
                    "\" caricato nelle caselle di mappatura. È un punto di partenza non ancora verificato: attiva la Vista dal vivo o Leggi una volta, premi i pulsanti, correggi i numeri sbagliati, poi premi Salva mappatura."));
            };
            row.Children.Add(_usePreset);
            row.Children.Add(_skip);
            row.Children.Add(_cancel);
            Children.Add(_progress);
            Children.Add(_prompt);
            Children.Add(row);
            _timer.Tick += (_, _) => Poll();
            Unloaded += (_, _) => Stop();
        }

        private static string T(string en, string it) =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        /// <summary>Raised with a closing message when the wizard ends (finished, stopped or the pad vanished).</summary>
        public event Action<string>? Ended;

        /// <param name="deviceId">The raw controller to read.</param>
        /// <param name="preset">A built-in proposal for this pad's VID/PID, or null. When present, "Use preset" fills the mapping boxes
        /// with it in one click; it is never saved or sent anywhere, so the owner still confirms (Live view, Save mapping).</param>
        /// <param name="applyPreset">Writes the preset into the mapping boxes.</param>
        public bool Begin(string? deviceId, PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.PadMappingPreset? preset = null,
            Action<PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad.PadMappingPreset>? applyPreset = null)
        {
            Stop();
            _source = string.IsNullOrWhiteSpace(deviceId) ? null : WindowsRawGamepadSource.TryOpen(deviceId);
            if (_source is null) return false;
            _preset = preset;
            _applyPreset = applyPreset;
            _usePreset.Visibility = preset is not null && applyPreset is not null ? Visibility.Visible : Visibility.Collapsed;
            _usePreset.Content = preset is null ? "" : T("Use preset: ", "Usa preset: ") + preset.Name + T(" (unverified)", " (non verificato)");
            _index = 0;
            Visibility = Visibility.Visible;
            ResetStep();
            _timer.Start();
            return true;
        }

        public void Stop()
        {
            _timer.Stop();
            _source = null;
            Visibility = Visibility.Collapsed;
        }

        private void Finish(string message)
        {
            Stop();
            Ended?.Invoke(message);
        }

        private void ResetStep()
        {
            MappingStep step = _steps[_index];
            _progress.Text = T("Step ", "Passo ") + (_index + 1) + " / " + _steps.Count;
            string what = T(step.English, step.Italian);
            _prompt.Text = step.Control switch
            {
                WizardControl.Button => T("Press and release ", "Premi e rilascia ") + what + T(" on your controller", " sul tuo controller"),
                WizardControl.Trigger => T("Pull ", "Premi a fondo ") + what + T(" all the way, then let it go (analogue or digital, both work)", ", poi rilascialo (analogico o digitale, vanno bene entrambi)"),
                WizardControl.Dpad => T("Press ", "Premi ") + what + T(" on the D-pad (hat, axes or buttons are detected automatically)", " sulla croce direzionale (croce, assi o pulsanti vengono riconosciuti da soli)"),
                _ => T("Move ", "Muovi ") + what + T(" all the way, then let it go", " fino in fondo, poi lascialo")
            };
            _stableTicks = 0;
            _candidate = default;
            _pauseUntil = DateTime.UtcNow.AddMilliseconds(600);   // let the previous press settle before listening again
            _baseline = null;
        }

        private void Advance(DetectedControl? assigned)
        {
            int skip = 0;
            if (assigned is { } found && !found.IsNone)
            {
                MappingStep step = _steps[_index];
                if (step.Target is not null) step.Target.Value = found.Index;
                if (step.OnDetected is not null) skip = Math.Max(0, step.OnDetected(found));
            }
            _index += 1 + skip;
            if (_index >= _steps.Count)
            {
                Finish(T("All steps done. Check the numbers, then press Save mapping.", "Tutti i passi completati. Controlla i numeri, poi premi Salva mappatura."));
                return;
            }
            ResetStep();
        }

        private void Poll()
        {
            if (_source is null || !_source.TryRead(out var state) || state is null)
            {
                Finish(T("The controller was disconnected.", "Il controller è stato scollegato."));
                return;
            }
            if (_baseline is null || DateTime.UtcNow < _pauseUntil)
            {
                _baseline = state;
                return;
            }
            DetectedControl hit = RawMappingWizardModel.Detect(_steps[_index].Control, _baseline, state);
            if (hit.IsNone) { _stableTicks = 0; _candidate = default; return; }
            // The same control for three readings in a row (about 120 ms) is a deliberate press, not contact noise.
            if (RawMappingWizardModel.SameControl(_candidate, hit)) _stableTicks++; else { _candidate = hit; _stableTicks = 1; }
            if (_stableTicks >= 3) Advance(hit);
        }
    }
}
