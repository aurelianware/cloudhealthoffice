using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi.DTOs;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;

public class StediPayerMapperTests
{
    [Fact]
    public void MapsDto_ToCanonicalPayer_WithGenericExternalIdentifiers()
    {
        var dto = SampleDto();
        var payer = StediPayerMapper.ToCanonical(dto, DateTimeOffset.UnixEpoch);

        payer.Should().NotBeNull();
        payer!.Id.Should().Be("AAAAA");
        payer.Name.Should().Be("Synthetic Alpha Plan");
        payer.Aliases.Should().Contain(new[] { "SYN-A", "60054", "Synthetic Alpha" });
        payer.Provenance.Source.Should().Be("stedi");
        payer.Provenance.LastSyncedAt.Should().Be(DateTimeOffset.UnixEpoch);

        payer.ExternalIdentifiers.Should().Contain(id =>
            id.System == "stedi" && id.Type == "id" && id.Value == "AAAAA");
        payer.ExternalIdentifiers.Should().Contain(id =>
            id.System == "stedi" && id.Type == "tradingPartnerServiceId" && id.Value == "60054");
        payer.ExternalIdentifiers.Should().Contain(id =>
            id.System == "stedi" && id.Type == "primaryPayerId" && id.Value == "60054");

        typeof(PayerReference).GetProperty("StediPayerId").Should().BeNull();
        typeof(PayerReference).GetProperty("StediTradingPartnerId").Should().BeNull();
    }

    [Fact]
    public void MapsTransactionSupport_AndEnrollmentSeparately()
    {
        var payer = StediPayerMapper.ToCanonical(SampleDto(), DateTimeOffset.UnixEpoch)!;

        Support(payer, HealthcareTransactionType.Eligibility270271)
            .Should().Be(PayerTransactionSupport.Supported);
        Support(payer, HealthcareTransactionType.ProfessionalClaim837P)
            .Should().Be(PayerTransactionSupport.Supported);
        Support(payer, HealthcareTransactionType.InstitutionalClaim837I)
            .Should().Be(PayerTransactionSupport.NotSupported);
        Support(payer, HealthcareTransactionType.Remittance835)
            .Should().Be(PayerTransactionSupport.EnrollmentRequired);

        var era = payer.EnrollmentRequirements.Should().ContainSingle(e =>
            e.Transaction == HealthcareTransactionType.Remittance835).Subject;
        era.Required.Should().BeTrue();
        era.ProcessType.Should().Be("ONE_CLICK");
        era.Timeframe.Should().Be("DAYS");

        payer.EnrollmentRequirements.Should().NotContain(e =>
            e.Transaction == HealthcareTransactionType.Eligibility270271 && e.Required);
    }

    [Fact]
    public void MapsAliases_FromNamesAndPrimaryPayerId()
    {
        var payer = StediPayerMapper.ToCanonical(SampleDto(), DateTimeOffset.UnixEpoch)!;
        payer.Aliases.Should().NotContain(payer.Name);
    }

    [Fact]
    public void MissingRequiredFields_ReturnsNull()
    {
        StediPayerMapper.ToCanonical(new StediPayerDto { DisplayName = "X" }, DateTimeOffset.UnixEpoch)
            .Should().BeNull();
        StediPayerMapper.ToCanonical(new StediPayerDto { StediId = "X" }, DateTimeOffset.UnixEpoch)
            .Should().BeNull();
    }

    [Fact]
    public void EligibilityEnrollmentRequired_IsRecordedOnBothSurfaces()
    {
        var dto = SampleDto();
        dto.TransactionSupport!.EligibilityCheck = "ENROLLMENT_REQUIRED";

        var payer = StediPayerMapper.ToCanonical(dto, DateTimeOffset.UnixEpoch)!;

        Support(payer, HealthcareTransactionType.Eligibility270271)
            .Should().Be(PayerTransactionSupport.EnrollmentRequired);
        payer.EnrollmentRequirements.Should().Contain(e =>
            e.Transaction == HealthcareTransactionType.Eligibility270271 && e.Required);
    }

    private static PayerTransactionSupport Support(PayerReference payer, HealthcareTransactionType tx) =>
        payer.SupportedTransactions.Single(t => t.Transaction == tx).Support;

    private static StediPayerDto SampleDto() => new()
    {
        StediId = "AAAAA",
        DisplayName = "Synthetic Alpha Plan",
        PrimaryPayerId = "60054",
        Aliases = new List<string> { "SYN-A", "60054" },
        Names = new List<string> { "Synthetic Alpha" },
        CoverageTypes = new List<string> { "medical" },
        TransactionSupport = new StediTransactionSupportDto
        {
            EligibilityCheck = "SUPPORTED",
            ClaimStatus = "SUPPORTED",
            ClaimSubmission = "SUPPORTED",
            ClaimPayment = "ENROLLMENT_REQUIRED",
            CoordinationOfBenefits = "NOT_SUPPORTED",
            DentalClaimSubmission = "NOT_SUPPORTED",
            InstitutionalClaimSubmission = "NOT_SUPPORTED",
            ProfessionalClaimSubmission = "SUPPORTED",
            UnsolicitedClaimAttachment = "NOT_SUPPORTED"
        },
        Enrollment = new StediEnrollmentDto
        {
            PtanRequired = false,
            TransactionEnrollmentProcesses = new Dictionary<string, StediEnrollmentProcessDto>
            {
                ["claimPayment"] = new() { Type = "ONE_CLICK", Timeframe = "DAYS" }
            }
        },
        Urls = new StediPayerUrlsDto { Website = "https://example.test/synthetic-alpha" }
    };
}
