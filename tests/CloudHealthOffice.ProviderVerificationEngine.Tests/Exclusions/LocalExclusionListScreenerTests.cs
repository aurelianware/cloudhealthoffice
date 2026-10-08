using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions.ExclusionTestData;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions;

public class LocalExclusionListScreenerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryExclusionRecordStore _store = new();
    private readonly FixedTimeProvider _time = new(Now);
    private readonly ExclusionScreeningOptions _options = Options();

    private LocalExclusionListScreener Screener() => new(
        ExclusionScreeningSource.OigLeie, _store, Wrap(_options),
        NullLogger<LocalExclusionListScreener>.Instance, _time);

    [Fact]
    public async Task ExactNpiMatch_IsExcluded_WithFullConfidence()
    {
        await SeedLeieAsync(_store, Now.AddDays(-3), LeieRow(last: "DOE", first: "JOHN", npi: "1234567893"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.True(outcome.IsExcluded);
        var match = Assert.Single(outcome.Matches);
        Assert.Equal(1.0f, match.MatchConfidence);
        Assert.Equal("NPI", match.MatchBasis);
        Assert.Equal(Now.AddDays(-3), outcome.Status.DataAsOf);
    }

    [Fact]
    public async Task ReinstatedProvider_IsNotExcluded()
    {
        await SeedLeieAsync(_store, Now.AddDays(-3),
            LeieRow(last: "DOE", first: "JOHN", npi: "1234567893", reinDate: "20240601"));

        var outcome = await Screener().ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893", FirstName = "John", LastName = "Doe" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.False(outcome.IsExcluded);
        Assert.Empty(outcome.Matches);
    }

    [Fact]
    public async Task FutureReinstatementDate_StillExcluded()
    {
        await SeedLeieAsync(_store, Now.AddDays(-3),
            LeieRow(last: "DOE", first: "JOHN", npi: "1234567893", reinDate: "20990101"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.True(outcome.IsExcluded);
    }

    [Fact]
    public async Task NpiMatchWithWaiver_IsPossibleMatch_NotDenial()
    {
        await SeedLeieAsync(_store, Now.AddDays(-3),
            LeieRow(last: "DOE", first: "JOHN", npi: "1234567893", waiverDate: "20230101", waiverState: "TX"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.IsExcluded);
        Assert.True(Assert.Single(outcome.Matches).MatchConfidence >= 0.7f);
    }

    [Fact]
    public async Task NameAndDobMatch_IsPossibleMatch_ForManualReview()
    {
        // LEIE row has no NPI (common for older exclusions).
        await SeedLeieAsync(_store, Now.AddDays(-3), LeieRow(last: "O'BRIEN", first: "MARY", dob: "19700412"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest
        {
            Npi = "1497758544",
            FirstName = "Mary",
            LastName = "OBrien",
            DateOfBirth = new DateTimeOffset(1970, 4, 12, 0, 0, 0, TimeSpan.Zero)
        }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.False(outcome.IsExcluded);
        var match = Assert.Single(outcome.Matches);
        Assert.True(match.MatchConfidence >= 0.7f && match.MatchConfidence < 1.0f);
        Assert.Equal("NAME_DOB", match.MatchBasis);
    }

    [Fact]
    public async Task NameMatch_WithDifferentDob_IsNotAMatch()
    {
        await SeedLeieAsync(_store, Now.AddDays(-3), LeieRow(last: "SMITH", first: "JOHN", dob: "19500101"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest
        {
            Npi = "1497758544", FirstName = "John", LastName = "Smith",
            DateOfBirth = new DateTimeOffset(1980, 6, 1, 0, 0, 0, TimeSpan.Zero)
        }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.Empty(outcome.Matches);
    }

    [Fact]
    public async Task NameMatch_WithoutDob_IsReviewMatch_UnlessListedNpiDiffers()
    {
        await SeedLeieAsync(_store, Now.AddDays(-3),
            LeieRow(last: "SMITH", first: "JOHN"),                        // no NPI, no DOB
            LeieRow(last: "SMITH", first: "JOHN", npi: "1234567893"));    // namesake with another NPI

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest
        {
            Npi = "1497758544", FirstName = "JOHN", LastName = "SMITH"
        }, default);

        var match = Assert.Single(outcome.Matches);
        Assert.Equal(0.7f, match.MatchConfidence);
        Assert.Null(match.Npi);
        Assert.False(outcome.IsExcluded);
    }

    [Fact]
    public async Task OrganizationNameMatch_AfterNormalization_IsPossibleMatch()
    {
        await SeedLeieAsync(_store, Now.AddDays(-3), LeieRow(bus: "THE ACME HOME HEALTH, L.L.C."));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest
        {
            Npi = "1497758544", OrganizationName = "Acme Home Health LLC"
        }, default);

        Assert.False(outcome.IsExcluded);
        var match = Assert.Single(outcome.Matches);
        Assert.Equal("BUSINESS_NAME", match.MatchBasis);
        Assert.True(match.MatchConfidence >= 0.7f);
    }

    [Fact]
    public async Task OrganizationName_OnExcludedIndividualRow_IsReviewOnly()
    {
        // LEIE person rows can carry the business the individual worked for.
        await SeedLeieAsync(_store, Now.AddDays(-3), LeieRow(last: "DOE", first: "JOHN", bus: "ACME PHARMACY INC"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest
        {
            Npi = "1497758544", OrganizationName = "Acme Pharmacy"
        }, default);

        Assert.False(outcome.IsExcluded);
        Assert.Equal(0.7f, Assert.Single(outcome.Matches).MatchConfidence);
    }

    [Fact]
    public async Task StaleDataset_ReportsNotScreened_AndNeverDenies()
    {
        await SeedLeieAsync(_store, Now.AddDays(-36), LeieRow(last: "DOE", first: "JOHN", npi: "1234567893"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("stale", outcome.Status.Note);
        Assert.False(outcome.IsExcluded);
        // The hit is still surfaced for review, never dropped.
        Assert.True(Assert.Single(outcome.Matches).MatchConfidence is >= 0.7f and < 1.0f);
    }

    [Fact]
    public async Task StaleDataset_WithNoHit_IsNotScreened_NotClear()
    {
        await SeedLeieAsync(_store, Now.AddDays(-40), LeieRow(last: "DOE", first: "JOHN", npi: "1234567893"));

        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest { Npi = "1497758544" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Empty(outcome.Matches);
    }

    [Fact]
    public async Task NeverSynced_ReportsNotScreened()
    {
        var outcome = await Screener().ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("never been synced", outcome.Status.Note);
    }

    [Fact]
    public async Task StoreFailure_ReportsNotScreened()
    {
        var broken = NSubstitute.Substitute.For<IExclusionRecordStore>();
        NSubstitute.ExceptionExtensions.ExceptionExtensions
            .ThrowsAsync(broken.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie, NSubstitute.Arg.Any<CancellationToken>()),
                new TimeoutException("mongo down"));
        var screener = new LocalExclusionListScreener(ExclusionScreeningSource.OigLeie, broken, Wrap(_options),
            NullLogger<LocalExclusionListScreener>.Instance, _time);

        var outcome = await screener.ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
    }
}
