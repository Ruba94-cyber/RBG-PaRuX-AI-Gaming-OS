using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>One pending "run this Python" request as the Core reports it (GET /api/v1/chat/code/approvals).</summary>
    public sealed record ChatCodeApprovalDto(string Id, string Action, string Summary, DateTime CreatedUtc, string State);

    /// <summary>What the approval card shows for one request.</summary>
    public sealed record ChatCodeApprovalView(string Id, string Code, bool Truncated);

    /// <summary>
    /// Pure rules for the in-chat Python approval card and the runtime row in Settings. No UI types, so the decisions
    /// (what is shown, when the card is empty, how a failure is worded) are covered by unit tests. Approving is always an
    /// explicit click on exactly the code shown; nothing here approves, retries or hides a request.
    /// </summary>
    public static class ChatCodeApprovalPresenter
    {
        public const string RunPythonAction = "run_python";
        public const int MaxCards = 4;

        /// <summary>Only still-pending run_python requests, oldest first, bounded so a flood cannot cover the chat.</summary>
        public static IReadOnlyList<ChatCodeApprovalView> Pending(IEnumerable<ChatCodeApprovalDto>? items)
        {
            if (items == null) return Array.Empty<ChatCodeApprovalView>();
            return items
                .Where(i => string.Equals(i.Action, RunPythonAction, StringComparison.Ordinal)
                    && string.Equals(i.State, "pending", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(i.Id))
                .OrderBy(i => i.CreatedUtc)
                .Take(MaxCards)
                .Select(i => ToView(i))
                .ToList();
        }

        /// <summary>The Core summary is "<header line>\n<code preview>"; the preview is cut at a fixed length with a marker.</summary>
        public static ChatCodeApprovalView ToView(ChatCodeApprovalDto item)
        {
            string summary = item.Summary ?? string.Empty;
            int newline = summary.IndexOf('\n');
            string code = newline >= 0 ? summary[(newline + 1)..] : summary;
            bool truncated = code.Contains("more characters)", StringComparison.Ordinal)
                && code.Contains("\n... (", StringComparison.Ordinal);
            return new ChatCodeApprovalView(item.Id, code, truncated);
        }

        public static string Title(bool italian) => italian
            ? "Il modello vuole eseguire codice Python"
            : "The model wants to run Python code";

        public static string SafetyNote(bool italian) => italian
            ? "Gira in una sandbox isolata: nessuna rete, nessun accesso ai tuoi file, tempo e memoria limitati. Ogni esecuzione richiede la tua approvazione."
            : "It runs in an isolated sandbox: no network, no access to your files, limited time and memory. Every run needs your approval.";

        public static string TruncationNote(bool italian) => italian
            ? "Il codice è più lungo dell'anteprima: approvi l'esecuzione del codice completo, non solo di questa parte."
            : "The code is longer than this preview: you are approving the full code, not only this part.";

        public static string Approve(bool italian) => italian ? "Approva ed esegui" : "Approve and run";
        public static string Deny(bool italian) => italian ? "Rifiuta" : "Deny";

        /// <summary>Failure wording that never claims the code ran or was refused when the Core could not be reached.</summary>
        public static string DecisionFailed(bool approve, string? detail, bool italian)
        {
            string reason = string.IsNullOrWhiteSpace(detail) ? (italian ? "motivo sconosciuto" : "unknown reason") : detail.Trim();
            if (reason.Length > 200) reason = reason[..200] + "...";
            return italian
                ? (approve ? "Non è stato possibile registrare l'approvazione: " : "Non è stato possibile registrare il rifiuto: ") + reason + " Il codice non è stato eseguito."
                : (approve ? "The approval could not be recorded: " : "The refusal could not be recorded: ") + reason + " The code was not run.";
        }

        public static string RuntimeStateText(string state, string? stage, string? error, bool italian) => state switch
        {
            "ready" => italian ? "Runtime Python gestito da PaRuX: installato." : "PaRuX-managed Python runtime: installed.",
            "installing" => (italian ? "Installazione in corso. " : "Installing. ") + (stage ?? string.Empty),
            "skipped" => italian ? "Esecuzione codice saltata: nulla viene installato." : "Code execution skipped: nothing is installed.",
            "failed" => (italian ? "Installazione non riuscita: " : "Installation failed: ") + (string.IsNullOrWhiteSpace(error) ? (italian ? "motivo sconosciuto" : "unknown reason") : error),
            _ => italian ? "Runtime Python gestito da PaRuX: non installato." : "PaRuX-managed Python runtime: not installed."
        };
    }
}
