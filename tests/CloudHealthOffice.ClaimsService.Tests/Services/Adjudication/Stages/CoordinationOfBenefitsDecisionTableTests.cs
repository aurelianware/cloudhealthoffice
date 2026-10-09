using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// PR #1278 B2 — the COB decision table: coverage-service records plus the
/// 837 (2000B SBR01, 2320/2430) settle the payer order before benefit
/// calculation (Order 275 &lt; 300). Disagreement and unusable data pend in
/// every mode; agreement with complete data lifts the Phase-1 pend.
/// </summary>
public partial class CoordinationOfBenefitsStageTests
{
    private void CoverageSays(params string[] sequences) =>
        _coverageClient.GetCobEntriesAsync(TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(sequences.Select((s, i) => new CobEntry { PayerName = $"Payer{i}", PayerId = $"P{i}", CoverageSequence = s }).ToArray());

    private static ClaimOtherPayer Payer(string sbr01, decimal? paid, string id = "X") => new()
    {
        PayerResponsibilityCode = sbr01, PayerId = id, PaidAmount = paid,
    };

    private static AdapterClaim ClaimAs(string? sbr01, params ClaimOtherPayer[] otherPayers)
    {
        var claim = DefaultClaim();
        claim.PayerResponsibilityCode = sbr01;
        claim.OtherPayers = otherPayers.ToList();
        return claim;
    }

    public static TheoryData<CobEnforcementMode> AllModes => new()
    {
        CobEnforcementMode.PendForSecondary, CobEnforcementMode.Deny, CobEnforcementMode.SoftValidation,
    };

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task CoveragePrimary_837Secondary_PendsMismatch_InEveryMode(CobEnforcementMode mode)
    {
        CoverageSays();
        var ctx = NewContext(ClaimAs("S", Payer("P", 80m)));

        var result = await NewStage(mode).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CoordinationOfBenefitsStage.PayerOrderMismatchPendReason, ctx.CobResult!.PendReason);
        Assert.False(ctx.CobResult.ApplyCob);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
    }

