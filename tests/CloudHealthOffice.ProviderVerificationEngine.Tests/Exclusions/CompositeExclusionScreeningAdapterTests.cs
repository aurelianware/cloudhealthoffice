using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using CloudHealthOffice.ProviderVerificationEngine.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions;

public class CompositeExclusionScreeningAdapterTests
{
    private sealed class FakeSource(ExclusionScreeningSource source, Func<ExclusionSourceOutcome> outcome) : IExclusionSource
    {
        public ExclusionScreeningSource Source { get; } = source;
        public Task<ExclusionSourceOutcome> ScreenAsync(ProviderScreeningRequest request, CancellationToken ct) => Task.FromResult(outcome());
    }

    private sealed class ThrowingSource(ExclusionScreeningSource source) : IExclusionSource
    {
        public ExclusionScreeningSource Source { get; } = source;
        public Task<ExclusionSourceOutcome> ScreenAsync(ProviderScreeningRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private static IExclusionSource Clear(ExclusionScreeningSource s) => new FakeSource(s, () => new ExclusionSourceOutcome
    {
        Status = new ExclusionSourceScreening { Source = s, WasScreened = true, Mode = "LocalDataset", DataAsOf = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) }
    });

    private static IExclusionSource NotScreened(ExclusionScreeningSource s, string note) => new FakeSource(s, () => new ExclusionSourceOutcome
    {
        Status = new ExclusionSourceScreening { Source = s, WasScreened = false, Note = note }
    });

    private static IExclusionSource Excluded(ExclusionScreeningSource s) => new FakeSource(s, () => new ExclusionSourceOutcome
    {
        Status = new ExclusionSourceScreening { Source = s, WasScreened = true },
        IsExcluded = true,
        Matches = [new ExclusionMatch { Source = s, Npi = "1234567893", MatchConfidence = 1.0f, MatchBasis = "NPI" }]
    });

    private static CompositeExclusionScreeningAdapter Adapter(params IExclusionSource[] sources) =>
        new(sources, [], NullLogger<CompositeExclusionScreeningAdapter>.Instance);

    [Fact]
    public async Task AllSourcesScreened_IsScreened_AndRecordsEachSource()
    {
        var result = await Adapter(Clear(ExclusionScreeningSource.OigLeie), Clear(ExclusionScreeningSource.SamGov))
            .ScreenProviderAsync("1234567893");

        Assert.True(result.WasScreened);
        Assert.False(result.IsExcluded);
        Assert.Equal([ExclusionScreeningSource.OigLeie, ExclusionScreeningSource.SamGov],
            result.SourceResults.Select(r => r.Source));
    }

    [Fact]
    public async Task OneSourceNotScreened_IsNotScreened()
    {
        var result = await Adapter(Clear(ExclusionScreeningSource.OigLeie),
                NotScreened(ExclusionScreeningSource.SamGov, "SAM.gov unavailable (HTTP 503)"))
            .ScreenProviderAsync("1234567893");

        Assert.False(result.WasScreened);
        Assert.Contains(result.SourceResults, r => r.Source == ExclusionScreeningSource.SamGov && !r.WasScreened);
    }

    [Fact]
    public async Task ExclusionFromOneSource_WinsEvenIfOtherSourceFailed()
    {
        var result = await Adapter(Excluded(ExclusionScreeningSource.OigLeie), new ThrowingSource(ExclusionScreeningSource.SamGov))
            .ScreenProviderAsync("1234567893");

        Assert.True(result.IsExcluded);
        Assert.False(result.WasScreened);
        Assert.Equal(ExclusionScreeningSource.OigLeie, result.Source);
        Assert.Contains(result.SourceResults, r => r.Source == ExclusionScreeningSource.SamGov && r.Note!.Contains("failed"));
    }

    [Fact]
    public async Task NoSources_IsNotScreened()
    {
        var result = await Adapter().ScreenProviderAsync("1234567893");
        Assert.False(result.WasScreened);
    }

