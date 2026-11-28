using System;

namespace PaRuX.Services
{
    public enum WorkspaceErrorKind
    {
        EngineNotRunning, OutOfMemory, NoModelInstalled, ModelNotLoaded, McpCommandMissing, FolderMissing,
        InvalidJson, TimedOut, CoreUnreachable, MicrophoneUnavailable, WorkflowNodeMissing, GamingModeActive
    }

    /// <summary>
    /// Plain-language failures for the advanced workspaces (media, automations, MCP, voice, knowledge). Reuses
    /// <see cref="SetupErrorExplainer"/> for disk, network, driver, port and permission problems first, then adds the
    /// failures that are specific to these workspaces. Like its sibling it only claims a cause the raw text clearly names.
    /// </summary>
    public static class WorkspaceErrorExplainer
    {
        private static bool Has(string s, string v) => s.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0;

        public static WorkspaceErrorKind? Classify(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string m = raw!;
            if (Has(m, "gaming mode") && (Has(m, "active") || Has(m, "paused") || Has(m, "refus"))) return WorkspaceErrorKind.GamingModeActive;
            if (Has(m, "out of memory") || Has(m, "CUDA out of memory") || Has(m, "OutOfMemory") || Has(m, "failed to allocate")
                || Has(m, "insufficient memory") || Has(m, "VRAM")) return WorkspaceErrorKind.OutOfMemory;
            if (Has(m, "missing node") || Has(m, "node type") && Has(m, "not found") || Has(m, "custom node") || Has(m, "missing_node"))
                return WorkspaceErrorKind.WorkflowNodeMissing;
            if (Has(m, "comfyui") && (Has(m, "not running") || Has(m, "unreachable") || Has(m, "not reachable") || Has(m, "connection refused")
                || Has(m, "not ready") || Has(m, "not installed"))) return WorkspaceErrorKind.EngineNotRunning;
            if (Has(m, "no model") && (Has(m, "installed") || Has(m, "found") || Has(m, "available"))) return WorkspaceErrorKind.NoModelInstalled;
            if (Has(m, "no checkpoint") || Has(m, "checkpoint not found") || Has(m, "model file not found")) return WorkspaceErrorKind.NoModelInstalled;
            if (Has(m, "model is not loaded") || Has(m, "no model loaded") || Has(m, "runtime is not running") || Has(m, "no active model"))
                return WorkspaceErrorKind.ModelNotLoaded;
            if ((Has(m, "npx") || Has(m, "uvx") || Has(m, "node") || Has(m, "command")) && (Has(m, "not found") || Has(m, "not recognized") || Has(m, "cannot find")))
                return WorkspaceErrorKind.McpCommandMissing;
            if (Has(m, "DirectoryNotFound") || Has(m, "could not find a part of the path") || Has(m, "folder does not exist") || Has(m, "folder was not found")
                || Has(m, "directory not found")) return WorkspaceErrorKind.FolderMissing;
            if (Has(m, "JsonException") || Has(m, "invalid json") || Has(m, "is invalid start of a value") || Has(m, "unexpected character")
                || (Has(m, "json") && (Has(m, "malformed") || Has(m, "parse")))) return WorkspaceErrorKind.InvalidJson;
            if (Has(m, "microphone") && (Has(m, "not found") || Has(m, "unavailable") || Has(m, "no device") || Has(m, "denied") || Has(m, "in use")))
                return WorkspaceErrorKind.MicrophoneUnavailable;
            if (Has(m, "no capture device") || Has(m, "no audio input")) return WorkspaceErrorKind.MicrophoneUnavailable;
            if (Has(m, "timed out") || Has(m, "timeout") || Has(m, "TaskCanceledException")) return WorkspaceErrorKind.TimedOut;
            if (Has(m, "actively refused") || Has(m, "connection refused") || Has(m, "127.0.0.1") && Has(m, "refused")
                || Has(m, "core is not reachable") || Has(m, "paRuX core") && Has(m, "unreachable")) return WorkspaceErrorKind.CoreUnreachable;
            return null;
        }

