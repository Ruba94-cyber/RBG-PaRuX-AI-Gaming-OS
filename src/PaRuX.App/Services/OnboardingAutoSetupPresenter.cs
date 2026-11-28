using System;

namespace PaRuX.Services
{
    /// <summary>What the "Set up automatically" card shows for one state. Pure, so tests can pin it.</summary>
    public sealed record AutoSetupViewModel(
        string Headline,
        string Detail,
        string PrimaryLabel,
        bool PrimaryEnabled,
        bool ShowCancel,
        bool ShowRetry,
        bool ShowSkip,
        bool ShowProgress,
        string? TechnicalDetails = null);

    public static class OnboardingAutoSetupPresenter
    {
        public static AutoSetupViewModel Present(AutoSetupState state, bool italian)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (italian) return PresentItalian(state);
            string message = state.Message ?? string.Empty;
            string runtime = string.IsNullOrWhiteSpace(state.RuntimeNote) ? string.Empty : " " + state.RuntimeNote;

            return state.Phase switch
            {
                AutoSetupPhase.NotStarted => new AutoSetupViewModel(
                    "Set up PaRuX in one step",
                    "PaRuX checks your PC, picks an AI model that fits it and tries a first chat to make sure everything works. "
                    + "Nothing is downloaded until you have reviewed the model's licence.",
                    "Set up automatically", true, false, false, false, false),
                AutoSetupPhase.CheckingHardware or AutoSetupPhase.CheckingRuntime or AutoSetupPhase.SelectingModel
                    or AutoSetupPhase.AssigningRoles or AutoSetupPhase.VerifyingChat => new AutoSetupViewModel(
                    StepTitle(state.Phase), message + runtime, "Setting up...", false, true, false, false, true),
                AutoSetupPhase.Downloading => new AutoSetupViewModel(
                    "Downloading " + (state.ModelTitle ?? "the model"),
                    message + runtime, "Setting up...", false, true, false, false, true),
                AutoSetupPhase.Ready => new AutoSetupViewModel(
                    "PaRuX is ready", message, "Start chatting", true, false, false, false, false),
                AutoSetupPhase.NoModelFits => new AutoSetupViewModel(
                    "No recommended model fits this PC", message, "Try again", true, false, false, true, false),
                AutoSetupPhase.Failed => new AutoSetupViewModel(
                    "Setup stopped", FailedDetail(message), "Set up automatically", false, false, true, true, false,
                    SetupErrorExplainer.RedactForDetails(state.Message)),
                AutoSetupPhase.Cancelled => new AutoSetupViewModel(
                    "Setup cancelled", message, "Set up automatically", true, false, false, true, false),
                AutoSetupPhase.Skipped => new AutoSetupViewModel(
                    "Setup skipped", message, "Set up automatically", true, false, false, false, false),
                _ => new AutoSetupViewModel("Set up PaRuX", message, "Set up automatically", true, false, false, false, false)
            };
        }

        private static string FailedDetail(string message)
        {
            FriendlyError? friendly = SetupErrorExplainer.Explain(message, italian: false);
            return (friendly?.Line ?? message) + " You can try again, or skip this and choose a model yourself below.";
        }

