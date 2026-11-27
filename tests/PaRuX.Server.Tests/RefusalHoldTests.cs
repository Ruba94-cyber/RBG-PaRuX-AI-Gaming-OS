using System.Text;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The refusal-detection hold buffers the first reply text so an auto-uncensored retry can replace a refusal before it is shown.
/// The adaptive rule must reach the same verdict as the fixed window for every reply that opens with a refusal, and must release
/// ordinary replies earlier than the fixed 100 ms window.
/// </summary>
public sealed class RefusalHoldTests
{
    private static readonly string[] Refusals =
    {
        "I'm sorry, but I can't help with that request.",
        "I can't help with that.",
        "I cannot provide instructions for that, as it violates my guidelines.",
        "As an AI language model, I am not able to assist with this.",
        "I'm unable to comply with this request. Please ask something else.",
        "I must decline to write that.",
        "I won't generate that content.",
        "Sorry, I can't do that.",
        "I'm not comfortable producing this, so I'll stop here.",
        "Mi dispiace, ma non posso aiutarti con questa richiesta.",
        "Non posso soddisfare questa richiesta perche esula dalle mie linee guida.",
        "Come modello di intelligenza artificiale non posso farlo.",
        "Non mi sento a mio agio nel rispondere a questa domanda.",
        "I don't feel comfortable doing that, sorry.",
        "Unfortunately, I can't fulfill this request.",
    };

    private static readonly string[] Normal =
    {
        "Sure! Here is a short summary of the report, with the three main findings listed below.",
        "The capital of France is Paris, and it has been the capital since the tenth century.",
        "Certo! Ecco un riepilogo breve del documento, con i punti principali elencati qui sotto.",
        "To reverse a list in Python you can call the reverse method or use slicing with a negative step.",
        "Photosynthesis converts light energy into chemical energy stored in glucose molecules.",
        "# Plan\n\n1. Read the file\n2. Parse the header\n3. Write the output to disk",
        "Purtroppo non ho accesso al tuo fuso orario, quindi non so che ore sono.",
        "Here's a recipe for pancakes: mix flour, eggs and milk, then fry in a hot pan.",
    };

    /// <summary>Streams the reply 4 characters at a time, 5 ms per chunk (about 200 tok/s). Returns the verdict and the time held.</summary>
    private static (RefusalHold.Verdict Verdict, int HeldMilliseconds) Stream(string reply, bool adaptive)
    {
        var text = new StringBuilder();
        int elapsed = 0;
        for (int i = 0; i < reply.Length; i += 4)
        {
            text.Append(reply, i, Math.Min(4, reply.Length - i));
            elapsed += 5;
            bool done = i + 4 >= reply.Length;
            RefusalHold.Verdict verdict = RefusalHold.Evaluate(text.ToString(), windowElapsed: elapsed >= 100, done, adaptive);
            if (verdict != RefusalHold.Verdict.Hold) return (verdict, elapsed);
        }
        return (RefusalHold.Evaluate(text.ToString(), windowElapsed: true, done: true, adaptive), elapsed);
    }

    [Fact]
    public void Every_reply_that_opens_with_a_refusal_is_escalated_exactly_as_with_the_fixed_window()
    {
        foreach (string reply in Refusals)
        {
            Assert.Equal(RefusalHold.Verdict.Escalate, Stream(reply, adaptive: false).Verdict);
            Assert.Equal(RefusalHold.Verdict.Escalate, Stream(reply, adaptive: true).Verdict);
        }
    }

    [Fact]
    public void Every_ordinary_reply_gets_the_same_release_verdict_as_with_the_fixed_window()
    {
        foreach (string reply in Normal)
        {
            Assert.Equal(RefusalHold.Verdict.Release, Stream(reply, adaptive: false).Verdict);
            Assert.Equal(RefusalHold.Verdict.Release, Stream(reply, adaptive: true).Verdict);
        }
    }

    [Fact]
    public void Ordinary_replies_are_released_earlier_than_the_fixed_100_ms_window()
    {
        int fixedTotal = 0, adaptiveTotal = 0;
        foreach (string reply in Normal.Where(r => r.Length >= 60))
        {
            (_, int fixedHeld) = Stream(reply, adaptive: false);
            (_, int adaptiveHeld) = Stream(reply, adaptive: true);
            Assert.True(adaptiveHeld <= fixedHeld);
            fixedTotal += fixedHeld;
            adaptiveTotal += adaptiveHeld;
        }
        Assert.True(adaptiveTotal < fixedTotal * 0.75, "adaptive " + adaptiveTotal + " ms vs fixed " + fixedTotal + " ms");
    }

