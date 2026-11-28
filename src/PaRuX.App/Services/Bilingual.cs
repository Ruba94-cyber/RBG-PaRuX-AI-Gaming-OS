namespace PaRuX.Services
{
    /// <summary>An English source text and its Italian pair. English is the fallback; see <see cref="UiText"/>.</summary>
    public readonly record struct Bilingual(string En, string It)
    {
        public string Pick() => UiText.Pick(En, It);
        public string Pick(bool italian) => UiText.Pick(En, It, italian);
    }
}