        private static AutoSetupViewModel PresentItalian(AutoSetupState state)
        {
            string message = state.Message?.Trim() ?? string.Empty;
            FriendlyError? friendly = SetupErrorExplainer.Explain(message, italian: true);
            string failure = friendly != null
                ? friendly.Line + " Puoi riprovare, oppure saltare e scegliere tu un modello qui sotto."
                : RuntimeRecoveryGuidance.Classify(message) is not RuntimeRecoveryGuidance.Reason.Unknown
                    ? RuntimeRecoveryGuidance.Describe(message, italian: true)
                    : string.IsNullOrWhiteSpace(message)
                        ? "Consulta i controlli manuali qui sotto per verificare componenti, modello e chat."
                        : "Qualcosa non ha funzionato durante la configurazione. Puoi riprovare, oppure saltare e scegliere tu un modello qui sotto. I dettagli tecnici sono qui sotto.";

            return state.Phase switch
            {
                AutoSetupPhase.NotStarted => new AutoSetupViewModel(
                    "Configura PaRuX in un passaggio",
                    "PaRuX controlla il tuo PC, sceglie un modello IA adatto e prova una prima chat per assicurarsi che tutto funzioni. "
                    + "Non scarica nulla finché non hai controllato la licenza del modello.",
                    "Configura automaticamente", true, false, false, false, false),
                AutoSetupPhase.CheckingHardware or AutoSetupPhase.CheckingRuntime or AutoSetupPhase.SelectingModel
                    or AutoSetupPhase.AssigningRoles or AutoSetupPhase.VerifyingChat => new AutoSetupViewModel(
                    StepTitleItalian(state.Phase), ProgressDetailItalian(state.Phase),
                    "Configurazione...", false, true, false, false, true),
                AutoSetupPhase.Downloading => new AutoSetupViewModel(
                    "Download di " + (state.ModelTitle ?? "modello locale"),
                    "Scaricamento in corso. Il progresso viene mostrato qui sotto; puoi annullare se necessario.",
                    "Configurazione...", false, true, false, false, true),
                AutoSetupPhase.Ready => new AutoSetupViewModel(
                    "PaRuX è pronto",
                    "La verifica della chat locale è stata completata. Puoi iniziare.",
                    "Apri la chat", true, false, false, false, false),
                AutoSetupPhase.NoModelFits => new AutoSetupViewModel(
                    "Nessun modello consigliato è adatto a questo PC",
                    "PaRuX non forzerà il download di un modello troppo grande. "
                    + "Apri le opzioni manuali, valuta un modello più piccolo o verifica RAM, GPU e runtime.",
                    "Riprova", true, false, false, true, false),
                AutoSetupPhase.Failed => new AutoSetupViewModel(
                    "Configurazione interrotta",
                    failure, "Configura automaticamente", false, false, true, true, false,
                    SetupErrorExplainer.RedactForDetails(state.Message)),
                AutoSetupPhase.Cancelled => new AutoSetupViewModel(
                    "Configurazione annullata",
                    "La verifica non è completa. Puoi riprendere manualmente oppure ricominciare.",
                    "Configura automaticamente", true, false, false, true, false),
                AutoSetupPhase.Skipped => new AutoSetupViewModel(
                    "Configurazione saltata",
                    "I controlli manuali restano disponibili. La chat non è ancora stata verificata.",
                    "Configura automaticamente", true, false, false, false, false),
                _ => new AutoSetupViewModel(
                    "Configura PaRuX", message,
                    "Configura automaticamente", true, false, false, false, false)
            };
        }

        private static string StepTitleItalian(AutoSetupPhase phase) => phase switch
        {
            AutoSetupPhase.CheckingHardware => "Passaggio 1 di 5: controllo del tuo PC",
            AutoSetupPhase.CheckingRuntime => "Passaggio 2 di 5: preparazione del motore IA",
            AutoSetupPhase.SelectingModel => "Passaggio 3 di 5: scelta di un modello adatto al tuo PC",
            AutoSetupPhase.AssigningRoles => "Passaggio 4 di 5: preparazione del modello",
            AutoSetupPhase.VerifyingChat => "Passaggio 5 di 5: prima chat di prova",
            _ => "Configurazione in corso"
        };

        private static string ProgressDetailItalian(AutoSetupPhase phase) => phase switch
        {
            AutoSetupPhase.CheckingHardware => "Verifica delle risorse hardware disponibili.",
            AutoSetupPhase.CheckingRuntime => "Controllo dei runtime locali, senza modificare quelli gestiti dall'utente.",
            AutoSetupPhase.SelectingModel => "Ricerca di un modello compatibile con questo PC.",
            AutoSetupPhase.AssigningRoles => "Impostazione dei ruoli dei modelli installati.",
            AutoSetupPhase.VerifyingChat => "Controllo che l'assistente locale risponda correttamente.",
            _ => "Operazione in corso."
        };

        private static string StepTitle(AutoSetupPhase phase) => phase switch
        {
            AutoSetupPhase.CheckingHardware => "Step 1 of 5: checking your PC",
            AutoSetupPhase.CheckingRuntime => "Step 2 of 5: preparing the AI engine",
            AutoSetupPhase.SelectingModel => "Step 3 of 5: choosing a model that fits your PC",
            AutoSetupPhase.AssigningRoles => "Step 4 of 5: getting the model ready",
            AutoSetupPhase.VerifyingChat => "Step 5 of 5: trying a first chat",
            _ => "Setting up"
        };
    }
}
