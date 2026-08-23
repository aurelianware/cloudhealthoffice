using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;

public class PayerReferenceServiceTests
{
    [Fact]
    public async Task GetById_ReturnsCanonicalPayer()
    {
        var service = PayerTestHarness.CreateService();

        var payer = await service.GetByIdAsync(SyntheticPayerSeed.EligibleId);

        payer.Should().NotBeNull();
        payer!.Name.Should().Be("Synthetic Eligible Payer");
    }

    [Fact]
    public async Task Search_ByAlias_ReturnsMatchesWithoutPickingAWinner()
    {
        var service = PayerTestHarness.CreateService();

        var results = await service.SearchAsync(new PayerSearchQuery { Text = SyntheticPayerSeed.SharedAlias });

        results.Select(p => p.Id).Should().BeEquivalentTo(new[]
        {
            SyntheticPayerSeed.DuplicateAId,
            SyntheticPayerSeed.DuplicateBId
        });
    }

    [Fact]
    public async Task ResolveExternalIdentifier_ExactMatch()
    {
        var service = PayerTestHarness.CreateService();

        var result = await service.ResolveExternalIdentifierAsync(
            "stedi", "tradingPartnerServiceId", SyntheticPayerSeed.EligibleTradingPartnerId);

        result.Status.Should().Be(PayerResolutionStatus.Found);
        result.Payer!.Id.Should().Be(SyntheticPayerSeed.EligibleId);
    }

    [Fact]
    public async Task ResolveForTransaction_ByAlias_Succeeds()
    {
        var service = PayerTestHarness.CreateService();

        var result = await service.ResolveForTransactionAsync(
            "tenant-x", "AETNA", HealthcareTransactionType.Eligibility270271, "stedi", "tradingPartnerServiceId");

        result.Status.Should().Be(PayerResolutionStatus.Found);
        result.ExternalIdentifierValue.Should().Be(SyntheticPayerSeed.EligibleTradingPartnerId);
    }

    [Fact]
    public async Task ResolveForTransaction_NoMatch()
    {
        var service = PayerTestHarness.CreateService();

        var result = await service.ResolveForTransactionAsync(
            "tenant-x", "NOPE", HealthcareTransactionType.Eligibility270271, "stedi", "tradingPartnerServiceId");

        result.Status.Should().Be(PayerResolutionStatus.PayerNotFound);
    }

    [Fact]
    public async Task ResolveForTransaction_AmbiguousMatch()
    {
        var service = PayerTestHarness.CreateService();

        var result = await service.ResolveForTransactionAsync(
            "tenant-x",
            SyntheticPayerSeed.SharedAlias,
            HealthcareTransactionType.Eligibility270271,
            "stedi",
            "tradingPartnerServiceId");

        result.Status.Should().Be(PayerResolutionStatus.AmbiguousPayer);
    }

    [Fact]
    public async Task ResolveForTransaction_MissingExternalIdentifier()
    {
        var service = PayerTestHarness.CreateService();

        var result = await service.ResolveForTransactionAsync(
            "tenant-x",
            SyntheticPayerSeed.MissingExternalId,
            HealthcareTransactionType.Eligibility270271,
            "stedi",
            "tradingPartnerServiceId");

        result.Status.Should().Be(PayerResolutionStatus.ExternalIdentifierMissing);
    }

    [Fact]
    public async Task ResolveForTransaction_Unsupported()
    {
        var service = PayerTestHarness.CreateService();

        var result = await service.ResolveForTransactionAsync(
            "tenant-x",
            SyntheticPayerSeed.UnsupportedId,
            HealthcareTransactionType.Eligibility270271,
            "stedi",
            "tradingPartnerServiceId");

        result.Status.Should().Be(PayerResolutionStatus.TransactionUnsupported);
    }

    [Fact]
    public async Task ResolveForTransaction_EnrollmentRequired_UntilTenantEnrolled()
    {
        var store = PayerTestHarness.CreateStore();
        var service = PayerTestHarness.CreateService(store);

        var blocked = await service.ResolveForTransactionAsync(
            "tenant-x",
            SyntheticPayerSeed.EnrollmentId,
            HealthcareTransactionType.Eligibility270271,
            "stedi",
            "tradingPartnerServiceId");
        blocked.Status.Should().Be(PayerResolutionStatus.EnrollmentRequired);

        await service.SaveTenantOverrideAsync(new PayerTenantOverride
        {
            TenantId = "tenant-x",
            PayerId = SyntheticPayerSeed.EnrollmentId,
            EnrolledTransactions = { HealthcareTransactionType.Eligibility270271 }
        });

        var allowed = await service.ResolveForTransactionAsync(
            "tenant-x",
            SyntheticPayerSeed.EnrollmentId,
            HealthcareTransactionType.Eligibility270271,
            "stedi",
            "tradingPartnerServiceId");
        allowed.Status.Should().Be(PayerResolutionStatus.Found);
    }

    [Fact]
    public async Task TenantOverride_Disabled_BlocksResolution()
    {
        var store = PayerTestHarness.CreateStore();
        var service = PayerTestHarness.CreateService(store);
        await service.SaveTenantOverrideAsync(new PayerTenantOverride
        {
            TenantId = "tenant-x",
            PayerId = SyntheticPayerSeed.EligibleId,
            Enabled = false
        });

        var result = await service.ResolveForTransactionAsync(
            "tenant-x",
            SyntheticPayerSeed.EligibleId,
            HealthcareTransactionType.Eligibility270271,
            "stedi",
            "tradingPartnerServiceId");

        result.Status.Should().Be(PayerResolutionStatus.PayerDisabled);
    }

    [Fact]
    public async Task GetSupportedTransactions_ReturnsDirectoryCapabilities()
    {
        var service = PayerTestHarness.CreateService();

        var caps = await service.GetSupportedTransactionsAsync(SyntheticPayerSeed.EligibleId);

        caps.Should().Contain(c =>
            c.Transaction == HealthcareTransactionType.Eligibility270271 &&
            c.Support == PayerTransactionSupport.Supported);
    }
}
