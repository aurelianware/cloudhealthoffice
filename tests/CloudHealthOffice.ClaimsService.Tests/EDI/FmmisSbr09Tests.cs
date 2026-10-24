using ClaimsService.EDI.Florida;
using ClaimsService.EDI.Florida.Models;
using ClaimsService.Models;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

/// <summary>
/// SBR09, the claim filing indicator code, in the FMMIS 837 generator
/// (plan-configuration.md §12.14).
///
/// <para>
/// The third generator found with the same defect, and the only one of the
/// three that submits for a live programme. It wrote "MC" at SBR07, two
/// elements early — an element the TR3 marks "not used", with SBR09 left
/// absent. The code itself was never in question here: FL Medicaid is always
/// MC, which is why it is a companion-guide constant. Only its position was
/// wrong.
/// </para>
/// </summary>
public class FmmisSbr09Tests
{
    private const string ProviderNpi = "1234567890";
    private const string FlMedicaidProviderId = "FL-MCD-00001";

    private static string[] Sbr(ClaimType claimType = ClaimType.Professional)
    {
        var edi = FmmisClaimTransformer.BuildEdi(
            Claim(claimType),
            new FmmisComplianceConfigDto
            {
                FmmisSubmitterId = "FLMCO00001",
                FmmisInterchangeSenderId = "FLMCO00001",
            },
            FlMedicaidProviderId,
            "000000001",
            FmmisCompanionGuide.VersionCode837P,
            new DateTime(2026, 7, 25, 5, 18, 0, DateTimeKind.Utc));

        return edi
            .Split('~', StringSplitOptions.RemoveEmptyEntries)
            .First(s => s.TrimStart().StartsWith("SBR*", StringComparison.Ordinal))
            .TrimStart()
            .Split('*');
    }

    private static Claim Claim(ClaimType claimType) => new()
    {
        TenantId = "t1",
        ClaimNumber = "CLM-FL-0001",
        MemberId = "MBR19-SAMPLE",
        SubscriberId = "MBR19-SAMPLE",
        SubscriberFirstName = "TEST",
        SubscriberLastName = "MEMBER",
        BillingProviderNPI = ProviderNpi,
        BillingProviderName = "E2E FL SPECIALIST GROUP",
        PlaceOfServiceCode = "11",
        ClaimType = claimType,
        LineOfBusiness = LineOfBusiness.Medicaid,
        ClaimFrequencyCode = "1",
        TotalChargeAmount = 200.00m,
        ServiceDateFrom = new DateTime(2026, 7, 25),
        ServiceDateTo = new DateTime(2026, 7, 25),
        Status = ClaimStatus.Approved,
        AdjudicatedDate = new DateTime(2026, 7, 25),
        DiagnosisCodes = new List<DiagnosisCode>
        {
            new() { Code = "J06.9", CodeQualifier = "ABK", PointerNumber = 1 },
        },
        ClaimLines = new List<ClaimLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                ChargeAmount = 200.00m,
                Units = 1,
                ServiceDateFrom = new DateTime(2026, 7, 25),
                ServiceDateTo = new DateTime(2026, 7, 25),
                PlaceOfServiceCode = "11",
                DiagnosisPointers = new List<int> { 1 },
            },
        },
    };

    [Fact]
    public void The_claim_filing_indicator_is_at_SBR09()
    {
        // Counted, not matched as a substring: "MC" was present before this
        // change too, just in the wrong element, so a Contains assertion would
        // have passed throughout the defect's life.
        var sbr = Sbr();

        Assert.Equal(10, sbr.Length);
        Assert.Equal("MC", sbr[9]);
    }

    [Fact]
    public void The_elements_the_TR3_does_not_use_are_left_empty()
    {
        var sbr = Sbr();

        Assert.All(new[] { 3, 4, 5, 6, 7, 8 }, i => Assert.Equal(string.Empty, sbr[i]));
    }

    [Fact]
    public void SBR01_and_SBR02_keep_their_positions()
    {
        // Nothing before SBR09 moves: the two codes that were already right
        // have to stay where a receiver reads them.
        var sbr = Sbr();

        Assert.Equal(FmmisCompanionGuide.PayerResponsibilityPrimary, sbr[1]);
        Assert.Equal("18", sbr[2]);
    }

    [Theory]
    [InlineData(ClaimType.Professional)]
    [InlineData(ClaimType.Institutional)]
    public void Both_837_variants_file_as_Medicaid(ClaimType claimType)
    {
        // Unlike encounter-service, there is no Medicare split to make here:
        // every FMMIS submission is Florida Medicaid by definition.
        Assert.Equal("MC", Sbr(claimType)[9]);
    }
}
