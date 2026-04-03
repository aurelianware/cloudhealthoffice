using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using CloudHealthOffice.ProviderVerificationEngine.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests;

public class ProviderVerificationOrchestratorTests
{
    private readonly INppesAdapter _nppes = Substitute.For<INppesAdapter>();
    private readonly IExclusionScreeningAdapter _exclusions = Substitute.For<IExclusionScreeningAdapter>();
    private readonly IPecosAdapter _pecos = Substitute.For<IPecosAdapter>();
    private readonly IOpenPaymentsAdapter _openPayments = Substitute.For<IOpenPaymentsAdapter>();
    private readonly IMedicareUtilizationAdapter _utilization = Substitute.For<IMedicareUtilizationAdapter>();
    private readonly INlmTaxonomyCrosswalkAdapter _taxonomyCrosswalk = Substitute.For<INlmTaxonomyCrosswalkAdapter>();
    private readonly IFsmbAdapter _fsmb = Substitute.For<IFsmbAdapter>();
    private readonly ProviderVerificationOrchestrator _orchestrator;

    public ProviderVerificationOrchestratorTests()
    {
        var scoringWeights = Options.Create(new ScoringWeights());
        var verificationOptions = Options.Create(new VerificationOptions());
        var calculator = new IntegrityScoreCalculator(scoringWeights, verificationOptions);
        var options = Options.Create(new VerificationOptions());

        _fsmb.IsConfigured.Returns(false);

        _orchestrator = new ProviderVerificationOrchestrator(
            _nppes, _exclusions, _pecos, _openPayments,
            _utilization, _taxonomyCrosswalk, _fsmb,
            calculator,
            NullLogger<ProviderVerificationOrchestrator>.Instance,
            options);
    }

    [Fact]
    public async Task BasicTier_OnlyCallsNppes()
    {
        var nppesData = CreateActiveNppesData();
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(nppesData);

        var result = await _orchestrator.VerifyProviderAsync("1234567893", VerificationTier.Basic);

        Assert.NotNull(result.NppesData);
        Assert.Equal("1234567893", result.Npi);
        await _nppes.Received(1).LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>());
        await _exclusions.DidNotReceive().ScreenProviderAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await _pecos.DidNotReceive().GetEnrollmentStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _openPayments.DidNotReceive().GetPaymentSummaryAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _utilization.DidNotReceive().GetUtilizationProfileAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StandardTier_CallsAllTier2Sources()
    {
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(CreateActiveNppesData());
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult { IsExcluded = false, Source = ExclusionScreeningSource.OigLeie });
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(new PecosEnrollmentStatus { IsEnrolledInMedicare = true });
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        var result = await _orchestrator.VerifyProviderAsync("1234567893", VerificationTier.Standard);

