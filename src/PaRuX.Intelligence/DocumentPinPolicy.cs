using System.Text;

namespace PaRuX.Intelligence;

public sealed record PinCandidate(string Name, string? Text, bool Found);

public sealed record PinnedDocumentPlan(string Name, bool Included, bool Truncated, int Tokens, string Text, string? Problem);

public sealed record DocumentPinPlan(
    IReadOnlyList<PinnedDocumentPlan> Documents, int BudgetTokens, int UsedTokens, bool ExceedsBudget, bool TokensEstimated);

/// <summary>
/// Pins documents into a workspace prompt under a hard token budget. Documents are taken in pin order;
/// one that does not fit is truncated to the remaining budget (when enough remains to be useful) or left
/// out, and the plan says so (ExceedsBudget) so the UI can warn instead of silently dropping text.
/// </summary>
public static class DocumentPinPolicy
{
    public const int DefaultBudgetTokens = 2_000;
    public const int MinBudgetTokens = 256;
    public const int MaxBudgetTokens = 32_000;
    public const int MaxPins = 5;
    public const int MinUsefulTokens = 120;

    public static int ClampBudget(int? requested) =>
        Math.Clamp(requested ?? DefaultBudgetTokens, MinBudgetTokens, MaxBudgetTokens);

    /// <param name="countTokens">Real tokenizer when available; null falls back to the planner's estimate and flags the plan as estimated.</param>
    public static DocumentPinPlan Plan(IReadOnlyList<PinCandidate> pins, int budgetTokens, Func<string, int>? countTokens = null)
    {
        int budget = ClampBudget(budgetTokens);
        Func<string, int> count = countTokens ?? ConversationWindowPlanner.EstimateTokens;
        var docs = new List<PinnedDocumentPlan>();
        int used = 0;
        bool exceeds = pins.Count > MaxPins;
        foreach (PinCandidate pin in pins.Take(MaxPins))
        {
            if (!pin.Found || string.IsNullOrWhiteSpace(pin.Text))
            {
                docs.Add(new PinnedDocumentPlan(pin.Name, false, false, 0, string.Empty, pin.Found ? "empty" : "not_found"));
                continue;
            }
            int remaining = budget - used;
            int full = count(pin.Text);
            if (full <= remaining)
            {
                docs.Add(new PinnedDocumentPlan(pin.Name, true, false, full, pin.Text, null));
                used += full;
                continue;
            }
            exceeds = true;
            if (remaining < MinUsefulTokens)
            {
                docs.Add(new PinnedDocumentPlan(pin.Name, false, false, 0, string.Empty, "over_budget"));
                continue;
            }
            string cut = CutToTokens(pin.Text, remaining, count);
            int cutTokens = count(cut);
            docs.Add(new PinnedDocumentPlan(pin.Name, cut.Length > 0, true, cutTokens, cut, "truncated"));
            used += cutTokens;
        }
        return new DocumentPinPlan(docs, budget, used, exceeds, countTokens == null);
    }

    private static string CutToTokens(string text, int maxTokens, Func<string, int> count)
    {
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (count(text.Substring(0, mid)) <= maxTokens) lo = mid; else hi = mid - 1;
        }
        return text.Substring(0, lo).TrimEnd();
    }

    public static string? FormatWarning(DocumentPinPlan plan, bool italian)
    {
        if (!plan.ExceedsBudget && plan.Documents.All(d => d.Problem == null)) return null;
        var sb = new StringBuilder();
        string approx = plan.TokensEstimated ? "~" : string.Empty;
        var culture = System.Globalization.CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-US");
        if (plan.ExceedsBudget)
            sb.Append(italian
                ? string.Create(culture, $"I documenti fissati superano il budget di {plan.BudgetTokens:N0} token ({approx}{plan.UsedTokens:N0} usati): ")
                : string.Create(culture, $"Pinned documents exceed the {plan.BudgetTokens:N0}-token budget ({approx}{plan.UsedTokens:N0} used): "));
        var notes = new List<string>();
        foreach (PinnedDocumentPlan d in plan.Documents)
        {
            switch (d.Problem)
            {
                case "truncated": notes.Add(italian ? d.Name + " troncato" : d.Name + " truncated"); break;
                case "over_budget": notes.Add(italian ? d.Name + " escluso" : d.Name + " left out"); break;
                case "not_found": notes.Add(italian ? d.Name + " non trovato nell'indice" : d.Name + " not found in the index"); break;
                case "empty": notes.Add(italian ? d.Name + " vuoto" : d.Name + " is empty"); break;
            }
        }
        sb.Append(string.Join("; ", notes)).Append('.');
        if (plan.TokensEstimated) sb.Append(italian ? " (conteggio stimato)" : " (estimated count)");
        return sb.ToString();
    }
}
