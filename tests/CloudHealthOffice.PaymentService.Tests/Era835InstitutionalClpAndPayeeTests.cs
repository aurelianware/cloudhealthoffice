using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// 005010X221A1 CLP08 (facility type code), CLP09 (claim frequency code) and
/// CLP11 (DRG) for institutional claims, carried from claims-service's
/// <c>institutional</c> / <c>claimFrequencyCode</c> through
/// <see cref="ClaimDto"/> and <see cref="ClaimPayment"/>, in payments and
/// reversals; and the N1*PE payee name from <c>billingProviderName</c>.
/// </summary>
public class Era835InstitutionalClpAndPayeeTests
{
    private static readonly ICarcRarcMappingService Mapper =
        new CarcRarcMappingService(NullLogger<CarcRarcMappingService>.Instance);

    /// <summary>An inpatient DRG claim paid at claim level: 42,500 billed, 15,525 paid, 2,475 member.</summary>
    private static ClaimDto InpatientClaim(string? facility = "11", string? frequency = "1", string? drg = "470") => new()
    {
        Id = "ip-1", ClaimNumber = "IP-0001", BillingProviderNPI = "1999999984", MemberId = "M1",
        ProviderName = "SYNTHETIC GENERAL HOSPITAL", PayerClaimControlNumber = "ICN-IP",
        Status = ClaimStatus.Paid, ClaimType = ClaimFormType.Institutional, TotalChargeAmount = 42500m,
        ClaimFrequencyCode = frequency,
        Institutional = new InstitutionalClaimDto { FacilityTypeCode = facility, DrgCode = drg },
        AdjudicationResult = new ClaimAdjudicationDto
        {
            PayerPayment = 15525m, PatientResponsibility = 2475m,
            AdjustmentReasons = new List<ClaimAdjustmentReasonDto>
            {
                new() { GroupCode = "CO", ReasonCode = "45", Amount = 24500m },
                new() { GroupCode = "PR", ReasonCode = "1", Amount = 500m },
                new() { GroupCode = "PR", ReasonCode = "2", Amount = 1975m },
            },
        },
    };

    private static string ClpOf(ClaimPayment cp)
    {
        var segments = 0;
        return Era835ClaimLoops.BuildClaimLoop(cp, ref segments).Split('~')[0];
    }

    [Fact]
    public void InstitutionalClaim_Clp_CarriesFacilityFrequencyAndDrg()
    {
        var cp = Era835ClaimPaymentBuilder.Build(InpatientClaim(), denied: false, Mapper);

        Assert.Equal(("11", "1", "470"), (cp.FacilityTypeCode, cp.ClaimFrequencyCode, cp.DrgCode));
        Assert.Equal("CLP*IP-0001*1*42500.00*15525.00*2475.00*HM*ICN-IP*11*1**470", ClpOf(cp));
    }

    [Fact]
    public void InstitutionalClaim_WithoutDrg_ClpEndsAtClp09()
    {
        var cp = Era835ClaimPaymentBuilder.Build(InpatientClaim(facility: "13", drg: null), denied: false, Mapper);

        Assert.Equal("CLP*IP-0001*1*42500.00*15525.00*2475.00*HM*ICN-IP*13*1", ClpOf(cp));
    }

    [Fact]
    public void InstitutionalDenial_ClpCarriesTheCodes()
    {
        var claim = InpatientClaim(frequency: "7");
        claim.AdjudicationResult!.PayerPayment = 0m;
        claim.AdjudicationResult.DenialReasonCode = "50";

        var cp = Era835ClaimPaymentBuilder.Build(claim, denied: true, Mapper);

        Assert.StartsWith("CLP*IP-0001*4*42500.00*0.00*", ClpOf(cp));
        Assert.EndsWith("*HM*ICN-IP*11*7**470", ClpOf(cp));
    }

    [Fact]
    public void ProfessionalClaim_ClpEndsAtClp07_EvenWithInstitutionalDetail()
    {
        var claim = InpatientClaim();
        claim.ClaimType = ClaimFormType.Professional;

        var cp = Era835ClaimPaymentBuilder.Build(claim, denied: false, Mapper);

        Assert.Null(cp.FacilityTypeCode);
        Assert.Null(cp.ClaimFrequencyCode);
        Assert.Null(cp.DrgCode);
        Assert.Equal("CLP*IP-0001*1*42500.00*15525.00*2475.00*HM*ICN-IP", ClpOf(cp));
    }

