using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// A ready-made starting point for an advanced workspace so nobody faces a blank form. A template only fills fields in;
    /// the person still presses the real action button. <see cref="Fields"/> are workspace-specific plain values.
    /// </summary>
    public sealed record WorkspaceTemplate(
        string Id,
        string Workspace,
        Bilingual Title,
        Bilingual Description,
        Bilingual Prompt,
        IReadOnlyDictionary<string, string> Fields,
        bool NeedsFolder = false)
    {
        public string Field(string key, string fallback = "") => Fields.TryGetValue(key, out string? v) ? v : fallback;
    }

    public static class TemplateWorkspaces
    {
        public const string MediaImage = "media.image";
        public const string MediaVideo = "media.video";
        public const string Coding = "coding";
        public const string Automations = "automations";
        public const string Knowledge = "knowledge";
        public const string GroupChat = "groupchat";
        public const string Compare = "compare";
    }

    public static class WorkspaceTemplateCatalog
    {
        private static Dictionary<string, string> F(params (string Key, string Value)[] pairs) =>
            pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        private static WorkspaceTemplate Tpl(string id, string ws, string t, string tIt, string d, string dIt, string p, string pIt,
            Dictionary<string, string>? fields = null, bool needsFolder = false) =>
            new(id, ws, new Bilingual(t, tIt), new Bilingual(d, dIt), new Bilingual(p, pIt), fields ?? F(), needsFolder);

        public static IReadOnlyList<WorkspaceTemplate> All { get; } = new List<WorkspaceTemplate>
        {
            // Media: image. Prompts are English on purpose because most image models follow English best.
            Tpl("media.portrait", TemplateWorkspaces.MediaImage, "Portrait", "Ritratto",
                "A tall picture of a person, softly lit.", "Un'immagine verticale di una persona, con luce morbida.",
                "Studio portrait of a person, soft window light, shallow depth of field, natural skin texture, 85mm photo",
                "Studio portrait of a person, soft window light, shallow depth of field, natural skin texture, 85mm photo",
                F(("width", "896"), ("height", "1152"))),
            Tpl("media.product", TemplateWorkspaces.MediaImage, "Product photo", "Foto di prodotto",
                "A clean square photo of an object on a plain background.", "Una foto quadrata e pulita di un oggetto su sfondo semplice.",
                "Product photo of a ceramic mug on a white seamless background, soft shadow, studio lighting, sharp focus",
                "Product photo of a ceramic mug on a white seamless background, soft shadow, studio lighting, sharp focus",
                F(("width", "1024"), ("height", "1024"))),
            Tpl("media.landscape", TemplateWorkspaces.MediaImage, "Landscape", "Paesaggio",
                "A wide scenic picture.", "Un'ampia immagine di paesaggio.",
                "Wide mountain valley at golden hour, mist over a lake, dramatic clouds, cinematic landscape photo",
                "Wide mountain valley at golden hour, mist over a lake, dramatic clouds, cinematic landscape photo",
                F(("width", "1344"), ("height", "768"))),
            Tpl("media.phone", TemplateWorkspaces.MediaImage, "Phone wallpaper", "Sfondo per telefono",
                "A tall picture that fits a phone screen.", "Un'immagine alta che entra nello schermo di un telefono.",
                "Minimal abstract gradient wallpaper with soft flowing shapes, calm colours, high quality",
                "Minimal abstract gradient wallpaper with soft flowing shapes, calm colours, high quality",
                F(("width", "768"), ("height", "1344"))),
            // Media: video.
            Tpl("media.video.fromImage", TemplateWorkspaces.MediaVideo, "Short video from an image", "Breve video da un'immagine",
                "Choose a picture and bring it to life for five seconds.", "Scegli un'immagine e falla muovere per cinque secondi.",
                "Gentle camera push-in, subtle natural motion, the scene comes to life",
                "Gentle camera push-in, subtle natural motion, the scene comes to life",
                F(("width", "832"), ("height", "480"), ("seconds", "5"), ("needsStartImage", "true"))),
            Tpl("media.video.text", TemplateWorkspaces.MediaVideo, "Quick preview clip", "Clip di anteprima veloce",
                "A small, fast clip to test an idea before making a bigger one.", "Una piccola clip veloce per provare un'idea prima di farne una più grande.",
                "Waves rolling onto a beach at sunset, cinematic, slow motion",
                "Waves rolling onto a beach at sunset, cinematic, slow motion",
                F(("width", "832"), ("height", "480"), ("seconds", "3"))),
            // Coding agent.
            Tpl("coding.fixBug", TemplateWorkspaces.Coding, "Fix a bug", "Correggi un bug",
                "Describe what goes wrong; the agent finds the cause, fixes it and runs the checks.", "Descrivi cosa non va; l'agente trova la causa, la corregge ed esegue i controlli.",
                "Find and fix this bug: <describe what happens and what you expected>. Explain the cause, make the smallest change that fixes it, and run the project's tests.",
                "Trova e correggi questo bug: <descrivi cosa succede e cosa ti aspettavi>. Spiega la causa, fai la modifica più piccola possibile che lo risolve ed esegui i test del progetto."),
            Tpl("coding.explainRepo", TemplateWorkspaces.Coding, "Explain this repo", "Spiega questo repository",
                "A read-only tour: what the project does and where things live. Nothing is changed.", "Un giro in sola lettura: cosa fa il progetto e dove si trova ogni cosa. Non viene modificato nulla.",
                "Explain this repository to a newcomer: what it does, how it is organised, how to run it, and where to start reading. Do not change any files.",
                "Spiega questo repository a chi lo vede per la prima volta: cosa fa, come è organizzato, come si avvia e da dove iniziare a leggere. Non modificare alcun file.",
                F(("readOnly", "true"))),
            Tpl("coding.addTest", TemplateWorkspaces.Coding, "Add a test", "Aggiungi un test",
                "Write a test for something that is not covered yet.", "Scrivi un test per qualcosa che non è ancora coperto.",
                "Add a test for <the function or behaviour to cover>, following the style of the existing tests, and run it.",
                "Aggiungi un test per <la funzione o il comportamento da coprire>, seguendo lo stile dei test esistenti, ed eseguilo."),
            Tpl("coding.reviewChanges", TemplateWorkspaces.Coding, "Review my changes", "Rivedi le mie modifiche",
                "A read-only second opinion on what you changed recently.", "Un secondo parere in sola lettura su ciò che hai modificato di recente.",
                "Review my uncommitted changes: point out bugs, risky spots and unclear code, most important first. Do not change any files.",
                "Rivedi le mie modifiche non salvate: segnala bug, punti rischiosi e codice poco chiaro, prima i più importanti. Non modificare alcun file.",
                F(("readOnly", "true"))),
            // Automations. {folder} is substituted by AutomationTemplateBuilder.
            Tpl("automation.dailyFolderSummary", TemplateWorkspaces.Automations, "Summarize new files in a folder every morning", "Riassumi ogni mattina i nuovi file di una cartella",
                "Once a day PaRuX looks for files added in that folder and writes a short summary.", "Una volta al giorno PaRuX cerca i file aggiunti in quella cartella e scrive un breve riassunto.",
                "Look in the folder <folder> for files added during the last day. Summarize each one in two sentences and list them with their names.",
                "Cerca nella cartella <folder> i file aggiunti nell'ultimo giorno. Riassumi ciascuno in due frasi ed elencali con il loro nome.",
                F(("trigger", "schedule"), ("name", "Morning folder summary"), ("triggerJson", "{\"intervalSeconds\":86400}"), ("role", "")), needsFolder: true),
            Tpl("automation.summarizeEachNewFile", TemplateWorkspaces.Automations, "Summarize every new file as it arrives", "Riassumi ogni nuovo file appena arriva",
                "Whenever a file appears in the folder, PaRuX reads it and summarizes it.", "Ogni volta che un file compare nella cartella, PaRuX lo legge e lo riassume.",
                "A new file arrived: {fileName} in {folderPath}. Read it and summarize it in three sentences.",
                "È arrivato un nuovo file: {fileName} in {folderPath}. Leggilo e riassumilo in tre frasi.",
                F(("trigger", "file_created"), ("name", "Summarize new files"), ("triggerJson", "{\"folderPath\":\"<folder>\",\"filter\":\"*\"}"), ("role", "")), needsFolder: true),
            Tpl("automation.commitReview", TemplateWorkspaces.Automations, "Review every commit", "Rivedi ogni commit",
                "After each commit in a project, PaRuX reads it and flags problems.", "Dopo ogni commit di un progetto, PaRuX lo legge e segnala i problemi.",
                "Review commit {commitSha}: \"{commitMessage}\" in {repositoryPath}. Point out bugs or risky changes. Do not change any files.",
                "Rivedi il commit {commitSha}: \"{commitMessage}\" in {repositoryPath}. Segnala bug o modifiche rischiose. Non modificare alcun file.",
                F(("trigger", "git_commit"), ("name", "Review every commit"), ("triggerJson", "{\"repositoryPath\":\"<folder>\",\"branch\":\"\"}"), ("role", "coding")), needsFolder: true),
            // Knowledge.
            Tpl("knowledge.askFolder", TemplateWorkspaces.Knowledge, "Ask questions about a folder of documents", "Fai domande su una cartella di documenti",
                "Add the folder once, then ask in plain words.", "Aggiungi la cartella una volta, poi chiedi con parole semplici.",
                "What are the main points across these documents?", "Quali sono i punti principali in questi documenti?"),
            Tpl("knowledge.findPassage", TemplateWorkspaces.Knowledge, "Find where something is mentioned", "Trova dove viene citata una cosa",
                "Search by meaning, not just exact words.", "Cerca per significato, non solo per parole esatte.",
                "Where do my documents talk about deadlines?", "Dove parlano di scadenze i miei documenti?"),
            // Group chat and compare.
            Tpl("group.brainstorm", TemplateWorkspaces.GroupChat, "Brainstorm with three experts", "Brainstorming con tre esperti",
                "A creative thinker, a sceptic and a planner build on one idea.", "Un creativo, uno scettico e un pianificatore sviluppano un'idea.",
                "We want ideas for <your topic>. Creative: propose ideas. Sceptic: point out weak spots. Planner: turn the best idea into steps.",
                "Cerchiamo idee per <il tuo argomento>. Creativo: proponi idee. Scettico: indica i punti deboli. Pianificatore: trasforma l'idea migliore in passaggi.",
                F(("speakers", "3"))),
            Tpl("group.debate", TemplateWorkspaces.GroupChat, "Debate pros and cons", "Dibattito pro e contro",
                "One model argues for, one against, one decides.", "Un modello argomenta a favore, uno contro, uno decide.",
                "Debate this decision: <your decision>. One argues for it, one against it, and the third gives a balanced verdict.",
                "Discutete questa decisione: <la tua decisione>. Uno argomenta a favore, uno contro, il terzo dà un verdetto equilibrato.",
                F(("speakers", "3"))),
            Tpl("compare.explain", TemplateWorkspaces.Compare, "Explain it simply", "Spiegalo in modo semplice",
                "See which model explains best.", "Scopri quale modello spiega meglio.",
                "Explain photosynthesis to a ten-year-old in five sentences.", "Spiega la fotosintesi a un bambino di dieci anni in cinque frasi."),
            Tpl("compare.write", TemplateWorkspaces.Compare, "Write a short email", "Scrivi una breve email",
                "Compare tone and clarity.", "Confronta tono e chiarezza.",
                "Write a polite three-line email asking a colleague to move a meeting to Friday.", "Scrivi un'email cortese di tre righe in cui chiedi a un collega di spostare una riunione a venerdì."),
        };

        public static IReadOnlyList<WorkspaceTemplate> For(string workspace) =>
            All.Where(t => string.Equals(t.Workspace, workspace, StringComparison.Ordinal)).ToList();

        public static WorkspaceTemplate Get(string id) =>
            All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal)) ?? throw new ArgumentException("Unknown template '" + id + "'.", nameof(id));
    }

    /// <summary>Result of filling an automation template with a chosen folder.</summary>
    public sealed record AutomationDraft(string Name, string Trigger, string TriggerJson, string Goal, string Role);

    public static class AutomationTemplateBuilder
    {
        /// <summary>Substitutes the chosen folder into the trigger JSON (JSON-escaped) and goal (verbatim). A template that needs a folder refuses an empty one.</summary>
        public static AutomationDraft Build(WorkspaceTemplate template, string? folder, bool italian)
        {
            ArgumentNullException.ThrowIfNull(template);
            string chosen = (folder ?? string.Empty).Trim();
            if (template.NeedsFolder && chosen.Length == 0)
                throw new ArgumentException("Choose a folder first.", nameof(folder));
            string escapedForJson = JsonSerializer.Serialize(chosen);
            escapedForJson = escapedForJson.Substring(1, escapedForJson.Length - 2);
            string json = template.Field("triggerJson", "{}").Replace("<folder>", escapedForJson, StringComparison.Ordinal);
            string goal = template.Prompt.Pick(italian).Replace("<folder>", chosen, StringComparison.Ordinal);
            return new AutomationDraft(template.Field("name", template.Title.En), template.Field("trigger"), json, goal, template.Field("role"));
        }
    }
}
