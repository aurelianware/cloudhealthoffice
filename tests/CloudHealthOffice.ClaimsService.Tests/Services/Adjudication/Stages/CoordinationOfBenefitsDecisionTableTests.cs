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
}
