using System;
using System.IO;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AttachmentDropPolicyTests
{
    private static DroppedItem F(string name, long size = 1024, bool link = false) => new(name, false, size, link);
    private static DroppedItem Dir(string name) => new(name, true, 0);

    [Theory]
    [InlineData("a.png", DroppedKind.Image)]
    [InlineData("A.JPG", DroppedKind.Image)]
    [InlineData("clip.mp4", DroppedKind.Video)]
    [InlineData("voice.wav", DroppedKind.Audio)]
    [InlineData("song.flac", DroppedKind.Audio)]
    [InlineData("report.pdf", DroppedKind.Document)]
    [InlineData("data.xlsx", DroppedKind.Document)]
    [InlineData("main.py", DroppedKind.Document)]
    [InlineData("setup.exe", DroppedKind.Unsupported)]
    [InlineData("archive.zip", DroppedKind.Unsupported)]
    [InlineData("noext", DroppedKind.Unsupported)]
    public void Files_are_classified_by_extension(string name, DroppedKind kind) =>
        Assert.Equal(kind, AttachmentDropPolicy.Classify(name, false));

    [Fact]
    public void A_folder_is_a_folder_whatever_its_name() => Assert.Equal(DroppedKind.Folder, AttachmentDropPolicy.Classify("photo.png", true));

    [Fact]
    public void Chat_accepts_every_supported_kind_within_limits()
    {
        DropPlan plan = AttachmentDropPolicy.Plan([F("a.png"), F("b.mp4"), F("c.wav"), F("d.pdf")], DropTarget.Chat);
        Assert.Equal(4, plan.Accepted.Count);
        Assert.Empty(plan.Rejected);
        Assert.Equal("", plan.RejectedSummary(false));
    }

    [Fact]
    public void Chat_refuses_unsupported_empty_and_linked_files_with_a_reason_in_both_languages()
    {
        DropPlan plan = AttachmentDropPolicy.Plan([F("virus.exe"), F("empty.png", 0), F("shortcut.pdf", 10, link: true)], DropTarget.Chat);
        Assert.Empty(plan.Accepted);
        Assert.Equal(3, plan.Rejected.Count);
        foreach (DropDecision d in plan.Rejected)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.ReasonEn));
            Assert.False(string.IsNullOrWhiteSpace(d.ReasonIt));
            Assert.NotEqual(d.ReasonEn, d.ReasonIt);
        }
        Assert.Contains("virus.exe", plan.Summary(false));
        Assert.Contains("non supportato", plan.Summary(true));
    }

    [Fact]
    public void Chat_enforces_counts_including_what_is_already_attached()
    {
        var limits = new DropLimits(2, 1, 1, 1, 20L << 20, 500L << 20, 25L << 20, 100L << 20);
        DropPlan plan = AttachmentDropPolicy.Plan([F("1.png"), F("2.png"), F("3.png"), F("a.pdf"), F("b.pdf"), F("v1.mp4"), F("v2.mp4"), F("a1.wav"), F("a2.mp3")],
            DropTarget.Chat, new DropCounts(Images: 1), limits);
        string[] accepted = plan.Accepted.Select(d => d.Item.Name).ToArray();
        Assert.Equal(["1.png", "a.pdf", "v1.mp4", "a1.wav"], accepted);   // one image slot left, then one of each other kind
        Assert.Equal(5, plan.Rejected.Count);
    }

    [Fact]
    public void Chat_enforces_per_kind_size_limits_and_uses_the_audio_cap()
    {
        DropPlan plan = AttachmentDropPolicy.Plan(
            [F("big.png", (20L << 20) + 1), F("ok.png", 20L << 20), F("long.wav", ChatAudioAttachmentPolicy.MaxBytes + 1), F("huge.pdf", (100L << 20) + 1)], DropTarget.Chat);
        Assert.Equal(["ok.png"], plan.Accepted.Select(d => d.Item.Name).ToArray());
        Assert.Contains("20 MB", plan.Rejected[0].ReasonEn);
        Assert.Contains("25 MB", plan.Rejected[1].ReasonEn);
    }

    [Fact]
    public void Chat_does_not_attach_folders_and_points_to_the_Knowledge_page()
    {
        DropPlan plan = AttachmentDropPolicy.Plan([Dir("My Docs")], DropTarget.Chat);
        DropDecision d = Assert.Single(plan.Rejected);
        Assert.Contains("Knowledge", d.ReasonEn);
        Assert.Contains("Conoscenza", d.ReasonIt);
    }

    [Fact]
    public void Knowledge_takes_folders_and_documents_and_explains_media_and_unknown_types()
    {
        DropPlan plan = AttachmentDropPolicy.Plan([Dir("Notes"), F("a.pdf"), F("b.md"), F("c.png"), F("d.mp4"), F("e.wav"), F("f.exe"), F("empty.txt", 0), F("link.docx", 5, true)], DropTarget.Knowledge);
        Assert.Equal(["Notes", "a.pdf", "b.md"], plan.Accepted.Select(d => d.Item.Name).ToArray());
        Assert.Equal(6, plan.Rejected.Count);
        Assert.Contains("attach them in a chat", plan.Rejected[0].ReasonEn);
        Assert.Contains("not supported for indexing", plan.Rejected[3].ReasonEn);
    }

    [Fact]
    public void Knowledge_has_no_per_message_count_limit_but_refuses_files_over_4_GB()
    {
        DropPlan plan = AttachmentDropPolicy.Plan(Enumerable.Range(0, 20).Select(i => F("d" + i + ".txt")).Append(F("huge.pdf", (4L << 30) + 1)), DropTarget.Knowledge);
        Assert.Equal(20, plan.Accepted.Count);
        Assert.Contains("4 GB", Assert.Single(plan.Rejected).ReasonEn);
    }

    [Fact]
    public void Summary_and_drag_caption_are_honest_and_bilingual()
    {
        DropPlan none = AttachmentDropPolicy.Plan([F("x.exe")], DropTarget.Chat);
        Assert.Equal("No supported item", none.DragCaption(false));
        Assert.Equal("Nessun elemento supportato", none.DragCaption(true));
        DropPlan some = AttachmentDropPolicy.Plan([F("x.png"), F("y.exe"), F("z.exe"), F("w.exe"), F("v.exe")], DropTarget.Chat);
        Assert.Equal("Attach 1 item(s)", some.DragCaption(false));
        string text = some.Summary(false);
        Assert.Contains("1 attachment(s) ready", text);
        Assert.Contains("and 1 more not added", text);   // at most three reasons are spelled out
        Assert.Equal("Nothing to add.", AttachmentDropPolicy.Plan([], DropTarget.Chat).Summary(false));
    }

    [Fact]
    public void Defaults_match_the_Attach_button_defaults_in_AppSettings()
    {
        PaRuX.Models.AppSettings s = new();
        DropLimits d = DropLimits.Defaults;
        Assert.Equal(s.MaxImagesPerMessage, d.MaxImages);
        Assert.Equal(s.MaxDocumentsPerMessage, d.MaxDocuments);
        Assert.Equal(s.MaxImageMegabytes * 1024L * 1024L, d.MaxImageBytes);
        Assert.Equal(s.MaxVideoMegabytes * 1024L * 1024L, d.MaxVideoBytes);
        Assert.Equal(s.MaxDocumentMegabytes * 1024L * 1024L, d.MaxDocumentBytes);
    }

    // ---- wiring contracts (source) ----

    private static string Read(params string[] relative)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(relative).ToArray()));
    }

    [Fact]
    public void The_chat_and_the_Knowledge_page_both_accept_drops_through_the_shared_policy()
    {
        string chat = Read("src", "PaRuX.App", "Views", "ChatView.Drop.cs");
        Assert.Contains("AllowDrop = true", chat);
        Assert.Contains("DropTarget.Chat", chat);
        Assert.Contains("AttachmentDropPolicy.Plan", chat);
        Assert.Contains("InputBox.Paste", chat);   // keyboard path: Ctrl+V of files copied in Explorer
        Assert.Contains("ChatSession.Current.IsBusy", chat);
        Assert.Contains("InitFileDrop();", Read("src", "PaRuX.App", "Views", "ChatView.xaml.cs"));

        string know = Read("src", "PaRuX.App", "Views", "DocumentSearchView.cs");
        Assert.Contains("DropTarget.Knowledge", know);
        Assert.Contains("RejectedSummary", know);
    }
}
