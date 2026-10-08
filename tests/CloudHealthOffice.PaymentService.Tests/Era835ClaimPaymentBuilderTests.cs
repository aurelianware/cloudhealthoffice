using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// The 835 adjustments <see cref="Era835ClaimPaymentBuilder"/> derives from a
/// claim: line CAS from the line's adjudication adjustments (with NCCI edits
/// merged, not double counted), the CO fallback for lines without detail,
/// and the denial CARC carrying whatever the other adjustments leave.
/// </summary>
public class Era835ClaimPaymentBuilderTests
{
    private static readonly ICarcRarcMappingService Mapper = new CarcRarcMappingService(NullLogger<CarcRarcMappingService>.Instance);

    private static ClaimLineAdjustmentReasonDto Adj(string group, string reason, decimal amount, string? remark = null) =>
        new() { GroupCode = group, ReasonCode = reason, Amount = amount, RemarkCode = remark };

    private static ClaimDto EngineClaim() => new()
    {
        Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", MemberId = "M1",
        Status = ClaimStatus.Approved, TotalChargeAmount = 300m,
        AdjudicationResult = new ClaimAdjudicationDto
        {
            PayerPayment = 170m, PatientResponsibility = 50m,
            // Claim-level totals of the line adjustments below: not repeated in the header CAS.
            AdjustmentReasons = new List<ClaimAdjustmentReasonDto>
            {
                new() { GroupCode = "CO", ReasonCode = "45", Amount = 80m },
                new() { GroupCode = "PR", ReasonCode = "1", Amount = 40m },
                new() { GroupCode = "PR", ReasonCode = "2", Amount = 10m },
            },
        },
        ServiceLines = new List<ClaimServiceLineDto>
        {
            new()
            {
                LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1,
                AdjudicationResult = new ClaimLineAdjudicationDto
                {
                    PaidAmount = 120m,
                    AdjustmentReasons = new List<ClaimLineAdjustmentReasonDto>
                    {
                        Adj("CO", "45", 50m), Adj("PR", "1", 20m, "N130"), Adj("PR", "2", 10m),
                    },
                },
            },
            new()
            {
                LineNumber = 2, ProcedureCode = "85025", ChargeAmount = 100m, Units = 1,
                AdjudicationResult = new ClaimLineAdjudicationDto
                {
                    PaidAmount = 50m,
                    AdjustmentReasons = new List<ClaimLineAdjustmentReasonDto> { Adj("CO", "45", 30m), Adj("PR", "1", 20m) },
                },
            },
        },
    };

    [Fact]
    public void LineAdjudicationAdjustments_BecomeLineCas_HeaderNotRepeated_Balanced()
    {
        var cp = Era835ClaimPaymentBuilder.Build(EngineClaim(), denied: false, Mapper);

        Assert.Empty(cp.ClaimAdjustments);
        var line1 = cp.ServiceLines[0].Adjustments;
        Assert.Equal(new[] { ("CO", "45", 50m), ("PR", "1", 20m), ("PR", "2", 10m) },
            line1.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.Equal("N130", line1[1].RemarkCode);
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(cp));
    }