    [Fact]
    public async Task Scorer_SurfacesScreenedSourcesInDetail()
    {
        var screening = await Adapter(Clear(ExclusionScreeningSource.OigLeie), Clear(ExclusionScreeningSource.SamGov))
            .ScreenProviderAsync("1234567893");
        var calc = new IntegrityScoreCalculator(
            Microsoft.Extensions.Options.Options.Create(new ScoringWeights()),
            Microsoft.Extensions.Options.Options.Create(new VerificationOptions()));

        var score = calc.Calculate(new ProviderVerificationRecord { Npi = "1234567893", ExclusionScreening = screening });

        Assert.True(score.ExclusionScreening.WasEvaluated);
        Assert.Equal(100, score.ExclusionScreening.Score);
        Assert.Contains("OIG LEIE (data as of 2026-10-01)", score.ExclusionScreening.Detail);
        Assert.Contains("SAM.gov", score.ExclusionScreening.Detail);
    }

    [Fact]
    public async Task Scorer_PartialScreen_IsNotEvaluated_NamesMissingSource_AndKeepsPossibleMatch()
    {
        var possible = new FakeSource(ExclusionScreeningSource.OigLeie, () => new ExclusionSourceOutcome
        {
            Status = new ExclusionSourceScreening { Source = ExclusionScreeningSource.OigLeie, WasScreened = false, Note = "OIG LEIE dataset is stale" },
            Matches = [new ExclusionMatch { Source = ExclusionScreeningSource.OigLeie, MatchConfidence = 0.9f }]
        });
        var screening = await Adapter(possible, NotScreened(ExclusionScreeningSource.SamGov, "SAM.gov API key rejected (HTTP 401)"))
            .ScreenProviderAsync("1234567893");
        var calc = new IntegrityScoreCalculator(
            Microsoft.Extensions.Options.Options.Create(new ScoringWeights()),
            Microsoft.Extensions.Options.Options.Create(new VerificationOptions()));

        var score = calc.Calculate(new ProviderVerificationRecord { Npi = "1234567893", ExclusionScreening = screening });

        Assert.False(score.ExclusionScreening.WasEvaluated);
        Assert.Contains("SAM.gov — SAM.gov API key rejected", score.ExclusionScreening.Detail);
        Assert.Contains(score.Flags, f => f.Code == "EXCLUSION_NOT_SCREENED");
        Assert.Contains(score.Flags, f => f.Code == "POSSIBLE_EXCLUSION_MATCH");
        Assert.Equal(IntegrityRating.Unknown, score.Rating);
    }

    private static NppesProviderData ActiveNppes() => new()
    {
        Npi = "1234567893",
        NpiStatus = NppesNpiStatus.Active,
        Taxonomies = [new NppesTaxonomy { Code = "207Q00000X", IsPrimary = true }],
        Addresses = [new NppesAddress { AddressPurpose = "LOCATION" }]
    };

    private static IntegrityScoreCalculator Calculator() => new(
        Microsoft.Extensions.Options.Options.Create(new ScoringWeights()),
        Microsoft.Extensions.Options.Options.Create(new VerificationOptions()));

    [Fact]
    public async Task PartiallyScreenedComposite_RatesUnknown_EvenWhenOtherDimensionsAreClean()
    {
        // LEIE clear, SAM failed: the composite is not screened, so no rating
        // (favourable or Blocked) is knowable.
        var screening = await Adapter(Clear(ExclusionScreeningSource.OigLeie),
                NotScreened(ExclusionScreeningSource.SamGov, "SAM.gov unavailable (HTTP 503)"))
            .ScreenProviderAsync("1234567893");

        var score = Calculator().Calculate(new ProviderVerificationRecord
        {
            Npi = "1234567893", NppesData = ActiveNppes(), ExclusionScreening = screening
        });

        Assert.False(screening.WasScreened);
        Assert.Equal(IntegrityRating.Unknown, score.Rating);
        Assert.Contains(score.Flags, f => f.Code == "EXCLUSION_NOT_SCREENED");
    }

    [Fact]
    public async Task PartiallyScreenedComposite_WithConfirmedExclusion_IsBlocked()
    {
        var screening = await Adapter(Excluded(ExclusionScreeningSource.OigLeie),
                NotScreened(ExclusionScreeningSource.SamGov, "SAM.gov API key rejected (HTTP 401)"))
            .ScreenProviderAsync("1234567893");

        var score = Calculator().Calculate(new ProviderVerificationRecord
        {
            Npi = "1234567893", NppesData = ActiveNppes(), ExclusionScreening = screening
        });

        Assert.False(screening.WasScreened);
        Assert.True(screening.IsExcluded);
        Assert.Equal(IntegrityRating.Blocked, score.Rating);
        Assert.Contains(score.Flags, f => f.Code == "EXCLUDED");
    }
}
