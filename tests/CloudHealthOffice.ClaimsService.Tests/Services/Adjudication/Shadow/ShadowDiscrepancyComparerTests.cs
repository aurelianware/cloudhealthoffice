using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Shadow;
using ClaimsService.Services.Adjudication.Stages;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowDiscrepancyComparerTests
{
    private const decimal Tolerance = 0.01m;

    [Fact]
    public void SameOutcomeAndAmounts_IsAMatch()
    {
        var core = CorePaid(allowed: 80m, paid: 60m, liability: 20m);
        var cho = ChoPassed(allowed: 80m, paid: 60m, liability: 20m);

        var result = ShadowDiscrepancyComparer.Compare(core, cho, Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.Match, result.Kind);
        Assert.Equal(0m, result.PaidVariance);
        Assert.Empty(result.Lines.Single().Differences);
        Assert.Equal("qnxt", result.SourceSystem);
        Assert.Equal("batch-1", result.ImportBatchId);
    }

    [Fact]
    public void ACentOfRounding_IsStillAMatch()
    {
        var result = ShadowDiscrepancyComparer.Compare(
            CorePaid(80m, 60m, 20m), ChoPassed(80.01m, 59.99m, 20.01m), Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.Match, result.Kind);
    }

    [Fact]
    public void ADifferentAllowed_IsAPricingDifference_WithTheVarianceSigned()
    {
        var result = ShadowDiscrepancyComparer.Compare(
            CorePaid(80m, 60m, 20m), ChoPassed(100m, 75m, 25m), Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.AllowedVariance, result.Kind);
        Assert.Equal(15m, result.PaidVariance);
        Assert.Equal(15m, result.AbsolutePaidVariance);
        Assert.Equal(20m, result.AllowedVariance);
        Assert.Contains(ShadowLineDifference.Allowed, result.Lines.Single().Differences);
    }

    [Fact]
    public void SameAllowed_DifferentPaid_IsABenefitDifference()
    {
        var result = ShadowDiscrepancyComparer.Compare(
            CorePaid(80m, 60m, 20m), ChoPassed(80m, 30m, 50m), Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.PaidVariance, result.Kind);
        Assert.Equal(-30m, result.PaidVariance);
    }

    [Fact]
    public void OnlyTheMembersShareDiffers_IsAMemberLiabilityDifference()
    {
        var result = ShadowDiscrepancyComparer.Compare(
            CorePaid(80m, 60m, 20m), ChoPassed(80m, 60m, 10m), Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.MemberLiabilityVariance, result.Kind);
    }

    [Fact]
    public void CorePaid_ChoDenied_IsAnOutcomeMismatch_WithChosDenialCode()
    {
        var cho = Cho(ClaimAdjudicationStageResult.Deny(TimelyFilingStage.StageName, "Filed late"));
        cho.AdjudicationResult.DenialReasonCode = "29";

        var result = ShadowDiscrepancyComparer.Compare(CorePaid(80m, 60m, 20m), cho, Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.OutcomeMismatch, result.Kind);
        Assert.Equal(ShadowOutcome.Denied, result.ChoOutcome);
        Assert.Equal("29", result.ChoDenialCode);
        Assert.Equal(-60m, result.PaidVariance);
        Assert.Contains("TimelyFiling: Filed late", result.ChoStageFindings);
    }

    [Fact]
    public void ADenialWithoutACode_GetsTheDefaultCarc_AsPersistenceWould()
    {
        var core = CoreDenied("27");
        var cho = Cho(ClaimAdjudicationStageResult.Deny(NcciEditsStage.StageName, "Unbundled"));

        var result = ShadowDiscrepancyComparer.Compare(core, cho, Tolerance);

        Assert.Equal(AdjudicationDenialCodes.DefaultDenyCarc, result.ChoDenialCode);
        Assert.Equal(ShadowDiscrepancyKind.DenialCodeMismatch, result.Kind);
    }

    [Fact]
    public void BothDenied_WithTheSameCode_IsAMatch()
    {
        var cho = Cho(ClaimAdjudicationStageResult.Deny(BenefitCalculationStage.StageName, "Not covered"));
        cho.AdjudicationResult.DenialReasonCode = "96";

        var result = ShadowDiscrepancyComparer.Compare(CoreDenied("96"), cho, Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.Match, result.Kind);
    }

    [Fact]
    public void AChoPend_IsUndecided_AndNamesTheMissingInput()
    {
        var cho = Cho(ClaimAdjudicationStageResult.Pend(PricingStage.StageName, "No contracted rate"));
        cho.PendDetails = new PendDetails { PendCode = "NOCONTRACT", PendReason = "No contracted rate for 99213" };

        var result = ShadowDiscrepancyComparer.Compare(CorePaid(80m, 60m, 20m), cho, Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.ChoUndecided, result.Kind);
        Assert.Equal("NOCONTRACT", result.ChoPendCode);
        Assert.Equal(0m, result.PaidVariance);
        Assert.Empty(result.Lines.Single().Differences);
    }

    [Fact]
    public void AChoReject_IsUndecided_NotADenial()
    {
        var cho = Cho(ClaimAdjudicationStageResult.Reject(BenefitCalculationStage.StageName, "Missing BenefitPlanId"));

        var result = ShadowDiscrepancyComparer.Compare(CorePaid(80m, 60m, 20m), cho, Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.ChoUndecided, result.Kind);
        Assert.Equal("REJECT:BenefitCalculation", result.ChoPendCode);
    }

    [Fact]
    public void ADenialBecauseTheMemberIsNotLoaded_IsUndecided()
    {
        var cho = Cho(ClaimAdjudicationStageResult.Deny(BenefitCalculationStage.StageName, BenefitCalculationStage.MemberNotFoundReason));
        cho.MemberLookup = MemberLookupOutcome.NotFound;
        cho.AdjudicationResult.DenialReasonCode = BenefitCalculationStage.MemberNotFoundCarc;

        var result = ShadowDiscrepancyComparer.Compare(CorePaid(80m, 60m, 20m), cho, Tolerance);

        Assert.Equal(ShadowDiscrepancyKind.ChoUndecided, result.Kind);
        Assert.Equal(ShadowDiscrepancyComparer.MemberNotLoadedCode, result.ChoPendCode);
        Assert.Null(result.ChoDenialCode);
    }

    [Fact]
    public void ALineTheCoreDenied_ButChoPaid_IsALineOutcomeDifference()
    {
        var core = CorePaid(80m, 60m, 20m, lines: 2);
        core.ClaimLines[1].AdjudicationResult = new LineAdjudicationResult();
        core.AdjudicationResult!.AdjustmentReasons.Add(new ClaimAdjustmentReason { ReasonCode = "97", Description = "Line 2" });
        var cho = ChoPassed(80m, 60m, 20m, lines: 2);
        cho.LineAdjudicationResults[1] = new LineAdjudicationResult();

        var result = ShadowDiscrepancyComparer.Compare(core, cho, Tolerance);

        Assert.Equal("97", result.Lines[1].CoreDenialCode);
        Assert.Null(result.Lines[1].ChoDenialCode);
        Assert.Contains(ShadowLineDifference.LineOutcome, result.Lines[1].Differences);
        Assert.Equal(ShadowDiscrepancyKind.PaidVariance, result.Kind);
    }

    [Fact]
    public void ChosContractualWriteDownAndMemberShare_AreNotLineDenials()
    {
        var cho = ChoPassed(80m, 60m, 20m);
        cho.LineAdjudicationResults[0].AdjustmentReasons.AddRange(
        [
            new ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "45", Amount = 20m },
            new ClaimAdjustmentReason { GroupCode = "PR", ReasonCode = "2", Amount = 20m },
        ]);

        var result = ShadowDiscrepancyComparer.Compare(CorePaid(80m, 60m, 20m), cho, Tolerance);

        Assert.Null(result.Lines.Single().ChoDenialCode);
        Assert.Equal(ShadowDiscrepancyKind.Match, result.Kind);
    }

    [Fact]
    public void BothDenyALine_WithDifferentCodes_IsADenialCodeDifference()
    {
        var core = CorePaid(80m, 60m, 20m, lines: 2);
        core.ClaimLines[1].AdjudicationResult = new LineAdjudicationResult();
        core.AdjudicationResult!.AdjustmentReasons.Add(new ClaimAdjustmentReason { ReasonCode = "97", Description = "Line 2" });
        var cho = ChoPassed(80m, 60m, 20m, lines: 2);
        cho.LineAdjudicationResults[1] = new LineAdjudicationResult
        {
            AdjustmentReasons = [new ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "234" }],
        };

        var result = ShadowDiscrepancyComparer.Compare(core, cho, Tolerance);

        Assert.Contains(ShadowLineDifference.DenialCode, result.Lines[1].Differences);
        Assert.Equal(ShadowDiscrepancyKind.DenialCodeMismatch, result.Kind);
    }

    [Fact]
    public void WhenTheCoreFileHadNoLineAmounts_OnlyTotalsAreCompared()
    {
        var core = CorePaid(80m, 60m, 20m);
        core.ClaimLines[0].AdjudicationResult = null;

        var result = ShadowDiscrepancyComparer.Compare(core, ChoPassed(80m, 60m, 20m), Tolerance);

        Assert.False(result.Lines.Single().CoreAmountsPresent);
        Assert.Equal(ShadowDiscrepancyKind.Match, result.Kind);
    }

    [Theory]
    [InlineData(ClaimStatus.Pended)]
    [InlineData(ClaimStatus.Voided)]
    [InlineData(ClaimStatus.Submitted)]
    public void ACoreClaimThatIsNotFinal_IsNotComparable(ClaimStatus status)
    {
        Assert.Equal(ShadowOutcome.NotFinal, ShadowDiscrepancyComparer.CoreOutcome(status));
        var result = ShadowDiscrepancyComparer.NotComparable(CorePaid(1m, 1m, 0m), "tenant-1");
        Assert.Equal(ShadowDiscrepancyKind.NotComparable, result.Kind);
    }

    internal static Claim CorePaid(decimal allowed, decimal paid, decimal liability, int lines = 1)
    {
        var claim = CoreClaim(ClaimStatus.Paid, lines);
        claim.AdjudicationResult = new AdjudicationResult
        {
            AllowedAmount = allowed,
            PayerPayment = paid,
            PatientResponsibility = liability,
        };
        claim.ClaimLines[0].AdjudicationResult = new LineAdjudicationResult
        {
            AllowedAmount = allowed,
            PaidAmount = paid,
            PatientResponsibility = liability,
        };
        return claim;
    }

    internal static Claim CoreDenied(string carc)
    {
        var claim = CoreClaim(ClaimStatus.Denied, 1);
        claim.AdjudicationResult = new AdjudicationResult { DenialReasonCode = carc };
        claim.ClaimLines[0].AdjudicationResult = new LineAdjudicationResult();
        return claim;
    }

    internal static Claim CoreClaim(ClaimStatus status, int lines)
    {
        var day = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        return new Claim
        {
            Id = "doc-1",
            ClaimVersionId = "cpcds-abc",
            ClaimNumber = "Q-1",
            TenantId = "tenant-1",
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional,
            ServiceDateFrom = day,
            ServiceDateTo = day,
            Status = status,
            ExternalSource = new ClaimExternalSource
            {
                Format = "cpcds",
                SourceSystem = "qnxt",
                SourceClaimId = "Q-1",
                ImportBatchId = "batch-1",
            },
            ClaimLines = Enumerable.Range(1, lines)
                .Select(n => new ClaimLine { LineNumber = n, ProcedureCode = "99213", ChargeAmount = 100m })
                .ToList(),
        };
    }

    private static ClaimAdjudicationContext ChoPassed(decimal allowed, decimal paid, decimal liability, int lines = 1)
    {
        var context = Cho(ClaimAdjudicationStageResult.Pass(BenefitCalculationStage.StageName));
        context.AdjudicationResult = new AdjudicationResult
        {
            AllowedAmount = allowed,
            PayerPayment = paid,
            PatientResponsibility = liability,
        };
        context.LineAdjudicationResults = Enumerable.Range(0, lines)
            .Select(i => i == 0
                ? new LineAdjudicationResult { AllowedAmount = allowed, PaidAmount = paid, PatientResponsibility = liability }
                : new LineAdjudicationResult())
            .ToList();
        return context;
    }

    private static ClaimAdjudicationContext Cho(params ClaimAdjudicationStageResult[] results)
    {
        var context = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = "cpcds-abc",
            Claim = new AdapterClaim { Id = "doc-1", ClaimVersionId = "cpcds-abc" },
            IsShadow = true,
            MemberLookup = MemberLookupOutcome.Found,
        };
        context.StageResults.AddRange(results);
        return context;
    }
}