        public static FriendlyError? Explain(string? raw, bool italian)
        {
            FriendlyError? setup = SetupErrorExplainer.Explain(raw, italian);
            if (setup != null) return setup;
            WorkspaceErrorKind? kind = Classify(raw);
            if (kind == null) return null;
            return kind.Value switch
            {
                WorkspaceErrorKind.EngineNotRunning => Pair(italian,
                    "The image and video engine isn't running.", "Start or install it from Runtime, then try again.",
                    "Il motore per immagini e video non è in esecuzione.", "Avvialo o installalo da Runtime, poi riprova."),
                WorkspaceErrorKind.OutOfMemory => Pair(italian,
                    "Your graphics card ran out of memory.", "Try a smaller size or a shorter clip, close other apps that use the graphics card, and try again.",
                    "La scheda video ha esaurito la memoria.", "Prova una dimensione più piccola o una clip più corta, chiudi le altre app che usano la scheda video e riprova."),
                WorkspaceErrorKind.NoModelInstalled => Pair(italian,
                    "There is no model installed for this job yet.", "Open Model Hub and install a recommended one.",
                    "Non c'è ancora un modello installato per questo lavoro.", "Apri Model Hub e installa uno di quelli consigliati."),
                WorkspaceErrorKind.ModelNotLoaded => Pair(italian,
                    "No model is loaded right now.", "Open Runtime and load a model, or send a chat message to load one automatically.",
                    "In questo momento nessun modello è caricato.", "Apri Runtime e carica un modello, oppure invia un messaggio in chat per caricarlo da solo."),
                WorkspaceErrorKind.McpCommandMissing => Pair(italian,
                    "PaRuX couldn't start that tool because its program isn't installed.", "Install Node.js (for presets that use npx) or check the program name, then add the tool again.",
                    "PaRuX non è riuscito ad avviare lo strumento perché il suo programma non è installato.", "Installa Node.js (per i preset che usano npx) o controlla il nome del programma, poi aggiungi di nuovo lo strumento."),
                WorkspaceErrorKind.FolderMissing => Pair(italian,
                    "That folder doesn't exist any more.", "Choose another folder and try again.",
                    "Quella cartella non esiste più.", "Scegli un'altra cartella e riprova."),
                WorkspaceErrorKind.InvalidJson => Pair(italian,
                    "The advanced settings text isn't valid.", "Pick a template to fill it in again, or check commas and quotes.",
                    "Il testo delle impostazioni avanzate non è valido.", "Scegli un modello per compilarlo di nuovo, oppure controlla virgole e virgolette."),
                WorkspaceErrorKind.TimedOut => Pair(italian,
                    "This took longer than expected and was stopped.", "Try again, or use a smaller job. A bigger model or a busy computer can be slow.",
                    "Ci ha messo più del previsto ed è stato fermato.", "Riprova, oppure usa un lavoro più piccolo. Un modello grande o un computer occupato possono essere lenti."),
                WorkspaceErrorKind.CoreUnreachable => Pair(italian,
                    "PaRuX's background service isn't answering.", "Wait a few seconds and try again; if it keeps happening, restart PaRuX.",
                    "Il servizio di PaRuX in background non risponde.", "Attendi qualche secondo e riprova; se continua, riavvia PaRuX."),
                WorkspaceErrorKind.MicrophoneUnavailable => Pair(italian,
                    "PaRuX can't use the microphone.", "Plug one in, allow microphone access in Windows privacy settings, and pick it in the list.",
                    "PaRuX non riesce a usare il microfono.", "Collegane uno, consenti l'accesso al microfono nelle impostazioni privacy di Windows e sceglilo dall'elenco."),
                WorkspaceErrorKind.WorkflowNodeMissing => Pair(italian,
                    "This workflow needs a ComfyUI add-on that isn't installed.", "Open the workflow's details to see which add-on is missing, or pick another workflow.",
                    "Questo workflow richiede un componente aggiuntivo di ComfyUI che non è installato.", "Apri i dettagli del workflow per vedere quale manca, oppure scegli un altro workflow."),
                _ => Pair(italian,
                    "Gaming Mode is on, so this job is paused to keep your game smooth.", "Leave Gaming Mode and try again.",
                    "La Modalità Gaming è attiva, quindi il lavoro è in pausa per non rallentare il gioco.", "Esci dalla Modalità Gaming e riprova.")
            };
        }

        private static FriendlyError Pair(bool italian, string causeEn, string actionEn, string causeIt, string actionIt) =>
            italian ? new FriendlyError(causeIt, actionIt) : new FriendlyError(causeEn, actionEn);

        /// <summary>
        /// One line for a status bar: "<prefix> <cause> <next action>" when the raw text is understood, otherwise the original
        /// prefix and raw text unchanged so exact messages (such as "An image prompt is required.") stay as they are.
        /// </summary>
        public static string StatusLine(string prefix, string? raw, bool italian)
        {
            FriendlyError? friendly = Explain(raw, italian);
            return friendly == null ? prefix + raw : prefix + friendly.Line;
        }
    }
}
