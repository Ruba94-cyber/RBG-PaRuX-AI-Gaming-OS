using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// One technical control explained in plain words: a short label, a tooltip that says what moving it does, and whether
    /// it belongs to the folded-away "Advanced" group (true) or the always-visible simple group (false).
    /// </summary>
    public sealed record ControlHelp(string Id, Bilingual Label, Bilingual Tooltip, bool Advanced);

    /// <summary>Stable ids for <see cref="ControlHelpCatalog"/>; views look controls up by these.</summary>
    public static class ControlHelpIds
    {
        public const string MediaSteps = "media.steps";
        public const string MediaCfg = "media.cfg";
        public const string MediaSampler = "media.sampler";
        public const string MediaSeed = "media.seed";
        public const string MediaWidth = "media.width";
        public const string MediaHeight = "media.height";
        public const string MediaSeconds = "media.seconds";
        public const string MediaFps = "media.fps";
        public const string MediaStrength = "media.strength";
        public const string MediaLoraWeight = "media.loraWeight";
        public const string MediaMaskBlend = "media.maskBlend";
        public const string MediaBrush = "media.brush";
        public const string MediaStartImage = "media.startImage";
        public const string ChatTemperature = "chat.temperature";
        public const string ChatTopP = "chat.topP";
        public const string ChatMaxTokens = "chat.maxTokens";
        public const string ChatSeed = "chat.seed";
        public const string RuntimeContextSize = "runtime.contextSize";
        public const string RuntimeQuant = "runtime.quant";
        public const string RuntimeGpuLayers = "runtime.gpuLayers";
        public const string RuntimeProfile = "runtime.profile";
        public const string TrainRank = "train.rank";
        public const string TrainEpochs = "train.epochs";
        public const string TrainLearningRate = "train.learningRate";
        public const string KnowledgeChunk = "knowledge.chunk";
        public const string KnowledgeResults = "knowledge.results";
        public const string AutomationTrigger = "automation.trigger";
        public const string AutomationTriggerJson = "automation.triggerJson";
        public const string AutomationGoal = "automation.goal";
        public const string AutomationRole = "automation.role";
        public const string McpCommand = "mcp.command";
        public const string McpArguments = "mcp.arguments";
        public const string CodingPermission = "coding.permission";
        public const string VoiceMicrophone = "voice.microphone";
        public const string VoiceLanguage = "voice.language";
        public const string VoiceModelSize = "voice.modelSize";
        public const string RoutingAuto = "routing.auto";
        public const string GroupSpeakers = "group.speakers";
        public const string GroupRounds = "group.rounds";
        public const string CompareModels = "compare.models";
        public const string UpdateChannel = "update.channel";
        public const string ObservabilityLimit = "observability.limit";
    }

    /// <summary>
    /// Plain-language labels and tooltips for the technical controls of the advanced workspaces, in English and Italian.
    /// Tooltips follow "Name: what it does" so a person who has never heard of a sampler still knows whether to touch it.
    /// </summary>
    public static class ControlHelpCatalog
    {
        private static ControlHelp H(string id, string labelEn, string labelIt, string tipEn, string tipIt, bool advanced = true) =>
            new(id, new Bilingual(labelEn, labelIt), new Bilingual(tipEn, tipIt), advanced);

        public static IReadOnlyList<ControlHelp> All { get; } = new List<ControlHelp>
        {
            H(ControlHelpIds.MediaSteps, "Steps", "Passaggi",
                "Steps: more steps = more detail but slower. 20-30 is a good balance.",
                "Passaggi: più passaggi = più dettagli ma più lentezza. 20-30 è un buon equilibrio."),
            H(ControlHelpIds.MediaCfg, "Prompt strength (cfg)", "Fedeltà al testo (cfg)",
                "Prompt strength (cfg): higher follows your description more literally, lower gives the model more freedom. Around 5-8 works for most images.",
                "Fedeltà al testo (cfg): più alto segue la descrizione alla lettera, più basso lascia più libertà al modello. Per la maggior parte delle immagini va bene 5-8."),
            H(ControlHelpIds.MediaSampler, "Drawing method (sampler)", "Metodo di disegno (sampler)",
                "Sampler: the method used to turn noise into a picture. The default is fine; change it only to compare looks.",
                "Sampler: il metodo che trasforma il rumore in un'immagine. Quello predefinito va bene; cambialo solo per confrontare lo stile."),
            H(ControlHelpIds.MediaSeed, "Seed", "Seed (numero di partenza)",
                "Seed: the starting number. The same seed with the same settings gives the same picture; leave it empty for a new one every time.",
                "Seed: il numero di partenza. Lo stesso seed con le stesse impostazioni dà la stessa immagine; lascialo vuoto per averne una diversa ogni volta."),
            H(ControlHelpIds.MediaWidth, "Width", "Larghezza",
                "Width in pixels. Bigger is sharper but slower and needs more video memory.",
                "Larghezza in pixel. Più grande è più nitido ma più lento e richiede più memoria video.", advanced: false),
            H(ControlHelpIds.MediaHeight, "Height", "Altezza",
                "Height in pixels. Bigger is sharper but slower and needs more video memory.",
                "Altezza in pixel. Più grande è più nitido ma più lento e richiede più memoria video.", advanced: false),
            H(ControlHelpIds.MediaSeconds, "Length (seconds)", "Durata (secondi)",
                "How long the clip lasts. Longer clips take much longer to make.",
                "Quanto dura la clip. Le clip più lunghe richiedono molto più tempo.", advanced: false),
            H(ControlHelpIds.MediaFps, "Smoothness (frames per second)", "Fluidità (fotogrammi al secondo)",
                "Frames per second: higher looks smoother but takes longer. 24 is the film standard.",
                "Fotogrammi al secondo: più alto è più fluido ma richiede più tempo. 24 è lo standard del cinema."),
            H(ControlHelpIds.MediaStrength, "How much to change", "Quanto cambiare",
                "Low keeps your picture almost as it is; high lets the model repaint it freely.",
                "Basso lascia l'immagine quasi com'è; alto permette al modello di ridipingerla liberamente.", advanced: false),
            H(ControlHelpIds.MediaLoraWeight, "Style strength", "Intensità dello stile",
                "How strongly this add-on style (LoRA) is applied. Start around 0.7; too high can distort the picture.",
                "Quanto forte viene applicato questo stile aggiuntivo (LoRA). Parti da circa 0,7; troppo alto può deformare l'immagine."),
            H(ControlHelpIds.MediaMaskBlend, "Edge blend", "Sfumatura dei bordi",
                "How softly the repainted area blends into the rest of the picture. A few pixels hides the seam.",
                "Quanto morbidamente l'area ridipinta si fonde col resto dell'immagine. Pochi pixel nascondono il bordo."),
            H(ControlHelpIds.MediaBrush, "Brush size", "Dimensione del pennello",
                "Paint over the part of the picture you want to replace.",
                "Dipingi sulla parte dell'immagine che vuoi sostituire.", advanced: false),
            H(ControlHelpIds.MediaStartImage, "Start image", "Immagine iniziale",
                "Optional first frame. The video begins from this picture; it must have the same proportions as the video.",
                "Primo fotogramma facoltativo. Il video parte da questa immagine; deve avere le stesse proporzioni del video.", advanced: false),
            H(ControlHelpIds.ChatTemperature, "Creativity (temperature)", "Creatività (temperatura)",
                "Temperature: low gives steady, predictable answers; high gives more varied and surprising ones.",
                "Temperatura: bassa dà risposte stabili e prevedibili; alta dà risposte più varie e sorprendenti."),
            H(ControlHelpIds.ChatTopP, "Word variety (top-p)", "Varietà di parole (top-p)",
                "Top-p: lower makes the model pick only from its most likely words; 1 lets it consider them all.",
                "Top-p: più basso fa scegliere al modello solo tra le parole più probabili; 1 le considera tutte."),
            H(ControlHelpIds.ChatMaxTokens, "Longest reply", "Risposta massima",
                "The longest answer, counted in tokens (about three quarters of a word each). Empty means automatic.",
                "La risposta più lunga, contata in token (circa tre quarti di parola ciascuno). Vuoto significa automatico."),
            H(ControlHelpIds.ChatSeed, "Seed", "Seed (numero di partenza)",
                "A seed makes a reply repeatable on the same model. Leave it empty for a different reply each time.",
                "Un seed rende ripetibile una risposta con lo stesso modello. Lascialo vuoto per una risposta diversa ogni volta."),
            H(ControlHelpIds.RuntimeContextSize, "Memory length (context size)", "Lunghezza della memoria (contesto)",
                "Context size: how much text the model can keep in mind at once. Bigger remembers more of a long chat but uses more memory.",
                "Contesto: quanto testo il modello riesce a tenere a mente. Più grande ricorda di più di una chat lunga ma usa più memoria."),
            H(ControlHelpIds.RuntimeQuant, "Compression level (quantization)", "Livello di compressione (quantizzazione)",
                "Quantization shrinks a model so it fits your computer. Q4 is small and quick, Q8 is larger and a bit more accurate.",
                "La quantizzazione rimpicciolisce il modello per farlo stare nel computer. Q4 è piccolo e veloce, Q8 è più grande e un po' più preciso."),
            H(ControlHelpIds.RuntimeGpuLayers, "Work on the graphics card", "Lavoro sulla scheda video",
                "How much of the model runs on the graphics card. More is faster, as long as your video memory is enough.",
                "Quanta parte del modello gira sulla scheda video. Di più è più veloce, finché la memoria video basta."),
            H(ControlHelpIds.RuntimeProfile, "Execution profile", "Profilo di esecuzione",
                "A ready-made balance between speed, quality and how much of your computer PaRuX may use.",
                "Un equilibrio già pronto tra velocità, qualità e quanta parte del computer PaRuX può usare.", advanced: false),
            H(ControlHelpIds.TrainRank, "Learning capacity (LoRA rank)", "Capacità di apprendimento (rango LoRA)",
                "Rank: how much new behaviour the training can add. Higher learns more but needs more memory; 8-16 suits most cases.",
                "Rango: quanto comportamento nuovo può aggiungere l'addestramento. Più alto impara di più ma richiede più memoria; 8-16 va bene quasi sempre."),
            H(ControlHelpIds.TrainEpochs, "Passes over your data (epochs)", "Passate sui tuoi dati (epoche)",
                "Epochs: how many times training reads your whole dataset. Too few learns little, too many memorises it.",
                "Epoche: quante volte l'addestramento legge tutto il tuo dataset. Poche imparano poco, troppe lo memorizzano."),
            H(ControlHelpIds.TrainLearningRate, "Learning speed", "Velocità di apprendimento",
                "Learning rate: how big each learning step is. The default is safe; a value that is too high can ruin the model.",
                "Learning rate: quanto è grande ogni passo di apprendimento. Quello predefinito è sicuro; un valore troppo alto può rovinare il modello."),
            H(ControlHelpIds.KnowledgeChunk, "Piece size (chunk)", "Dimensione dei pezzi (chunk)",
                "Documents are cut into pieces before they are searched. Smaller pieces find exact facts; larger pieces keep more context.",
                "I documenti vengono tagliati in pezzi prima della ricerca. Pezzi piccoli trovano fatti precisi; pezzi grandi conservano più contesto."),
            H(ControlHelpIds.KnowledgeResults, "Passages to use", "Passaggi da usare",
                "How many matching passages are given to the model when it answers. More is thorough but slower.",
                "Quanti passaggi pertinenti vengono dati al modello per rispondere. Di più è più completo ma più lento."),
            H(ControlHelpIds.AutomationTrigger, "When to run", "Quando eseguire",
                "What starts the automation: a timer, a new file, a code change and so on.",
                "Cosa avvia l'automazione: un timer, un nuovo file, una modifica al codice e così via.", advanced: false),
            H(ControlHelpIds.AutomationTriggerJson, "Trigger details (JSON)", "Dettagli del trigger (JSON)",
                "The exact settings of the trigger, as text. Templates fill this in for you.",
                "Le impostazioni esatte del trigger, come testo. I modelli lo compilano al posto tuo."),
            H(ControlHelpIds.AutomationGoal, "What it should do", "Cosa deve fare",
                "Describe the job in plain words, like you would ask a colleague.",
                "Descrivi il lavoro con parole semplici, come lo chiederesti a un collega.", advanced: false),
            H(ControlHelpIds.AutomationRole, "Specialist (role)", "Specialista (ruolo)",
                "Optional: which kind of specialist model should do the job, such as coding or reasoning. Empty lets PaRuX choose.",
                "Facoltativo: quale tipo di modello specialista deve fare il lavoro, per esempio programmazione o ragionamento. Vuoto lascia scegliere a PaRuX."),
            H(ControlHelpIds.McpCommand, "Program to start", "Programma da avviare",
                "The program that provides the tools. Presets fill this in; change it only if you know the server.",
                "Il programma che fornisce gli strumenti. I preset lo compilano; cambialo solo se conosci il server."),
            H(ControlHelpIds.McpArguments, "Options for the program", "Opzioni del programma",
                "Extra words passed to the program, such as the folder it may read. Check them before connecting.",
                "Parole aggiuntive passate al programma, per esempio la cartella che può leggere. Controllale prima di connetterti."),
            H(ControlHelpIds.CodingPermission, "How much the agent may do alone", "Quanto può fare da solo l'agente",
                "Controls whether the coding agent asks before it edits files or runs commands.",
                "Decide se l'agente di programmazione chiede prima di modificare file o eseguire comandi.", advanced: false),
            H(ControlHelpIds.VoiceMicrophone, "Microphone", "Microfono",
                "Which microphone PaRuX listens to.",
                "Quale microfono ascolta PaRuX.", advanced: false),
            H(ControlHelpIds.VoiceLanguage, "Spoken language", "Lingua parlata",
                "The language you speak. Leave it on automatic unless words are recognised wrongly.",
                "La lingua che parli. Lasciala su automatico a meno che le parole vengano riconosciute male."),
            H(ControlHelpIds.VoiceModelSize, "Listening quality", "Qualità dell'ascolto",
                "Bigger listening models understand more accurately but are slower and use more memory.",
                "I modelli di ascolto più grandi capiscono meglio ma sono più lenti e usano più memoria."),
            H(ControlHelpIds.RoutingAuto, "Let PaRuX choose the model", "Lascia scegliere il modello a PaRuX",
                "AUTO picks a fitting installed model for each question. Turn it off to always use the model you select.",
                "AUTO sceglie un modello installato adatto a ogni domanda. Disattivalo per usare sempre il modello selezionato.", advanced: false),
            H(ControlHelpIds.GroupSpeakers, "Who takes part", "Chi partecipa",
                "Each participant is a model with its own role. Two to four keeps the conversation readable.",
                "Ogni partecipante è un modello con un proprio ruolo. Da due a quattro rende la conversazione leggibile.", advanced: false),
            H(ControlHelpIds.GroupRounds, "Rounds", "Giri di conversazione",
                "How many times everyone speaks before the group stops. More rounds take longer.",
                "Quante volte parlano tutti prima che il gruppo si fermi. Più giri richiedono più tempo."),
            H(ControlHelpIds.CompareModels, "Models to compare", "Modelli da confrontare",
                "Send the same question to several models and read the answers side by side.",
                "Invia la stessa domanda a più modelli e leggi le risposte affiancate.", advanced: false),
            H(ControlHelpIds.UpdateChannel, "Update channel", "Canale degli aggiornamenti",
                "Stable gets only tested releases; earlier channels get new things sooner but may have rough edges.",
                "Stabile riceve solo versioni testate; i canali anticipati ricevono le novità prima ma possono avere difetti."),
            H(ControlHelpIds.ObservabilityLimit, "How many recent events", "Quanti eventi recenti",
                "How many of the latest events to list. A smaller number loads faster.",
                "Quanti degli ultimi eventi elencare. Un numero più piccolo si carica più in fretta."),
        };

        private static readonly Dictionary<string, ControlHelp> ById = All.ToDictionary(h => h.Id, StringComparer.Ordinal);

        public static ControlHelp? Find(string id) => ById.TryGetValue(id, out ControlHelp? help) ? help : null;

        public static ControlHelp Get(string id) =>
            Find(id) ?? throw new ArgumentException("No help text is registered for control '" + id + "'.", nameof(id));

        public static string Tooltip(string id, bool italian) => Get(id).Tooltip.Pick(italian);

        public static string Label(string id, bool italian) => Get(id).Label.Pick(italian);
    }
}
