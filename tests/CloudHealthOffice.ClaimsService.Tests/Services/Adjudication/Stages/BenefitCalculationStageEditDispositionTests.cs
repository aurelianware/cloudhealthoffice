using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Models;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

public partial class BenefitCalculationStageTests
{
    /// <summary>
    /// The approval re-run with an upheld pair edit on line 2: the engine is
    /// asked to allow nothing on line 2, and the line's 835 adjustments carry
    /// the edit's CARC in place of CO-45.
    /// </summary>
    [Fact]
    public async Task Execute_ApprovalRerun_UpheldEdit_DeniesTheLineItCovers()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 2, ProcedureCode = "99214", ChargeAmount = 120m, Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        var pair = new NcciEditFailureSnapshot
        {
            EditType = "NcciPair", RuleId = "NE001", Column1Code = "99213", Column2Code = "99214",
            AffectedLineNumbers = [2], SuggestedCarc = "B20", SuggestedRarc = "N519",
        };
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
            ExaminerApproval = new ExaminerApproval
            {
                ExaminerId = "examiner-1",
                ReviewedPend = new PendDetails { PendCode = "NCCI", EditFailures = [pair] },
                EditDispositions =
                [
                    new EditDisposition { EditKey = EditIdentity.Key(pair), Decision = EditDecision.Uphold, UpheldByDefault = true },
                ],
            },
        };

        BenefitResolutionRequest? sent = null;
        _engine.CalculateAsync(Arg.Do<BenefitResolutionRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult
            {
                PreparedAccumulatorCommit = Prepared,
                Success = true,
                Totals = new ClaimTotals { TotalAllowed = 72m, TotalPlanPaid = 72m },
                Lines =
                [
                    new() { LineNumber = 1, IsCovered = true, AllowedAmount = 72m, PlanPaidAmount = 72m, ContractualAdjustment = 28m },
                    new() { LineNumber = 2, IsCovered = true, AllowedAmount = 0m, PlanPaidAmount = 0m, ContractualAdjustment = 120m },
                ],
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(72m, sent!.AllowedAmounts[1]);
        Assert.Equal(0m, sent.AllowedAmounts[2]);

        var line2 = Assert.Single(ctx.LineAdjudicationResults[1].AdjustmentReasons);
        Assert.Equal(("CO", "B20", "N519", 120m), (line2.GroupCode, line2.ReasonCode, line2.RemarkCode, line2.Amount));
        var line1 = Assert.Single(ctx.LineAdjudicationResults[0].AdjustmentReasons);
        Assert.Equal(("CO", "45", 28m), (line1.GroupCode, line1.ReasonCode, line1.Amount));
        Assert.Contains(ctx.ExaminerOverrides, o => o.Contains("line 2 denied"));
    }
}
