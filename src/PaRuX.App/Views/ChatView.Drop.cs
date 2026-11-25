using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PaRuX.Views
{
    /// <summary>
    /// Drop files on the chat (or paste files from Explorer with Ctrl+V, the keyboard path; the Attach buttons remain the other one).
    /// What is accepted is decided by <see cref="AttachmentDropPolicy"/> with the same limits as the Attach button; every refusal is said
    /// in plain words and accepted files show up as the usual attachment chips (name, size, remove button) before anything is sent.
    /// </summary>
    public sealed partial class ChatView
    {
        private void InitFileDrop()
        {
            AllowDrop = true;
            // handledEventsToo: the composer TextBox marks its own drag events handled for text.
            AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnChatFileDragOver), true);
            AddHandler(UIElement.DropEvent, new DragEventHandler(OnChatFileDrop), true);
            InputBox.Paste += OnComposerPaste;
        }

        private DropLimits CurrentDropLimits()
        {
            AppSettings s = SettingsStore.Current.Data;
            return new DropLimits(
                Math.Max(1, s.MaxImagesPerMessage), 1, 1, Math.Max(1, s.MaxDocumentsPerMessage),
                Math.Max(1, s.MaxImageMegabytes) * 1024L * 1024L, Math.Max(1, s.MaxVideoMegabytes) * 1024L * 1024L,
                ChatAudioAttachmentPolicy.MaxBytes, Math.Max(1, s.MaxDocumentMegabytes) * 1024L * 1024L);
        }

        private DropCounts CurrentDropCounts()
        {
            int images = _pendingAttachments.Count(a => a.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
            int videos = _pendingAttachments.Count(a => a.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);
            int audio = _pendingAttachments.Count(a => ChatAudioAttachmentPolicy.IsAudioMediaType(a.MediaType));
            return new DropCounts(images, videos, audio, _pendingAttachments.Count - images - videos - audio);
        }

        private async void OnChatFileDragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            DragOperationDeferral deferral = e.GetDeferral();
            try
            {
                e.AcceptedOperation = ChatSession.Current.IsBusy ? DataPackageOperation.None : DataPackageOperation.Copy;
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                DropPlan plan = await PlanAsync(items, CurrentDropCounts(), CurrentDropLimits());
                e.DragUIOverride.Caption = ChatSession.Current.IsBusy
                    ? T("Wait for the reply to finish", "Attendi la fine della risposta")
                    : plan.DragCaption(UiText.IsItalian);
                e.DragUIOverride.IsGlyphVisible = false;
                if (plan.Accepted.Count == 0) e.AcceptedOperation = DataPackageOperation.None;
            }
            catch (Exception ex) { Log.Write("Chat drag-over failed: " + ex.Message); }
            finally { deferral.Complete(); }
        }

        private async void OnChatFileDrop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            DragOperationDeferral deferral = e.GetDeferral();
            try
            {
                if (ChatSession.Current.IsBusy)
                {
                    ShowTransientStatus(T("Wait for the reply to finish before attaching files.", "Attendi la fine della risposta prima di allegare file."));
                    return;
                }
                await AttachStorageItemsAsync(await e.DataView.GetStorageItemsAsync());
            }
            catch (Exception ex)
            {
                Log.Write("Chat drop failed: " + ex);
                ShowTransientStatus(T("Couldn't attach the dropped items: ", "Impossibile allegare gli elementi trascinati: ") + ex.Message);
            }
            finally { deferral.Complete(); }
        }

        private async void OnComposerPaste(object sender, TextControlPasteEventArgs e)
        {
            try
            {
                DataPackageView view = Clipboard.GetContent();
                if (!view.Contains(StandardDataFormats.StorageItems)) return;   // plain text paste: untouched
                e.Handled = true;
                if (ChatSession.Current.IsBusy) return;
                await AttachStorageItemsAsync(await view.GetStorageItemsAsync());
            }
            catch (Exception ex) { Log.Write("Composer paste of files failed: " + ex.Message); }
        }

        private static async Task<DropPlan> PlanAsync(IReadOnlyList<IStorageItem> items, DropCounts? counts, DropLimits? limits)
        {
            var dropped = new List<DroppedItem>();
            foreach (IStorageItem item in items)
            {
                if (item is StorageFile file)
                {
                    long size = 0;
                    try { size = (long)(await file.GetBasicPropertiesAsync()).Size; } catch (Exception) { }
                    bool link = false;
                    try { link = (File.GetAttributes(file.Path) & System.IO.FileAttributes.ReparsePoint) != 0; } catch (Exception) { }
                    dropped.Add(new DroppedItem(file.Name, false, size, link));
                }
                else dropped.Add(new DroppedItem(item.Name, true, 0));
            }
            return AttachmentDropPolicy.Plan(dropped, DropTarget.Chat, counts, limits);
        }

        /// <summary>Applies the policy, copies each accepted file into PaRuX's attachments folder and adds the chip.</summary>
        private async Task AttachStorageItemsAsync(IReadOnlyList<IStorageItem> items)
        {
            DropPlan plan = await PlanAsync(items, CurrentDropCounts(), CurrentDropLimits());
            StorageFolder destination = await StorageFolder.GetFolderFromPathAsync(AppPaths.AttachmentsDir);
            int attached = 0;
            var failed = new List<string>();
            for (int i = 0; i < items.Count && i < plan.Decisions.Count; i++)
            {
                DropDecision decision = plan.Decisions[i];
                if (!decision.Accepted || items[i] is not StorageFile file) continue;
                try
                {
                    StorageQuotaService.EnsureCapacity(decision.Item.SizeBytes);
                    string ext = Path.GetExtension(file.Name).ToLowerInvariant();
                    StorageFile stored = await file.CopyAsync(destination, Guid.NewGuid().ToString("N") + ext, NameCollisionOption.FailIfExists);
                    var attachment = new ChatAttachment
                    {
                        FileName = file.Name,
                        LocalPath = stored.Path,
                        MediaType = decision.Kind == DroppedKind.Audio ? ChatAudioAttachmentPolicy.MediaTypeFor(ext)! : MediaTypeFor(ext),
                        SizeBytes = decision.Item.SizeBytes
                    };
                    if (decision.Kind == DroppedKind.Audio)
                    {
                        TimeSpan? duration = null;
                        if (ext == ".wav")
                        {
                            try { using var fs = new FileStream(stored.Path, FileMode.Open, FileAccess.Read, FileShare.Read); duration = ChatAudioAttachmentPolicy.TryReadWavDuration(fs); }
                            catch (IOException) { }
                        }
                        _audioDurations[attachment.Id] = duration;
                    }
                    _pendingAttachments.Add(attachment);
                    attached++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(file.Name + ": " + ex.Message);
                }
            }
            if (attached > 0) { RefreshAttachmentPanel(); UpdateRouteLabel(); InputBox.Focus(FocusState.Programmatic); }

            // Honest result: what was attached, what was refused and why, what failed to copy.
            string summary = AttachedSummary(attached, plan, failed);
            if (!string.IsNullOrWhiteSpace(summary)) ShowTransientStatus(summary);
        }

        private static string AttachedSummary(int attached, DropPlan plan, IReadOnlyList<string> failed)
        {
            var parts = new List<string>();
            if (attached > 0) parts.Add(UiText.Pick(attached + " attachment(s) ready to send.", attached + " allegato/i pronto/i da inviare."));
            foreach (DropDecision d in plan.Rejected.Take(3))
                parts.Add(d.Item.Name + ": " + UiText.Pick(d.ReasonEn, d.ReasonIt));
            if (plan.Rejected.Count > 3)
                parts.Add(UiText.Pick("and " + (plan.Rejected.Count - 3) + " more not attached.", "e altri " + (plan.Rejected.Count - 3) + " non allegati."));
            foreach (string f in failed.Take(2)) parts.Add(UiText.Pick("Copy failed: ", "Copia non riuscita: ") + f);
            return string.Join(" ", parts);
        }
    }
}