    [Fact]
    public void StoredInstitutionalPayment_WithoutTheCodes_StillGenerates_ClpEndsAtClp07()
    {
        var cp = Era835ClaimPaymentBuilder.Build(InpatientClaim(facility: null, frequency: null, drg: null), denied: false, Mapper);

        Assert.True(cp.IsInstitutional);
        Assert.Equal("CLP*IP-0001*1*42500.00*15525.00*2475.00*HM*ICN-IP", ClpOf(cp));
    }

    [Fact]
    public void StoredPaymentDocument_WithoutTheFields_DeserializesWithNulls()
    {
        // A ClaimPayment stored before CLP08/09/11 were carried.
        const string json = """
            {"claimId":"ip-1","patientControlNumber":"IP-0001","claimStatusCode":"1","chargeAmount":100,
             "paymentAmount":100,"patientResponsibilityAmount":0,"isInstitutional":true}
            """;

        var cp = JsonSerializer.Deserialize<ClaimPayment>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Null(cp.FacilityTypeCode);
        Assert.Equal("CLP*IP-0001*1*100.00*100.00*0.00*HM*ip-1", ClpOf(cp));
    }

    [Fact]
    public void Reversal_RepeatsTheOriginalsClp08Clp09Clp11()
    {
        var claim = InpatientClaim();
        var original = Era835ClaimPaymentBuilder.Build(claim, denied: false, Mapper);

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, claim, Mapper);

