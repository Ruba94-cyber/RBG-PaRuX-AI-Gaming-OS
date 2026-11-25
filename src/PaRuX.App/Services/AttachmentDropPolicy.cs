using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    public enum DroppedKind { Image, Video, Audio, Document, Folder, Unsupported }

    public enum DropTarget { Chat, Knowledge }

    /// <summary>One item taken from a drag-and-drop. <paramref name="IsLink"/> is a reparse point / shortcut: never followed.</summary>
    public sealed record DroppedItem(string Name, bool IsFolder, long SizeBytes, bool IsLink = false);

    /// <summary>Attachments already waiting in the composer (chat only).</summary>
    public sealed record DropCounts(int Images = 0, int Videos = 0, int Audio = 0, int Documents = 0);

    public sealed record DropLimits(
        int MaxImages, int MaxVideos, int MaxAudio, int MaxDocuments,
        long MaxImageBytes, long MaxVideoBytes, long MaxAudioBytes, long MaxDocumentBytes)
    {
        /// <summary>Same defaults as the Attach button (Settings: images 4 x 20 MB, video 1 x 500 MB, documents 4 x 100 MB; audio 1 x 25 MB).</summary>
        public static DropLimits Defaults { get; } = new(4, 1, 1, 4, 20L << 20, 500L << 20, ChatAudioAttachmentPolicy.MaxBytes, 100L << 20);
    }

    public sealed record DropDecision(DroppedItem Item, DroppedKind Kind, bool Accepted, string ReasonEn, string ReasonIt);

    public sealed record DropPlan(DropTarget Target, IReadOnlyList<DropDecision> Decisions)
    {
        public IReadOnlyList<DropDecision> Accepted => Decisions.Where(d => d.Accepted).ToList();
        public IReadOnlyList<DropDecision> Rejected => Decisions.Where(d => !d.Accepted).ToList();

        /// <summary>One honest sentence: what was taken and what was left out and why (at most three reasons spelled out).</summary>
        public string Summary(bool italian)
        {
            if (Decisions.Count == 0) return italian ? "Nessun elemento da aggiungere." : "Nothing to add.";
            int ok = Accepted.Count;
            var parts = new List<string>();
            if (ok > 0)
                parts.Add(Target == DropTarget.Chat
                    ? (italian ? ok + " allegato/i pronto/i da inviare." : ok + " attachment(s) ready to send.")
                    : (italian ? ok + " elemento/i aggiunto/i a Conoscenza." : ok + " item(s) added to Knowledge."));
            string left = RejectedSummary(italian);
            if (left.Length > 0) parts.Add(left);
            return string.Join(" ", parts);
        }

        /// <summary>Only the refusals, each with its reason (at most three spelled out); empty when nothing was refused.</summary>
        public string RejectedSummary(bool italian)
        {
            IReadOnlyList<DropDecision> rejected = Rejected;
            var parts = new List<string>();
            foreach (DropDecision d in rejected.Take(3))
                parts.Add(d.Item.Name + ": " + (italian ? d.ReasonIt : d.ReasonEn));
            if (rejected.Count > 3)
                parts.Add(italian ? "e altri " + (rejected.Count - 3) + " non aggiunti." : "and " + (rejected.Count - 3) + " more not added.");
            return string.Join(" ", parts);
        }

        /// <summary>Short text for the drop hint shown while dragging (keyboard users get the same wording on the Attach button).</summary>
        public string DragCaption(bool italian)
        {
            int ok = Accepted.Count;
            if (ok == 0) return italian ? "Nessun elemento supportato" : "No supported item";
            return Target == DropTarget.Chat
                ? (italian ? "Allega " + ok + " elemento/i" : "Attach " + ok + " item(s)")
                : (italian ? "Aggiungi " + ok + " a Conoscenza" : "Add " + ok + " to Knowledge");
        }
    }

    /// <summary>
    /// Pure decision logic for files and folders dropped on the chat composer or on the Knowledge page: kind by extension, per-kind size
    /// and count limits (the same ones the Attach button uses), links never followed, and a plain bilingual reason for every refusal.
    /// Nothing is read or copied here; the views act only on what is accepted.
    /// </summary>
    public static class AttachmentDropPolicy
    {
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
        public static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mov", ".m4v" };
        public static readonly string[] DocumentExtensions =
        {
            ".pdf", ".docx", ".pptx", ".xls", ".xlsx", ".xlsb", ".csv", ".tsv", ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".log",
            ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".ps1", ".sql", ".html", ".css"
        };

        public static DroppedKind Classify(string name, bool isFolder)
        {
            if (isFolder) return DroppedKind.Folder;
            string ext = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
            if (ImageExtensions.Contains(ext)) return DroppedKind.Image;
            if (VideoExtensions.Contains(ext)) return DroppedKind.Video;
            if (ChatAudioAttachmentPolicy.MediaTypeFor(ext) != null) return DroppedKind.Audio;
            if (DocumentExtensions.Contains(ext)) return DroppedKind.Document;
            return DroppedKind.Unsupported;
        }

        public static DropPlan Plan(IEnumerable<DroppedItem> items, DropTarget target, DropCounts? existing = null, DropLimits? limits = null)
        {
            DropLimits lim = limits ?? DropLimits.Defaults;
            DropCounts have = existing ?? new DropCounts();
            int images = have.Images, videos = have.Videos, audio = have.Audio, docs = have.Documents;
            var decisions = new List<DropDecision>();

            foreach (DroppedItem item in items)
            {
                DroppedKind kind = Classify(item.Name, item.IsFolder);
                DropDecision Reject(string en, string it) => new(item, kind, false, en, it);
                DropDecision Accept() => new(item, kind, true, "ok", "ok");

                if (item.IsLink) { decisions.Add(Reject("shortcuts and links are not followed; drop the real file.", "collegamenti e scorciatoie non vengono seguiti; trascina il file vero.")); continue; }

                if (target == DropTarget.Knowledge)
                {
                    if (kind == DroppedKind.Folder) { decisions.Add(Accept()); continue; }
                    if (kind == DroppedKind.Document)
                    {
                        decisions.Add(item.SizeBytes <= 0 ? Reject("the file is empty.", "il file è vuoto.")
                            : item.SizeBytes > 4L << 30 ? Reject("larger than 4 GB.", "oltre 4 GB.")
                            : Accept());
                        continue;
                    }
                    decisions.Add(kind is DroppedKind.Image or DroppedKind.Video or DroppedKind.Audio
                        ? Reject("images, audio and video are not indexed as knowledge; attach them in a chat instead.", "immagini, audio e video non vengono indicizzati come conoscenza; allegali in una chat.")
                        : Reject("file type not supported for indexing (documents, PDF, spreadsheets, text and code are).", "tipo di file non indicizzabile (sono supportati documenti, PDF, fogli di calcolo, testo e codice)."));
                    continue;
                }

                // Chat composer
                switch (kind)
                {
                    case DroppedKind.Folder:
                        decisions.Add(Reject("folders cannot be attached to a message; add it on the Knowledge page and mention it with #name.", "le cartelle non si allegano a un messaggio; aggiungila nella pagina Conoscenza e citala con #nome."));
                        break;
                    case DroppedKind.Unsupported:
                        decisions.Add(Reject("file type not supported (images, audio, video, documents, PDF, spreadsheets, text and code are).", "tipo di file non supportato (sono supportati immagini, audio, video, documenti, PDF, fogli di calcolo, testo e codice)."));
                        break;
                    case DroppedKind.Image:
                        if (item.SizeBytes <= 0) decisions.Add(Reject("the file is empty.", "il file è vuoto."));
                        else if (images >= lim.MaxImages) decisions.Add(Reject("image limit per message reached (" + lim.MaxImages + ").", "limite di immagini per messaggio raggiunto (" + lim.MaxImages + ")."));
                        else if (item.SizeBytes > lim.MaxImageBytes) decisions.Add(Reject("larger than " + Mb(lim.MaxImageBytes) + " MB.", "oltre " + Mb(lim.MaxImageBytes) + " MB."));
                        else { images++; decisions.Add(Accept()); }
                        break;
                    case DroppedKind.Video:
                        if (item.SizeBytes <= 0) decisions.Add(Reject("the file is empty.", "il file è vuoto."));
                        else if (videos >= lim.MaxVideos) decisions.Add(Reject("one video per message.", "un solo video per messaggio."));
                        else if (item.SizeBytes > lim.MaxVideoBytes) decisions.Add(Reject("larger than " + Mb(lim.MaxVideoBytes) + " MB.", "oltre " + Mb(lim.MaxVideoBytes) + " MB."));
                        else { videos++; decisions.Add(Accept()); }
                        break;
                    case DroppedKind.Audio:
                        if (item.SizeBytes <= 0) decisions.Add(Reject("the file is empty.", "il file è vuoto."));
                        else if (audio >= lim.MaxAudio) decisions.Add(Reject("one audio clip per message.", "una sola clip audio per messaggio."));
                        else if (item.SizeBytes > lim.MaxAudioBytes) decisions.Add(Reject("larger than " + Mb(lim.MaxAudioBytes) + " MB; trim it or split it.", "oltre " + Mb(lim.MaxAudioBytes) + " MB; accorciala o dividila."));
                        else { audio++; decisions.Add(Accept()); }
                        break;
                    default:
                        if (item.SizeBytes <= 0) decisions.Add(Reject("the file is empty.", "il file è vuoto."));
                        else if (docs >= lim.MaxDocuments) decisions.Add(Reject("document limit per message reached (" + lim.MaxDocuments + ").", "limite di documenti per messaggio raggiunto (" + lim.MaxDocuments + ")."));
                        else if (item.SizeBytes > lim.MaxDocumentBytes) decisions.Add(Reject("larger than " + Mb(lim.MaxDocumentBytes) + " MB.", "oltre " + Mb(lim.MaxDocumentBytes) + " MB."));
                        else { docs++; decisions.Add(Accept()); }
                        break;
                }
            }
            return new DropPlan(target, decisions);
        }

        private static long Mb(long bytes) => Math.Max(1, bytes / (1024 * 1024));
    }
}
