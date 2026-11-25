using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>One task shortcut: reachable from the Ctrl+K palette and, where it has one, from a keyboard accelerator.</summary>
    public sealed record QuickAction(string Id, string TitleEn, string TitleIt, string Keywords, char? AcceleratorKey, string? ShortcutText);

    /// <summary>
    /// The shortest paths for the everyday tasks (choose a model, change generation settings, attach a file, create an image, search chats).
    /// Pure data so the palette, the accelerators and the tests all read one list. All accelerators are Ctrl+Shift+letter, which no
    /// text box uses, so typing in the composer or a search box is never intercepted.
    /// </summary>
    public static class QuickActionCatalog
    {
        public const string SearchChats = "quick-search-chats";
        public const string ChooseModel = "quick-choose-model";
        public const string GenerationSettings = "quick-generation-settings";
        public const string AttachFile = "quick-attach-file";
        public const string CreateImage = "quick-create-image";

        public static IReadOnlyList<QuickAction> All { get; } = new QuickAction[]
        {
            new(SearchChats, "Search chats", "Cerca nelle chat", "find history conversations cronologia trova conversazioni", 'F', "Ctrl+Shift+F"),
            new(ChooseModel, "Choose or load a model for the chat", "Scegli o carica un modello per la chat", "model load switch picker modello carica cambia", 'M', "Ctrl+Shift+M"),
            new(GenerationSettings, "Chat settings: temperature, reply length, context", "Impostazioni chat: temperatura, lunghezza risposta, contesto", "sampling creativity seed creativita lunghezza contesto context", 'G', "Ctrl+Shift+G"),
            new(AttachFile, "Attach a file or image to the chat", "Allega un file o un'immagine alla chat", "document pdf upload image audio video documento carica allega", 'A', "Ctrl+Shift+A"),
            new(CreateImage, "Create an image", "Crea un'immagine", "picture generate draw image immagine genera disegna", 'I', "Ctrl+Shift+I"),
        };

        /// <summary>Accelerators already owned elsewhere in the app; a quick action must never reuse one.</summary>
        public static readonly string[] ReservedShortcuts = { "Ctrl+K" };

        public static string Title(QuickAction a, bool italian) => italian ? a.TitleIt : a.TitleEn;

        /// <summary>Palette title with the shortcut appended so people learn it where they already look.</summary>
        public static string PaletteTitle(QuickAction a, bool italian) =>
            a.ShortcutText is null ? Title(a, italian) : Title(a, italian) + "  (" + a.ShortcutText + ")";
    }
}