        Assert.Equal("CLP*IP-0001*22*-42500.00*-15525.00*-2475.00*HM*ICN-IP*11*1**470", ClpOf(reversal));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(reversal));
    }

    [Fact]
    public void Reversal_OfAStoredPaymentWithoutTheCodes_TakesThemFromThePredecessor()
    {
        var original = Era835ClaimPaymentBuilder.Build(InpatientClaim(facility: null, frequency: null, drg: null), denied: false, Mapper);

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, InpatientClaim(), Mapper);

        Assert.EndsWith("*HM*ICN-IP*11*1**470", ClpOf(reversal));
        Assert.Null(original.FacilityTypeCode); // the recorded payment is left as recorded
    }

    [Fact]
    public void Reversal_OfAProfessionalClaim_NeverGetsInstitutionalCodes()
    {
        var pred = InpatientClaim();
        pred.ClaimType = ClaimFormType.Professional;
        var original = Era835ClaimPaymentBuilder.Build(pred, denied: false, Mapper);

        var reversal = Era835ClaimPaymentBuilder.BuildReversal(original, pred, Mapper);

        Assert.Equal("CLP*IP-0001*22*-42500.00*-15525.00*-2475.00*HM*ICN-IP", ClpOf(reversal));
    }

    [Fact]
    public void ClaimsServiceWire_BillingProviderNameAndInstitutionalDetail_Deserialize()
    {
        const string json = """
            {"id":"ip-1","claimNumber":"IP-0001","billingProviderNPI":"1999999984",
             "billingProviderName":"SYNTHETIC GENERAL HOSPITAL","claimType":2,"claimFrequencyCode":"1",
             "institutional":{"facilityTypeCode":"11","drgCode":"470","admissionTypeCode":"3"}}
            """;

        var dto = JsonSerializer.Deserialize<ClaimDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("SYNTHETIC GENERAL HOSPITAL", dto.ProviderName);
        Assert.Equal("SYNTHETIC GENERAL HOSPITAL", dto.PayeeNameOr(dto.BillingProviderNPI));
        Assert.Equal(ClaimFormType.Institutional, dto.ClaimType);
        Assert.Equal("1", dto.ClaimFrequencyCode);
        Assert.Equal(("11", "470"), (dto.Institutional!.FacilityTypeCode, dto.Institutional.DrgCode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PayeeName_WithoutBillingProviderName_FallsBackToTheGivenValue(string? name)
    {
        var dto = new ClaimDto { BillingProviderNPI = "1234567893", ProviderName = name };

        Assert.Equal("1234567893", dto.PayeeNameOr(dto.BillingProviderNPI));
    }

    [Fact]
    public void PayeeName_IsTrimmed()
    {
        Assert.Equal("ACME CLINIC", new ClaimDto { ProviderName = "  ACME CLINIC " }.PayeeNameOr("x"));
    }

    // ── Pay-to provider: N102 never names a different organization than N104 ──

    [Fact]
    public void DistinctPayToNpi_PayeeNameIsThePayToNpi_NotTheBillingProviderName()
    {
        var dto = new ClaimDto
        {
            BillingProviderNPI = "1234567893", PayToProviderNPI = "1999999976", ProviderName = "ACME BILLING GROUP",
        };

        Assert.True(dto.HasDistinctPayToProvider);
        Assert.Equal("1999999976", dto.PayeeNameOr(dto.BillingProviderNPI));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567893")]
    [InlineData(" 1234567893 ")]
    public void PayToNpiAbsentOrSameAsBilling_PayeeNameIsTheBillingProviderName(string? payTo)
    {
        var dto = new ClaimDto { BillingProviderNPI = "1234567893", PayToProviderNPI = payTo, ProviderName = "ACME CLINIC" };

        Assert.False(dto.HasDistinctPayToProvider);
        Assert.Equal("ACME CLINIC", dto.PayeeNameOr(dto.BillingProviderNPI));
    }

    // ── N102 length: 60 in 005010; billingProviderName can be 300 ──────────

    private static Payment PayeeOnly(string payeeName, bool reversal = false)
    {
        var cp = new ClaimPayment
        {
            ClaimId = "c1", PatientControlNumber = "CLM-1", ClaimStatusCode = reversal ? "22" : "1",
            ChargeAmount = reversal ? -100m : 100m, PaymentAmount = reversal ? -100m : 100m,
        };
        return new Payment
        {
            CheckNumber = "0001000001", PaymentMethod = "CHK", TotalPaymentAmount = cp.PaymentAmount,
            PaymentDate = new DateTime(2026, 4, 2), PayerName = "CHO", PayerId = "CHO",
            PayeeName = payeeName, PayeeNPI = "1234567893", IsReversal = reversal,
            ClaimPayments = new List<ClaimPayment> { cp },
        };
    }

    private static string N102(string edi) =>
        edi.Split('~').Select(s => s.Trim().Split('*')).Single(s => s[0] == "N1" && s[1] == "PE")[2];

    private static string BatchN102(Payment payment) => N102(
        new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance)
            .GenerateBatch(new[] { new EraPaymentInput { TradingPartnerId = "TP", Payment = payment } },
                new Dictionary<string, TradingPartnerInfo> { ["TP"] = new() { OriginatingCompanyId = "1123456789" } })
            .Single().EdiContent);

    private static string SingleN102(Payment payment) => N102(
        new EraGeneratorService(NullLogger<EraGeneratorService>.Instance)
            .Generate835(payment, new TradingPartnerInfo { OriginatingCompanyId = "1123456789" }));

    [Theory]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(300, 60)]
    public void PayeeName_IsCutAt60_InPaymentReversalAndSingleEraPaths(int length, int expected)
    {
        var name = string.Concat(Enumerable.Range(0, length).Select(i => (char)('A' + i % 26)));

        foreach (var n102 in new[] { BatchN102(PayeeOnly(name)), BatchN102(PayeeOnly(name, reversal: true)), SingleN102(PayeeOnly(name)) })
        {
            Assert.Equal(expected, n102.Length);
            Assert.Equal(name[..expected], n102);
        }
    }

    [Fact]
    public void N102_TrimsBeforeAndAfterTheCut_AndReplacesDelimiters()
    {
        // 59 characters then a space at position 60: the cut leaves no trailing space.
        var name = "  " + new string('A', 59) + " B" + new string('C', 20);

        Assert.Equal(new string('A', 59), Era835Names.N102(name));
        Assert.Equal("ACME CLINIC  LLC", Era835Names.N102(" ACME*CLINIC~:LLC "));
        Assert.Equal(string.Empty, Era835Names.N102(null));
    }
}