    [Fact]
    public void NcciEdit_SameCarcAsLineAdjustment_NotDoubleCounted_RarcKept()
    {
        var claim = EngineClaim();
        claim.PendDetails = new PendDetailsDto
        {
            PendCode = "NCCI",
            EditFailures = new List<EditFailureDto>
            {
                new() { EditType = "NCCI_PAIR", RuleId = "R1", SuggestedCarc = "45", SuggestedRarc = "M80", AffectedLineNumbers = new List<int> { 2 } },
                new() { EditType = "MUE", RuleId = "R2", SuggestedCarc = "151", AffectedLineNumbers = new List<int> { 2 } },
            },
        };

        var cp = Era835ClaimPaymentBuilder.Build(claim, denied: false, Mapper);

        var line2 = cp.ServiceLines[1].Adjustments;
        Assert.Single(line2, a => a.GroupCode == "CO" && a.ReasonCode == "45");
        Assert.Equal("M80", line2.Single(a => a.ReasonCode == "45").RemarkCode);
        Assert.Equal(0m, line2.Single(a => a.ReasonCode == "151").Amount); // edit kept, adds nothing
        Assert.Equal(50m, line2.Sum(a => a.Amount));
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(cp));
    }

    [Fact]
    public void LineWithoutAdjustments_FallsBackToSingleCo45_Balances()
    {
        var claim = EngineClaim();
        foreach (var line in claim.ServiceLines!)
            line.AdjudicationResult!.AdjustmentReasons = null; // adjudicated before line detail existed

        var cp = Era835ClaimPaymentBuilder.Build(claim, denied: false, Mapper);

        Assert.Equal(new[] { ("CO", "45", 80m) }, cp.ServiceLines[0].Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.Equal(new[] { ("CO", "45", 50m) }, cp.ServiceLines[1].Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.Empty(cp.ClaimAdjustments);
        Assert.Empty(Era835FinancialSegments.AdjustmentBalanceProblems(cp));
    }

    [Fact]
    public void LineAdjustmentsNotMatchingPayment_ReportedUnbalanced()
    {
        var claim = EngineClaim();
        claim.ServiceLines![0].AdjudicationResult!.AdjustmentReasons![0].Amount = 40m; // 200 - 70 != 120

        var problems = Era835FinancialSegments.AdjustmentBalanceProblems(Era835ClaimPaymentBuilder.Build(claim, false, Mapper));

        Assert.Contains(problems, p => p.Contains("line 1") && p.Contains("SVC03"));
        Assert.Contains(problems, p => p.Contains("CLP04"));
    }

    [Fact]
    public void Denial_ExistingEntryWithDenialCarc_IsRecomputed_SoCasTotalsTheCharge()
    {
        // $300 denied with CARC 45, and an existing CO-45 of $250: the CAS must total $300.
        var claim = new ClaimDto
        {
            Id = "d1", ClaimNumber = "CLM-D1", BillingProviderNPI = "NPI-A", Status = ClaimStatus.Denied,
            TotalChargeAmount = 300m,
            AdjudicationResult = new ClaimAdjudicationDto
            {
                PayerPayment = 0m, DenialReasonCode = "45",
                AdjustmentReasons = new List<ClaimAdjustmentReasonDto> { new() { GroupCode = "CO", ReasonCode = "45", Amount = 250m } },
            },
        };

        var cp = Era835ClaimPaymentBuilder.Build(claim, denied: true, Mapper);

        var cas = Assert.Single(cp.ClaimAdjustments);
        Assert.Equal(("CO", "45", 300m), (cas.GroupCode, cas.ReasonCode, cas.Amount));
        Assert.Equal(cp.ChargeAmount - cp.PaymentAmount, cp.ClaimAdjustments.Sum(a => a.Amount));
    }

    [Fact]
    public void Denial_WithOtherAdjustments_DenialCarcCarriesTheRest()
    {
        var claim = new ClaimDto
        {
            Id = "d2", ClaimNumber = "CLM-D2", BillingProviderNPI = "NPI-A", Status = ClaimStatus.Denied,
            TotalChargeAmount = 300m,
            AdjudicationResult = new ClaimAdjudicationDto
            {
                PayerPayment = 0m, DenialReasonCode = "50",
                AdjustmentReasons = new List<ClaimAdjustmentReasonDto>
                {
                    new() { GroupCode = "CO", ReasonCode = "50", Amount = 120m },
                    new() { GroupCode = "PR", ReasonCode = "1", Amount = 40m },
                },
            },
        };

        var cp = Era835ClaimPaymentBuilder.Build(claim, denied: true, Mapper);

        Assert.Equal(260m, cp.ClaimAdjustments.Single(a => a.ReasonCode == "50").Amount);
        Assert.Equal(300m, cp.ClaimAdjustments.Sum(a => a.Amount));
    }
}
