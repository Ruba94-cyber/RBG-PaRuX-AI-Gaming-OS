using System.Linq;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelBadgePresenterTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static RecommendedModel M(string id, long gb, string profile = ModelContentProfileIds.Standard) =>
        new() { Id = id, Title = id, EstimatedSizeBytes = gb * Gb, ContentProfileId = profile };

    private static string[] Texts(RecommendedModel m, ModelFitVerdict v, (RecommendedModel, ModelFitVerdict)[] all, bool it = false) =>
        ModelBadgePresenter.For(m, v, all, it).Select(b => b.Text).ToArray();

    [Fact]
    public void Small_fitting_model_is_fast_and_the_largest_fitting_one_is_best_quality()
    {
        var small = M("small", 3);
        var large = M("large", 9);
        var huge = M("huge", 40);
        var all = new[] { (small, ModelFitVerdict.Fits), (large, ModelFitVerdict.Fits), (huge, ModelFitVerdict.Big) };

        Assert.Equal(new[] { "Fits your PC", "Fast" }, Texts(small, ModelFitVerdict.Fits, all));
        Assert.Equal(new[] { "Fits your PC", "Best quality" }, Texts(large, ModelFitVerdict.Fits, all));
        Assert.Equal(new[] { "Too big for your PC" }, Texts(huge, ModelFitVerdict.Big, all));
    }

    [Fact]
    public void Uncensored_badge_follows_the_content_profile_and_italian_is_translated_without_jargon()
    {
        var u = M("u", 4, ModelContentProfileIds.Uncensored);
        var all = new[] { (u, ModelFitVerdict.Fits) };
        Assert.Contains("Uncensored", Texts(u, ModelFitVerdict.Fits, all));
        string[] it = Texts(u, ModelFitVerdict.Fits, all, it: true);
        Assert.Equal(new[] { "Adatto al tuo PC", "Veloce", "Senza censura" }, it);
        foreach (ModelBadge b in ModelBadgePresenter.For(u, ModelFitVerdict.Fits, all, false))
        {
            Assert.DoesNotContain("quant", b.Text + b.Explanation, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GGUF", b.Text + b.Explanation);
        }
    }

    [Fact]
    public void Unknown_fit_gets_no_fit_claim_and_a_single_fitting_model_is_not_called_best()
    {
        var only = M("only", 8);
        Assert.Empty(Texts(only, ModelFitVerdict.Unknown, new[] { (only, ModelFitVerdict.Unknown) }));
        Assert.DoesNotContain("Best quality", Texts(only, ModelFitVerdict.Fits, new[] { (only, ModelFitVerdict.Fits) }));
    }

    [Fact]
    public void Empty_chat_call_to_action_names_the_recommended_model_or_falls_back_to_a_plain_label()
    {
        var advice = new FirstModelAdvice
        {
            RecommendedId = "m1",
            VramBytes = 16 * Gb,
            Candidates = { new FirstModelCandidate { Model = new RecommendedModel { Id = "m1", Title = "Starter 8B" }, Suitability = "FitsOnGpu", Rank = 3 } }
        };
        Assert.Equal("Get a model — recommended: Starter 8B", FirstModelRecommendationPresenter.GetModelButtonText(advice, false));
        Assert.Equal("Scarica un modello — consigliato: Starter 8B", FirstModelRecommendationPresenter.GetModelButtonText(advice, true));
        Assert.Equal("Get a model", FirstModelRecommendationPresenter.GetModelButtonText(null, false));
        Assert.Contains("one click", FirstModelRecommendationPresenter.NoModelDescription(null, false));
        Assert.DoesNotContain("error", FirstModelRecommendationPresenter.NoModelDescription(advice, false), System.StringComparison.OrdinalIgnoreCase);
    }
}
