using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Adjudication.Tpl;
using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

public class ThirdPartyLiabilityStageTests
{
    private const string TenantId = "tenant-1";
    private const string MemberId = "91234567A";
    private static readonly DateTime Dos = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly ITplClient _tpl = Substitute.For<ITplClient>();

    private ThirdPartyLiabilityStage Stage(TplEnforcementMode mode = TplEnforcementMode.CostAvoid, params string[] medicaidPrimaryPlans) =>
        new(_tpl, Options.Create(new ThirdPartyLiabilityOptions
        {
            Mode = mode,
            MedicaidPrimaryPlanCodes = medicaidPrimaryPlans.ToList(),
        }), NullLogger<ThirdPartyLiabilityStage>.Instance);

    private void OnFile(params TplResource[] resources) =>
        _tpl.GetActiveAsync(TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(resources);

    private static TplResource Resource(TplServiceScope scope, string carrier = "Kaiser", bool payAndChase = false,
        string state = "CA", string? code = "K", DateTime? end = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        State = state,
        CarrierName = carrier,
        PolicyNumber = "POL1",
        StateCoverageCode = code,
        Scope = scope,
        PayAndChaseOnly = payAndChase,
        EffectiveDate = new DateTime(2026, 1, 1),
        EndDate = end,
    };

    private static AdapterClaim Claim(params string[] codes) => new()
    {
        TenantId = TenantId,
        Id = "c1",
        MemberId = MemberId,
        LineOfBusiness = LineOfBusiness.Medicaid,
        ClaimType = ClaimType.Professional,
        ServiceDateFrom = Dos,
        ServiceDateTo = Dos,
        TotalChargeAmount = 100m * codes.Length,
        ClaimLines = codes.Select((c, i) => new AdapterClaimLine
        {
            LineNumber = i + 1,
            ProcedureCode = c,
            ChargeAmount = 100m,
            ServiceDateFrom = Dos,
            ServiceDateTo = Dos,
        }).ToList(),
    };

    private static ClaimAdjudicationContext Context(AdapterClaim claim, DateTime? dob = null, string? planCode = null,
        ExaminerApproval? approval = null) => new()
    {
        TenantId = TenantId,
        ClaimVersionId = "v1",
        Claim = claim,
        ResolvedMember = new ResolvedMember { MemberId = MemberId, DateOfBirth = dob ?? new DateTime(1985, 3, 1) },
        ResolvedPlan = planCode is null ? null : new ResolvedBenefitPlan { Id = "plan-guid", PlanCode = planCode },
        ExaminerApproval = approval,
    };

    [Fact]
    public void Metadata_RunsRightAfterCob()
    {
        var stage = Stage();
        Assert.Equal("ThirdPartyLiability", stage.Name);
        Assert.Equal(276, stage.Order);
    }

    [Fact]
    public async Task Off_DoesNothing()
    {
        var ctx = Context(Claim("99213"));
        var result = await Stage(TplEnforcementMode.Off).ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.TplResult);
        await _tpl.DidNotReceiveWithAnyArgs().GetActiveAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task NonMedicaidClaim_IsIgnored()
    {
        var claim = Claim("99213");
        claim.LineOfBusiness = LineOfBusiness.Commercial;
        var ctx = Context(claim);

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.TplResult);
    }

    [Fact]
    public async Task ComprehensiveCoverage_CostAvoidsTheClaim()
    {
        OnFile(Resource(TplServiceScope.Comprehensive));
        var ctx = Context(Claim("99213", "85025"));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(TplDecision.CostAvoided, ctx.TplResult!.Decision);
        Assert.Equal([1, 2], ctx.TplResult.CostAvoidLines);
        Assert.Contains(ctx.AdjudicationResult.AdjustmentReasons, a => a is { GroupCode: "CO", ReasonCode: "22" });
        Assert.Equal(2, ctx.LineAdjudicationResults.Count);
        Assert.All(ctx.LineAdjudicationResults, l => Assert.Equal("22", l.AdjustmentReasons.Single().ReasonCode));
        Assert.Contains("Kaiser policy POL1", result.Reason);
    }

