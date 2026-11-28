using System;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>One friendly sentence about what went wrong plus one thing the person can do next.</summary>
    public sealed record FriendlyError(string Cause, string NextAction)
    {
        public string Line => Cause + " " + NextAction;
    }

    public enum SetupErrorKind { DiskFull, Network, HashMismatch, GpuDriver, PortInUse, AccessDenied }

    /// <summary>
    /// Turns the raw failure text of automatic setup into plain language (English or Italian) with a next action.
    /// Pure and conservative: it only claims a cause when the raw text clearly names it, otherwise it returns null so the caller
    /// can fall back to its generic guidance. The raw text itself belongs behind a "Details" disclosure (see <see cref="RedactForDetails"/>).
    /// </summary>
    public static class SetupErrorExplainer
    {
        private static bool Has(string s, string v) => s.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0;

        public static SetupErrorKind? Classify(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string m = raw!;

            if (Has(m, "address already in use") || Has(m, "only one usage of each socket") || Has(m, "WSAEADDRINUSE")
                || (Has(m, "port") && (Has(m, "already in use") || Has(m, "is occupied") || Has(m, "in use by another"))))
                return SetupErrorKind.PortInUse;

            if (Has(m, "no space left") || Has(m, "not enough space") || Has(m, "disk is full") || Has(m, "disk full")
                || Has(m, "ERROR_DISK_FULL") || Has(m, "0x80070070") || Has(m, "insufficient disk space")
                || Has(m, "not enough free disk") || Has(m, "free space"))
                return SetupErrorKind.DiskFull;

            if ((Has(m, "hash") || Has(m, "checksum") || Has(m, "sha256") || Has(m, "sha-256"))
                && (Has(m, "mismatch") || Has(m, "does not match") || Has(m, "didn't match") || Has(m, "failed") || Has(m, "invalid")))
                return SetupErrorKind.HashMismatch;

            if (Has(m, "access to the path") || Has(m, "access is denied") || Has(m, "access denied")
                || Has(m, "UnauthorizedAccess") || Has(m, "permission denied"))
                return SetupErrorKind.AccessDenied;

            if (Has(m, "CUDA_ERROR") || Has(m, "no CUDA-capable") || Has(m, "driver") || Has(m, "ggml_vulkan")
                || Has(m, "vkCreateInstance") || Has(m, "no GPU") || Has(m, "No sufficiently powerful GPU"))
                return SetupErrorKind.GpuDriver;

            if (Has(m, "No such host") || Has(m, "name or service not known") || Has(m, "network is unreachable")
                || Has(m, "unable to connect to the remote server") || Has(m, "error occurred while sending the request")
                || Has(m, "HttpRequestException") || Has(m, "SSL connection") || Has(m, "no internet")
                || (Has(m, "timed out") && (Has(m, "download") || Has(m, "connect") || Has(m, "network"))))
                return SetupErrorKind.Network;

            return null;
        }

        public static FriendlyError? Explain(string? raw, bool italian)
        {
            SetupErrorKind? kind = Classify(raw);
            if (kind == null) return null;
            return kind.Value switch
            {
                SetupErrorKind.DiskFull => italian
                    ? new FriendlyError("Non c'è abbastanza spazio libero sul disco.",
                        "Libera un po' di spazio (o scegli un altro disco nelle impostazioni di archiviazione) e riprova.")
                    : new FriendlyError("There isn't enough free disk space.",
                        "Free some space (or pick another drive in the storage settings) and try again."),
                SetupErrorKind.Network => italian
                    ? new FriendlyError("PaRuX non riesce a raggiungere internet.",
                        "Controlla la connessione e riprova: i download riprendono da dove si erano fermati.")
                    : new FriendlyError("PaRuX couldn't reach the internet.",
                        "Check your connection and try again; downloads resume where they stopped."),
                SetupErrorKind.HashMismatch => italian
                    ? new FriendlyError("Il file scaricato non ha superato il controllo di sicurezza, quindi PaRuX lo ha scartato.",
                        "Riprova; se succede ancora, scegli un altro modello.")
                    : new FriendlyError("The downloaded file didn't pass its safety check, so PaRuX discarded it.",
                        "Try again; if it keeps happening, choose a different model."),
                SetupErrorKind.GpuDriver => italian
                    ? new FriendlyError("PaRuX non riesce a usare la tua scheda video (driver mancante o non aggiornato).",
                        "Aggiorna i driver della scheda video, oppure continua con il processore scegliendo un modello più piccolo.")
                    : new FriendlyError("PaRuX couldn't use your graphics card (driver missing or out of date).",
                        "Update your graphics driver, or continue on the processor with a smaller model."),
                SetupErrorKind.PortInUse => italian
                    ? new FriendlyError("Un altro programma sta usando la porta di cui PaRuX ha bisogno.",
                        "Chiudi gli altri strumenti di IA locale (o riavvia PaRuX) e riprova.")
                    : new FriendlyError("Another program is using the port PaRuX needs.",
                        "Close other local AI tools (or restart PaRuX) and try again."),
                _ => italian
                    ? new FriendlyError("Windows ha negato l'accesso a una cartella necessaria a PaRuX.",
                        "Scegli un'altra cartella di archiviazione nelle impostazioni qui sotto, o una cartella di tua proprietà, e riprova.")
                    : new FriendlyError("Windows denied access to a folder PaRuX needs.",
                        "Choose a different storage folder in the settings below, or a folder you own, and try again.")
            };
        }

        /// <summary>Raw detail for the "Details" disclosure: file paths are replaced so a pasted screenshot does not reveal user folders.</summary>
        public static string? RedactForDetails(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string text = Regex.Replace(raw!.Trim(), @"[A-Za-z]:\\[^\s""'<>|]*", "<path>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return text.Length > 600 ? text[..600] + "…" : text;
        }
    }
}