    [Fact]
    public void A_refusal_is_never_released_early_while_its_cue_is_still_arriving()
    {
        // Every prefix of every refusal that ends before the decision window must still be held or escalated, never released.
        foreach (string reply in Refusals)
        {
            for (int length = 1; length <= Math.Min(reply.Length, 40); length++)
            {
                string prefix = reply[..length];
                RefusalHold.Verdict verdict = RefusalHold.Evaluate(prefix, windowElapsed: false, done: false, adaptive: true);
                if (verdict == RefusalHold.Verdict.Release)
                    Assert.Fail("released \"" + prefix + "\" of a refusal");
            }
        }
    }

    [Fact]
    public void Reasoning_free_short_replies_and_end_of_stream_still_use_the_original_rule()
    {
        Assert.Equal(RefusalHold.Verdict.Hold, RefusalHold.Evaluate("Sure", windowElapsed: false, done: false, adaptive: true));
        Assert.Equal(RefusalHold.Verdict.Release, RefusalHold.Evaluate("Sure", windowElapsed: false, done: true, adaptive: true));
        Assert.Equal(RefusalHold.Verdict.Escalate, RefusalHold.Evaluate("Sorry, I can't.", windowElapsed: false, done: true, adaptive: true));
        Assert.Equal(RefusalHold.Verdict.Escalate, RefusalHold.Evaluate("I can't help with that. " + new string('x', 240), windowElapsed: false, done: false, adaptive: true));
    }

    [Fact]
    public void A_cue_free_prefix_that_ends_inside_a_word_is_not_released_yet()
    {
        Assert.Equal(RefusalHold.Verdict.Hold, RefusalHold.Evaluate("The capital of France is Paris, and it is lar", windowElapsed: false, done: false, adaptive: true));
        Assert.Equal(RefusalHold.Verdict.Release, RefusalHold.Evaluate("The capital of France is Paris, and it is large ", windowElapsed: false, done: false, adaptive: true));
    }

    [Fact]
    public void Documented_boundary_a_refusal_after_thirty_two_cue_free_characters_is_not_held()
    {
        // The fixed window caught this only when it arrived inside 100 ms. The adaptive hold releases first. Pinned so the change is deliberate.
        const string late = "Let me think about the request here first. I can't help with that.";
        Assert.Equal(RefusalHold.Verdict.Escalate, Stream(late, adaptive: false).Verdict);
        Assert.Equal(RefusalHold.Verdict.Release, Stream(late, adaptive: true).Verdict);
    }

    [Fact]
    public void Skipping_evaluation_on_lines_without_new_text_never_changes_the_verdict_or_when_it_is_reached()
    {
        // Each text chunk is followed by a blank SSE separator line, as on the wire. The gated loop (what ChatProxyService runs)
        // must reach the same verdict after the same number of text chunks as the loop that evaluates on every line.
        foreach (bool adaptive in new[] { false, true })
        foreach (string reply in Refusals.Concat(Normal))
        {
            Assert.Equal(Run(reply, adaptive, gated: false), Run(reply, adaptive, gated: true));
        }

        static (RefusalHold.Verdict, int) Run(string reply, bool adaptive, bool gated)
        {
            var text = new StringBuilder();
            int elapsed = 0, chunks = 0;
            for (int i = 0; i < reply.Length; i += 4)
            {
                text.Append(reply, i, Math.Min(4, reply.Length - i));
                chunks++;
                elapsed += 5;
                bool done = i + 4 >= reply.Length;
                for (int line = 0; line < 2; line++) // data line, then blank separator
                {
                    bool changed = line == 0;
                    if (gated && !RefusalHold.ShouldEvaluate(changed, elapsed >= 100, done && line == 1)) continue;
                    RefusalHold.Verdict v = RefusalHold.Evaluate(text.ToString(), elapsed >= 100, done && line == 1, adaptive);
                    if (v != RefusalHold.Verdict.Hold) return (v, chunks);
                }
            }
            return (RefusalHold.Evaluate(text.ToString(), true, true, adaptive), chunks);
        }
    }

    [Fact]
    public void ShouldEvaluate_is_false_only_for_a_line_that_cannot_change_a_previous_hold()
    {
        Assert.False(RefusalHold.ShouldEvaluate(textChanged: false, windowElapsed: false, done: false));
        Assert.True(RefusalHold.ShouldEvaluate(true, false, false));
        Assert.True(RefusalHold.ShouldEvaluate(false, true, false));
        Assert.True(RefusalHold.ShouldEvaluate(false, false, true));
    }
}
