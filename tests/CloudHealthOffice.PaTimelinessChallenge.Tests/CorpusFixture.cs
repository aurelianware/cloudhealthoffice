using CloudHealthOffice.Tools.PaTimelinessChallenge;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

internal static class CorpusFixture
{
    public static readonly DateTimeOffset AsOf = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public static PaCorpusProfile Profile(int count = 2_000, int seed = 57) => new()
    {
        Count = count,
        Seed = seed,
        AsOf = AsOf,
    };

    private static readonly Lazy<IReadOnlyList<PaCase>> Default = new(() => PaCorpusGenerator.Generate(Profile()));

    public static IReadOnlyList<PaCase> Cases => Default.Value;
}
