using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// The short "What this does" header of an advanced workspace: one sentence, one example, and what to show when the
    /// workspace has nothing in it yet together with the single first action.
    /// </summary>
    public sealed record WorkspaceGuide(
        string Id,
        Bilingual WhatItDoes,
        Bilingual Example,
        Bilingual EmptyState,
        Bilingual FirstAction);

    public static class WorkspaceGuideIds
    {
        public const string Create = "create";
        public const string Workflows = "create.workflows";
        public const string Coding = "coding";
        public const string Automations = "automations";
        public const string Mcp = "mcp";
        public const string Knowledge = "knowledge";
        public const string Projects = "projects";
        public const string GroupChat = "groupchat";
        public const string Compare = "compare";
        public const string Voice = "voice";
        public const string Runtime = "runtime";
        public const string Routing = "routing";
        public const string Profiles = "profiles";
        public const string Updates = "updates";
        public const string Observability = "observability";
        public const string Settings = "settings";
    }

    public static class WorkspaceGuideCatalog
    {
        private static WorkspaceGuide G(string id, string what, string whatIt, string ex, string exIt, string empty, string emptyIt, string act, string actIt) =>
            new(id, new Bilingual(what, whatIt), new Bilingual(ex, exIt), new Bilingual(empty, emptyIt), new Bilingual(act, actIt));

        public static IReadOnlyList<WorkspaceGuide> All { get; } = new List<WorkspaceGuide>
        {
            G(WorkspaceGuideIds.Create,
                "Make pictures, videos, music and voices on your own computer. Nothing is uploaded.",
                "Crea immagini, video, musica e voci sul tuo computer. Non viene caricato nulla.",
                "Pick \"Portrait\", press Generate, and your picture appears in the gallery.",
                "Scegli \"Ritratto\", premi Genera e l'immagine compare nella galleria.",
                "Nothing created yet.", "Ancora nulla di creato.",
                "Pick a template", "Scegli un modello"),
            G(WorkspaceGuideIds.Workflows,
                "Run ComfyUI workflows you already have, and add style packs (LoRAs) to your images.",
                "Esegui i workflow ComfyUI che hai già e aggiungi pacchetti di stile (LoRA) alle tue immagini.",
                "Import a workflow file, press Run, and the result lands in your gallery.",
                "Importa un file di workflow, premi Esegui e il risultato finisce nella galleria.",
                "No imported workflows yet.", "Nessun workflow importato.",
                "Import a workflow", "Importa un workflow"),
            G(WorkspaceGuideIds.Coding,
                "A coding helper that reads your project, makes changes and checks them. You review before anything is kept.",
                "Un assistente di programmazione che legge il tuo progetto, fa modifiche e le verifica. Rivedi tu prima che resti qualcosa.",
                "Choose a project folder and ask it to \"Fix a bug\" or \"Explain this repo\".",
                "Scegli la cartella di un progetto e chiedi \"Correggi un bug\" o \"Spiega questo repository\".",
                "No coding task yet.", "Ancora nessuna attività di programmazione.",
                "Choose a task template", "Scegli un modello di attività"),
            G(WorkspaceGuideIds.Automations,
                "Let PaRuX do a job for you by itself when something happens or on a timer.",
                "Lascia che PaRuX faccia un lavoro da solo quando succede qualcosa o a intervalli regolari.",
                "Summarize new files in a folder every morning.",
                "Riassumi ogni mattina i nuovi file di una cartella.",
                "No automations yet.", "Nessuna automazione per ora.",
                "Start from a template", "Parti da un modello"),
            G(WorkspaceGuideIds.Mcp,
                "Connect extra tools (MCP servers) so the assistant can read a folder, a database or the web.",
                "Collega strumenti aggiuntivi (server MCP) così l'assistente può leggere una cartella, un database o il web.",
                "Add \"Filesystem\" to let the assistant read the files in one folder you choose.",
                "Aggiungi \"Filesystem\" per far leggere all'assistente i file di una cartella che scegli.",
                "No tools connected yet.", "Nessuno strumento collegato.",
                "Add a tool from a preset", "Aggiungi uno strumento da un preset"),
            G(WorkspaceGuideIds.Knowledge,
                "Teach PaRuX your own documents so it can answer from them and show where the answer came from.",
                "Insegna a PaRuX i tuoi documenti così può rispondere in base a essi e mostrare da dove viene la risposta.",
                "Add a folder of PDFs, then ask \"What does the contract say about notice periods?\".",
                "Aggiungi una cartella di PDF e chiedi \"Cosa dice il contratto sul preavviso?\".",
                "No documents added yet.", "Nessun documento aggiunto.",
                "Add a folder or files", "Aggiungi una cartella o dei file"),
            G(WorkspaceGuideIds.Projects,
                "Keep related chats, files and results together in one place.",
                "Tieni insieme chat, file e risultati collegati in un unico posto.",
                "Make a project called \"Holiday plans\" and keep the itinerary and its chats there.",
                "Crea un progetto \"Piani per le vacanze\" e tieni lì itinerario e chat.",
                "No projects yet.", "Nessun progetto per ora.",
                "Create a project", "Crea un progetto"),
            G(WorkspaceGuideIds.GroupChat,
                "Let several AI models talk about your question together, each with its own role.",
                "Fai ragionare insieme più modelli IA sulla tua domanda, ciascuno con il proprio ruolo.",
                "Start \"Brainstorm with three experts\" and watch them build on each other's ideas.",
                "Avvia \"Brainstorming con tre esperti\" e guarda come sviluppano le idee a vicenda.",
                "No group chat yet.", "Nessuna chat di gruppo.",
                "Start from a template", "Parti da un modello"),
            G(WorkspaceGuideIds.Compare,
                "Ask the same question to several models and read their answers side by side.",
                "Fai la stessa domanda a più modelli e leggi le risposte affiancate.",
                "Compare two models on \"Explain photosynthesis to a ten-year-old\".",
                "Confronta due modelli su \"Spiega la fotosintesi a un bambino di dieci anni\".",
                "No comparison yet.", "Nessun confronto.",
                "Pick models to compare", "Scegli i modelli da confrontare"),
            G(WorkspaceGuideIds.Voice,
                "Talk instead of typing, have answers read aloud, and turn meetings into notes.",
                "Parla invece di scrivere, fatti leggere le risposte e trasforma le riunioni in appunti.",
                "Press Record in a meeting and get a written summary when you stop.",
                "Premi Registra durante una riunione e ottieni un riassunto scritto quando ti fermi.",
                "The meeting assistant is off.", "L'assistente riunioni è spento.",
                "Turn on the meeting assistant", "Attiva l'assistente riunioni"),
            G(WorkspaceGuideIds.Runtime,
                "See the engines PaRuX runs on, whether they are healthy, and install or repair them.",
                "Vedi i motori su cui gira PaRuX, se funzionano bene, e installali o riparali.",
                "If chat feels slow, check here that the graphics engine is installed.",
                "Se la chat è lenta, controlla qui che il motore per la scheda video sia installato.",
                "Nothing needs attention.", "Niente richiede attenzione.",
                "Check my setup", "Controlla la mia configurazione"),
            G(WorkspaceGuideIds.Routing,
                "Decide which model answers what. AUTO picks a good one for every question.",
                "Decidi quale modello risponde a cosa. AUTO sceglie quello giusto per ogni domanda.",
                "Leave AUTO on, or pin a favourite model for coding.",
                "Lascia AUTO attivo, oppure fissa un modello preferito per la programmazione.",
                "AUTO is choosing for you.", "AUTO sceglie al posto tuo.",
                "Keep AUTO on", "Lascia AUTO attivo"),
            G(WorkspaceGuideIds.Profiles,
                "Pick how much of your computer PaRuX may use: quiet and light, balanced, or full speed.",
                "Scegli quanta parte del computer può usare PaRuX: silenzioso e leggero, bilanciato o massima velocità.",
                "Choose \"Balanced\" while you work and a faster profile while you step away.",
                "Scegli \"Bilanciato\" mentre lavori e un profilo più veloce quando ti allontani.",
                "A profile is already active.", "Un profilo è già attivo.",
                "Choose a profile", "Scegli un profilo"),
            G(WorkspaceGuideIds.Updates,
                "Keeps PaRuX and its engines up to date, and never changes anything you installed yourself.",
                "Mantiene aggiornati PaRuX e i suoi motori, e non modifica mai ciò che hai installato tu.",
                "Press Check for updates and install what is offered when it suits you.",
                "Premi Cerca aggiornamenti e installa ciò che viene proposto quando ti fa comodo.",
                "Everything is up to date.", "Tutto è aggiornato.",
                "Check for updates", "Cerca aggiornamenti"),
            G(WorkspaceGuideIds.Observability,
                "Shows what PaRuX has been doing and how fast your models run. Everything stays on your computer.",
                "Mostra cosa ha fatto PaRuX e quanto sono veloci i tuoi modelli. Tutto resta sul tuo computer.",
                "If an answer was slow, look here to see which model and engine produced it.",
                "Se una risposta è stata lenta, guarda qui quale modello e motore l'ha prodotta.",
                "Nothing recorded yet. Use Chat for a moment and come back.", "Ancora nulla registrato. Usa la Chat per un momento e torna qui.",
                "Refresh", "Aggiorna"),
            G(WorkspaceGuideIds.Settings,
                "Everyday options are at the top. Technical options are folded away under Advanced.",
                "Le opzioni di uso quotidiano sono in alto. Quelle tecniche sono nascoste sotto Avanzate.",
                "Change the language or theme here; leave the rest unless you need it.",
                "Cambia qui la lingua o il tema; lascia il resto a meno che non ti serva.",
                "Defaults are in use.", "Sono in uso le impostazioni predefinite.",
                "Open Advanced settings", "Apri le impostazioni avanzate"),
        };

        private static readonly Dictionary<string, WorkspaceGuide> ById = All.ToDictionary(g => g.Id, StringComparer.Ordinal);

        public static WorkspaceGuide Get(string id) =>
            ById.TryGetValue(id, out WorkspaceGuide? guide) ? guide : throw new ArgumentException("Unknown workspace guide '" + id + "'.", nameof(id));
    }
}