    [Theory]
    [InlineData("P", "P")]           // coverage: we are secondary; 837: primary
    [InlineData("P", "T")]           // coverage: secondary; 837: tertiary
    [InlineData("P,S", "S")]         // coverage: tertiary; 837: secondary
    public async Task CoverageAnd837Disagree_PendMismatch(string coverage, string sbr01)
    {
        CoverageSays(coverage.Split(','));
        var ctx = NewContext(ClaimAs(sbr01, Payer("P", 80m, "A"), Payer("S", 20m, "B")));

        var result = await NewStage(CobEnforcementMode.SoftValidation).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CoordinationOfBenefitsStage.PayerOrderMismatchPendReason, ctx.CobResult!.PendReason);
    }

    [Theory]
    [InlineData("P", "S")]
    [InlineData("P,S", "T")]
    [InlineData("P,P", "T")]
    public async Task Agree_WithComplete2320Data_PassesAndAppliesCob(string coverage, string sbr01)
    {
        CoverageSays(coverage.Split(','));
        // An 837 to the secondary lists the primary (and may list a later
        // payer, e.g. the tertiary — ignored); one to the tertiary lists both.
        var ctx = NewContext(sbr01 == "S"
            ? ClaimAs(sbr01, Payer("P", 80m, "A"), Payer("T", null, "C"))
            : ClaimAs(sbr01, Payer("P", 80m, "A"), Payer("S", 20m, "B")));

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(ctx.CobResult!.ApplyCob);
        Assert.Equal(sbr01 == "S" ? 2 : 3, ctx.CobResult.PayerSequence);
        Assert.Null(ctx.CobResult.PendReason);
        Assert.Null(ctx.PendDetails);
    }

    [Fact]
    public async Task Agree_But837HasNo2320_KeepsPending()
    {
        CoverageSays("P");
        var ctx = NewContext(ClaimAs("S"));

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CoordinationOfBenefitsStage.SecondaryNotSupportedPendReason, ctx.CobResult!.PendReason);
        Assert.False(ctx.CobResult.ApplyCob);
    }

    [Fact]
    public async Task Agree_But2320HasNoPaidAmount_KeepsPending()
    {
        CoverageSays("P", "S");
        // Tertiary: the secondary's 2320 has neither AMT*D nor 2430.
        var ctx = NewContext(ClaimAs("T", Payer("P", 80m, "A"), Payer("S", null, "B")));

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.False(ctx.CobResult!.ApplyCob);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task DuplicatePayerSequence_Pends(CobEnforcementMode mode)
    {
        CoverageSays("P", "S");
        var ctx = NewContext(ClaimAs("T", Payer("P", 80m, "A"), Payer("P", 20m, "B")));

        var result = await NewStage(mode).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CoordinationOfBenefitsStage.DuplicatePayerSequencePendReason, ctx.CobResult!.PendReason);
    }

    [Fact]
    public async Task UnmatchedLineAdjudication_Pends()
    {
        CoverageSays("P", "S");
        var claim = ClaimAs("T", Payer("P", 80m, "A"), Payer("S", 20m, "B"));
        claim.UnmatchedOtherPayerLines = [new ClaimOtherPayerLine { LineNumber = 1, PayerId = "ZZZ", PaidAmount = 10m }];
        var ctx = NewContext(claim);

        var result = await NewStage(CobEnforcementMode.SoftValidation).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CoordinationOfBenefitsStage.UnmatchedLineAdjudicationPendReason, ctx.CobResult!.PendReason);
        Assert.Contains("ZZZ", ctx.PendDetails!.PendReason);
    }

    [Fact]
    public async Task CoverageUnavailable_837LaterPayer_PendsEvenInSoftValidation()
    {
        _coverageClient.GetCobEntriesAsync(TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CobEntry>?)null);
        var ctx = NewContext(ClaimAs("S", Payer("P", 80m)));

        var result = await NewStage(CobEnforcementMode.SoftValidation).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.False(ctx.CobResult!.ApplyCob);
    }

    // ── PR #1278 round 3, B2: the examiner's payer order ──────────────

    private static ClaimAdjudicationContext Approving(AdapterClaim claim, ExaminerApproval approval)
    {
        var ctx = NewContext(claim);
        ctx.ExaminerApproval = approval;
        return ctx;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Examiner_SequenceOutsideThePayersOnTheClaim_Pends_NotClamped(int sequence)
    {
        CoverageSays("P", "S");
        var ctx = Approving(ClaimAs("T", Payer("P", 112m, "A"), Payer("S", 60m, "B")),
            new ExaminerApproval { PayerSequence = sequence, PayerOrderOverrideAuthorized = true });

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CoordinationOfBenefitsStage.ExaminerSequenceOutOfRangePendReason, ctx.CobResult!.PendReason);
        Assert.False(ctx.CobResult.ConfirmedByExaminer);
    }

    /// <summary>Coverage-service says primary; the examiner says tertiary: needs override authority.</summary>
    [Fact]
    public async Task Examiner_DisagreeingWithCoverage_NeedsOverrideAuthority()
    {
        CoverageSays();
        var claim = ClaimAs("T", Payer("P", 112m, "A"), Payer("S", 60m, "B"));

        var plain = Approving(claim, new ExaminerApproval { PayerSequence = 3 });
        var authorized = Approving(claim, new ExaminerApproval { PayerSequence = 3, PayerOrderOverrideAuthorized = true });

        Assert.Equal(ClaimAdjudicationOutcome.Pend, (await NewStage().ExecuteAsync(plain, CancellationToken.None)).Outcome);
        Assert.Equal(CoordinationOfBenefitsStage.PayerOrderOverrideRequiredPendReason, plain.CobResult!.PendReason);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, (await NewStage().ExecuteAsync(authorized, CancellationToken.None)).Outcome);
        Assert.Equal(3, authorized.CobResult!.PayerSequence);
        Assert.True(authorized.CobResult.ApplyCob);
    }

    /// <summary>Coverage-service unavailable: nothing corroborates the examiner's order.</summary>
    [Fact]
    public async Task Examiner_CoverageUnavailable_NeedsOverrideAuthority()
    {
        _coverageClient.GetCobEntriesAsync(TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CobEntry>?)null);
        var ctx = Approving(ClaimAs("S", Payer("P", 80m, "A")), new ExaminerApproval { PayerSequence = 2 });

        await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(CoordinationOfBenefitsStage.PayerOrderOverrideRequiredPendReason, ctx.CobResult!.PendReason);
    }

    /// <summary>Agreeing with coverage-service and SBR01 needs no override authority.</summary>
    [Fact]
    public async Task Examiner_AgreeingWithCoverageAndSbr01_Passes()
    {
        CoverageSays("P");
        var ctx = Approving(ClaimAs("S", Payer("P", 80m, "A")), new ExaminerApproval { PayerSequence = 2 });

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(ctx.CobResult!.ApplyCob);
    }

    /// <summary>
    /// Primary over a prior payment ($172 on golden 07) needs a second,
    /// different approver — the same approver twice does not count.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("supervisor-1", false)]
    [InlineData("supervisor-2", true)]
    public async Task Examiner_PrimaryOverPriorPayment_NeedsASecondDifferentApprover(string? second, bool passes)
    {
        CoverageSays();
        var ctx = Approving(ClaimAs("T", Payer("P", 112m, "A"), Payer("S", 60m, "B")), new ExaminerApproval
        {
            ExaminerId = "supervisor-1", SecondApproverId = second, PayerSequence = 1, PayerOrderOverrideAuthorized = true,
        });

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(passes ? ClaimAdjudicationOutcome.Pass : ClaimAdjudicationOutcome.Pend, result.Outcome);
        if (passes)
        {
            Assert.Equal(1, ctx.CobResult!.PayerSequence);
            Assert.True(ctx.CobResult.ConfirmedByExaminer);
        }
        else
        {
            Assert.Equal(CoordinationOfBenefitsStage.PrimaryOverPriorPaymentPendReason, ctx.CobResult!.PendReason);
        }
    }
}