    [Fact]
    public async Task PendMode_PendsInsteadOfDenying()
    {
        OnFile(Resource(TplServiceScope.Comprehensive));
        var ctx = Context(Claim("99213"));

        var result = await Stage(TplEnforcementMode.Pend).ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal("TPL", ctx.PendDetails!.PendCode);
        Assert.Equal(ThirdPartyLiabilityStage.CostAvoidReason, ctx.TplResult!.ReasonCode);
    }

    [Fact]
    public async Task SoftValidation_RecordsButNeverChangesTheClaim()
    {
        OnFile(Resource(TplServiceScope.Comprehensive));
        var ctx = Context(Claim("99213"));

        var result = await Stage(TplEnforcementMode.SoftValidation).ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.CostAvoided, ctx.TplResult!.Decision);
        Assert.Null(ctx.PendDetails);
        Assert.Empty(ctx.LineAdjudicationResults);
    }

    [Fact]
    public async Task PartialScope_PendsForAnExaminer()
    {
        // Vision-only policy: the eye exam is covered, the office visit is not.
        OnFile(Resource(TplServiceScope.Vision, carrier: "VSP", code: "E"));
        var ctx = Context(Claim("92004", "99213"));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(ThirdPartyLiabilityStage.PartialScopeReason, ctx.TplResult!.ReasonCode);
        Assert.Equal([1], ctx.TplResult.CostAvoidLines);
    }

    [Fact]
    public async Task OutOfScopeCoverage_PaysNormally()
    {
        OnFile(Resource(TplServiceScope.Dental, carrier: "Delta Dental", code: "L"));
        var ctx = Context(Claim("99213"));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.NoTpl, ctx.TplResult!.Decision);
    }

    [Fact]
    public async Task MediCalCodeA_PaysAndChases()
    {
        OnFile(Resource(TplServiceScope.Comprehensive, carrier: "Any carrier", payAndChase: true, code: "A"));
        var ctx = Context(Claim("99213"));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.PayAndChase, ctx.TplResult!.Decision);
        Assert.Single(ctx.TplResult.PayAndChaseLines);
    }

    [Fact]
    public async Task PediatricPreventive_IsPaidAndChased()
    {
        OnFile(Resource(TplServiceScope.Comprehensive, state: "TX", code: "MAJOR MEDICAL"));
        var ctx = Context(Claim("99392", "90471"), dob: new DateTime(2024, 5, 1));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.PayAndChase, ctx.TplResult!.Decision);
        Assert.Equal(2, ctx.TplResult.PayAndChaseLines.Count);
    }

    [Fact]
    public async Task AdultImmunization_IsNotPediatric()
    {
        OnFile(Resource(TplServiceScope.Comprehensive, state: "TX", code: "MAJOR MEDICAL"));
        var ctx = Context(Claim("90471"), dob: new DateTime(1970, 1, 1));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
    }

    [Fact]
    public async Task OtherPayerAlreadyBilled_SkipsTheLookup()
    {
        var claim = Claim("99213");
        claim.OtherPayers.Add(new ClaimOtherPayer { PayerResponsibilityCode = "P", PayerName = "Kaiser", PaidAmount = 40m });
        var ctx = Context(claim);

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.OtherPayerBilled, ctx.TplResult!.Decision);
        await _tpl.DidNotReceiveWithAnyArgs().GetActiveAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task LookupFailure_Pends()
    {
        _tpl.GetActiveAsync(TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TplResource>?)null);
        var ctx = Context(Claim("99213"));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.CheckUnavailable);
        Assert.Equal(ThirdPartyLiabilityStage.LookupUnavailableReason, ctx.TplResult!.ReasonCode);
    }

    [Fact]
    public async Task StarHealth_MedicaidIsPrimary()
    {
        OnFile(Resource(TplServiceScope.Comprehensive, state: "TX"));
        var ctx = Context(Claim("99213"), planCode: "STAR-HEALTH");

        var result = await Stage(TplEnforcementMode.CostAvoid, "STAR-HEALTH").ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(ThirdPartyLiabilityStage.MedicaidPrimaryPlanReason, ctx.TplResult!.ReasonCode);
    }

    [Fact]
    public async Task ExaminerApprovalOfTheExactPend_PaysAndChases()
    {
        OnFile(Resource(TplServiceScope.Comprehensive));
        var first = Context(Claim("99213"));
        await Stage(TplEnforcementMode.Pend).ExecuteAsync(first, default);

        var approval = new ExaminerApproval { ExaminerId = "ex1", ReviewedPend = first.PendDetails };
        var rerun = Context(Claim("99213"), approval: approval);
        var result = await Stage(TplEnforcementMode.Pend).ExecuteAsync(rerun, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.PayAndChase, rerun.TplResult!.Decision);
        Assert.Single(rerun.ExaminerOverrides);
    }

    [Fact]
    public async Task MedicarePartAOnly_DoesNotCoverAnOfficeVisit()
    {
        OnFile(Resource(TplServiceScope.MedicarePartA, carrier: "Medicare", code: "MEDICARE A"));
        var ctx = Context(Claim("99213"));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.NoTpl, ctx.TplResult!.Decision);
    }

    [Fact]
    public async Task Casualty_CoversOnlyAccidentClaims()
    {
        OnFile(Resource(TplServiceScope.Casualty, carrier: "Auto insurer", code: "CASUALTY"));

        var plain = Context(Claim("99283"));
        Assert.Equal(ClaimAdjudicationOutcome.Pass, (await Stage().ExecuteAsync(plain, default)).Outcome);

        var accident = Claim("99283");
        accident.RelatedCausesCode = "AA";
        var ctx = Context(accident);
        Assert.Equal(ClaimAdjudicationOutcome.Deny, (await Stage().ExecuteAsync(ctx, default)).Outcome);
    }

    [Fact]
    public async Task ResourceEndedBeforeService_IsIgnored()
    {
        OnFile(Resource(TplServiceScope.Comprehensive, end: new DateTime(2026, 9, 30)));
        var ctx = Context(Claim("99213"));

        var result = await Stage().ExecuteAsync(ctx, default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(TplDecision.NoTpl, ctx.TplResult!.Decision);
    }
}