        await _nppes.Received(1).LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>());
        await _exclusions.Received(1).ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>());
        await _pecos.Received(1).GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>());
        await _openPayments.Received(1).GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>());
        await _utilization.Received(1).GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>());
        Assert.NotNull(result.ExclusionScreening);
        Assert.NotNull(result.PecosStatus);
    }

    [Fact]
    public async Task PremiumTier_CallsFsmb_WhenConfigured()
    {
        _fsmb.IsConfigured.Returns(true);
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(CreateActiveNppesData());
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult { IsExcluded = false });
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);
        _fsmb.VerifyProviderAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(new FsmbLicenseVerification
            {
                Licenses = [new StateLicense { State = "TX", Status = LicenseStatus.Active }]
            });

        var result = await _orchestrator.VerifyProviderAsync("1234567893", VerificationTier.Premium);

        await _fsmb.Received(1).VerifyProviderAsync("1234567893", Arg.Any<CancellationToken>());
        Assert.NotNull(result.FsmbVerification);
        Assert.Single(result.FsmbVerification.Licenses);
    }

    [Fact]
    public async Task PremiumTier_SkipsFsmb_WhenNotConfigured()
    {
        _fsmb.IsConfigured.Returns(false);
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(CreateActiveNppesData());
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult { IsExcluded = false });
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        var result = await _orchestrator.VerifyProviderAsync("1234567893", VerificationTier.Premium);

        await _fsmb.DidNotReceive().VerifyProviderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Null(result.FsmbVerification);
    }

    [Fact]
    public async Task NpiNotFound_ReturnsFailedStatus()
    {
        _nppes.LookupByNpiAsync("9999999999", Arg.Any<CancellationToken>())
            .Returns((NppesProviderData?)null);
        _exclusions.ScreenProviderAsync("9999999999", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult { IsExcluded = false });
        _pecos.GetEnrollmentStatusAsync("9999999999", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("9999999999", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("9999999999", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        var result = await _orchestrator.VerifyProviderAsync("9999999999");

        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Null(result.NppesData);
    }

    [Fact]
    public async Task ExcludedProvider_ReturnsExcludedStatus()
    {
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(CreateActiveNppesData());
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult
            {
                IsExcluded = true,
                Source = ExclusionScreeningSource.OigLeie,
                Matches = [new ExclusionMatch { Source = ExclusionScreeningSource.OigLeie, MatchConfidence = 1.0f }]
            });
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        var result = await _orchestrator.VerifyProviderAsync("1234567893");

        Assert.Equal(VerificationStatus.Excluded, result.Status);
        Assert.Equal(0, result.IntegrityScore.CompositeScore);
        Assert.Equal(IntegrityRating.Blocked, result.IntegrityScore.Rating);
    }

    [Fact]
    public async Task VerifyProvider_SetsReverificationSchedule()
    {
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(CreateActiveNppesData());
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult { IsExcluded = false });
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        var before = DateTimeOffset.UtcNow;
        var result = await _orchestrator.VerifyProviderAsync("1234567893");

        Assert.True(result.LastVerifiedAt >= before);
        Assert.NotNull(result.NextScheduledVerification);
        Assert.True(result.NextScheduledVerification > result.LastVerifiedAt);
    }

    [Fact]
    public async Task VerifyProvider_EnrichesTaxonomies_WhenPresent()
    {
        var nppesData = CreateActiveNppesData();
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(nppesData);
        _taxonomyCrosswalk.LookupTaxonomyAsync("207Q00000X", Arg.Any<CancellationToken>())
            .Returns(new TaxonomyCrosswalkResult
            {
                TaxonomyCode = "207Q00000X",
                MedicareProviderType = "Physician",
                MedicareSpecialtyCode = "08"
            });
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult { IsExcluded = false });
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        var result = await _orchestrator.VerifyProviderAsync("1234567893");

        Assert.Equal("Physician", result.NppesData!.Taxonomies[0].MedicareProviderType);
        Assert.Equal("08", result.NppesData.Taxonomies[0].MedicareSpecialtyCode);
    }

    [Fact]
    public async Task VerifyProvider_PartialFailure_ReturnsPartialResult()
    {
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(CreateActiveNppesData());
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("LEIE service unavailable"));
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        // Should not throw — partial results are acceptable
        var result = await _orchestrator.VerifyProviderAsync("1234567893");

        Assert.NotNull(result.NppesData);
        // Score is still calculated from available data
        Assert.True(result.IntegrityScore.CompositeScore >= 0);
    }

    [Fact]
    public async Task BatchVerify_ReturnsAllResults()
    {
        var npis = new[] { "1234567893", "1497758544" };

        foreach (var npi in npis)
        {
            _nppes.LookupByNpiAsync(npi, Arg.Any<CancellationToken>())
                .Returns(new NppesProviderData
                {
                    Npi = npi,
                    NpiStatus = NppesNpiStatus.Active,
                    Taxonomies = [new NppesTaxonomy { Code = "207Q00000X", IsPrimary = true }],
                    Addresses = [new NppesAddress { AddressPurpose = "LOCATION" }]
                });
            _exclusions.ScreenProviderAsync(npi, ct: Arg.Any<CancellationToken>())
                .Returns(new ExclusionScreeningResult { IsExcluded = false });
            _pecos.GetEnrollmentStatusAsync(npi, Arg.Any<CancellationToken>())
                .Returns((PecosEnrollmentStatus?)null);
            _openPayments.GetPaymentSummaryAsync(npi, ct: Arg.Any<CancellationToken>())
                .Returns((OpenPaymentsSummary?)null);
            _utilization.GetUtilizationProfileAsync(npi, ct: Arg.Any<CancellationToken>())
                .Returns((MedicareUtilizationProfile?)null);
        }

        var results = new List<ProviderVerificationRecord>();
        await foreach (var record in _orchestrator.BatchVerifyAsync(npis))
        {
            results.Add(record);
        }

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Npi == "1234567893");
        Assert.Contains(results, r => r.Npi == "1497758544");
    }

    [Fact]
    public async Task DeactivatedNpi_ReturnsExpiredStatus()
    {
        _nppes.LookupByNpiAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns(new NppesProviderData
            {
                Npi = "1234567893",
                NpiStatus = NppesNpiStatus.Deactivated,
                Taxonomies = [new NppesTaxonomy { Code = "207Q00000X", IsPrimary = true }],
                Addresses = [new NppesAddress { AddressPurpose = "LOCATION" }]
            });
        _exclusions.ScreenProviderAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns(new ExclusionScreeningResult { IsExcluded = false });
        _pecos.GetEnrollmentStatusAsync("1234567893", Arg.Any<CancellationToken>())
            .Returns((PecosEnrollmentStatus?)null);
        _openPayments.GetPaymentSummaryAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((OpenPaymentsSummary?)null);
        _utilization.GetUtilizationProfileAsync("1234567893", ct: Arg.Any<CancellationToken>())
            .Returns((MedicareUtilizationProfile?)null);

        var result = await _orchestrator.VerifyProviderAsync("1234567893");

        Assert.Equal(VerificationStatus.Expired, result.Status);
    }

    private static NppesProviderData CreateActiveNppesData() => new()
    {
        Npi = "1234567893",
        NpiStatus = NppesNpiStatus.Active,
        EnumerationDate = new DateTimeOffset(2010, 5, 1, 0, 0, 0, TimeSpan.Zero),
        Taxonomies = [new NppesTaxonomy { Code = "207Q00000X", Description = "Family Medicine", IsPrimary = true }],
        Addresses = [new NppesAddress { AddressPurpose = "LOCATION", City = "Austin", State = "TX" }]
    };
}