public class TplServiceRulesTests
{
    private static AdapterClaim Inst(string facilityType) => new()
    {
        ClaimType = ClaimType.Institutional,
        Institutional = new InstitutionalClaimDetails { FacilityTypeCode = facilityType },
    };

    [Theory]
    [InlineData("E0601", TplServiceCategory.Dme)]
    [InlineData("A4253", TplServiceCategory.Dme)]
    [InlineData("V2020", TplServiceCategory.Vision)]
    [InlineData("92014", TplServiceCategory.Vision)]
    [InlineData("90837", TplServiceCategory.BehavioralHealth)]
    [InlineData("H0015", TplServiceCategory.BehavioralHealth)]
    [InlineData("99213", TplServiceCategory.Professional)]
    public void Categorise_Professional(string code, TplServiceCategory expected) =>
        Assert.Equal(expected, TplServiceRules.Categorise(new AdapterClaim { ClaimType = ClaimType.Professional },
            new AdapterClaimLine { ProcedureCode = code }));

    [Theory]
    [InlineData("11", TplServiceCategory.InpatientHospital)]
    [InlineData("13", TplServiceCategory.OutpatientHospital)]
    [InlineData("21", TplServiceCategory.LongTermCare)]
    public void Categorise_Institutional(string facilityType, TplServiceCategory expected) =>
        Assert.Equal(expected, TplServiceRules.Categorise(Inst(facilityType), new AdapterClaimLine()));

    [Fact]
    public void MedicareParts_CoverWhatMedicareCovers()
    {
        Assert.True(TplServiceRules.Covers(TplServiceScope.MedicarePartA, TplServiceCategory.InpatientHospital, false));
        Assert.False(TplServiceRules.Covers(TplServiceScope.MedicarePartA, TplServiceCategory.OutpatientHospital, false));
        Assert.True(TplServiceRules.Covers(TplServiceScope.MedicarePartB, TplServiceCategory.Dme, false));
        Assert.False(TplServiceRules.Covers(TplServiceScope.MedicarePartB, TplServiceCategory.Dental, false));
        Assert.True(TplServiceRules.Covers(TplServiceScope.MedicarePartC, TplServiceCategory.Professional, false));
    }
}
