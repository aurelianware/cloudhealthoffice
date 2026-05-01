using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.9 — behavior coverage for <see cref="AiExaminationStage"/>:
/// stage metadata, eligibility filter (NotApplicable / Skipped / Triggered),
/// kill-switch (Disabled mode), Kafka emission verification, defensive
/// exception handling, mirrored predicate parity with
/// <c>NcciEditFailureSnapshot.IsModifierAddressable()</c>.
/// </summary>
public class AiExaminationStageTests
{
    private const string TenantId = "tenant-1";
    private const string ClaimVersionId = "ver-ai-1";

    private readonly IClaimEventPublisher _publisher = Substitute.For<IClaimEventPublisher>();

    private AiExaminationStage NewStage(
        AiEnforcementMode mode = AiEnforcementMode.BestEffort,
        IClaimEventPublisher? publisher = null)
    {
        var options = Options.Create(new TenantEnforcementPolicyOptions { AiMode = mode });
        return new AiExaminationStage(
            publisher ?? _publisher,
            options,
            NullLogger<AiExaminationStage>.Instance);
    }

    [Fact]
    public void Stage_metadata_matches_pipeline_contract()
    {
        var stage = NewStage();
        Assert.Equal("AiExamination", stage.Name);
        Assert.Equal(600, stage.Order);
        Assert.False(stage.IsRequired);
    }

    [Fact]
    public async Task NoPendDetails_returns_Pass_with_NotApplicable()
    {
        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.AiExaminationResult);
        Assert.Equal(AiInvocationStatus.NotApplicable, ctx.AiExaminationResult!.Status);
        Assert.Equal(AiExaminationStage.NotApplicableNoPendDetailsReason, ctx.AiExaminationResult.Reason);
        Assert.Equal(0, ctx.AiExaminationResult.EligibleEditFailureCount);
        await _publisher.DidNotReceive()
            .PublishClaimPendedAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonNcciPend_returns_Pass_with_NotApplicable()
    {
        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "AUTH",
            PendReason = "prior auth missing",
        };

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.NotNull(ctx.AiExaminationResult);
        Assert.Equal(AiInvocationStatus.NotApplicable, ctx.AiExaminationResult!.Status);
        Assert.Equal(AiExaminationStage.NotApplicableNonNcciReason, ctx.AiExaminationResult.Reason);
        await _publisher.DidNotReceive()
            .PublishClaimPendedAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NcciPend_with_only_Mue_failures_returns_Skipped()
    {
        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "Mue", RuleId = "NE002", UnitsBilled = 5, MueMaxUnits = 1 },
            },
        };

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.NotNull(ctx.AiExaminationResult);
        Assert.Equal(AiInvocationStatus.Skipped, ctx.AiExaminationResult!.Status);
        Assert.Equal(AiExaminationStage.NoModifierAddressableEditsReason, ctx.AiExaminationResult.Reason);
        Assert.Equal(0, ctx.AiExaminationResult.EligibleEditFailureCount);
        await _publisher.DidNotReceive()
            .PublishClaimPendedAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NcciPend_with_NcciPair_NE001_returns_Triggered_and_emits_Kafka()
    {
        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new()
                {
                    EditType = "NcciPair",
                    RuleId = "NE001",
                    Column1Code = "11042",
                    Column2Code = "97597",
                    AffectedLineNumbers = new List<int> { 1, 2 },
                    ModifierOverridePresent = false,
                },
            },
        };

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue);
        Assert.Equal(AiExaminationStage.PendingAiExaminationReason, result.Reason);
        Assert.NotNull(ctx.AiExaminationResult);
        Assert.Equal(AiInvocationStatus.Triggered, ctx.AiExaminationResult!.Status);
        Assert.Equal(1, ctx.AiExaminationResult.EligibleEditFailureCount);
        await _publisher.Received(1).PublishClaimPendedAsync(
            Arg.Is<Claim>(c => c.Id == ctx.Claim.Id),
            TenantId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Triggered_carries_PendDetails_through_to_published_claim()
    {
        var ctx = NewContext();
        var pendDetails = new PendDetails
        {
            PendCode = "NCCI",
            PendReason = "bundling violation 11042 / 97597",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001" },
            },
        };
        ctx.PendDetails = pendDetails;

        Claim? captured = null;
        _publisher.When(p => p.PublishClaimPendedAsync(
                Arg.Any<Claim>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<Claim>());

        await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.PendDetails);
        Assert.Equal("NCCI", captured.PendDetails!.PendCode);
        Assert.Single(captured.PendDetails.EditFailures);
        Assert.Equal("NE001", captured.PendDetails.EditFailures[0].RuleId);
    }

    [Fact]
    public async Task ModifierOverridePresent_does_NOT_affect_eligibility_filter()
    {
        // Plan-First Gap A.1 — the filter mirrors IsModifierAddressable()
        // (rule attribute), NOT ModifierOverridePresent (claim attribute).
        // Both polarities of ModifierOverridePresent should trigger when
        // the rule is NcciPair/NE001.
        var ctxOverridePresent = NewContext();
        ctxOverridePresent.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001", ModifierOverridePresent = true },
            },
        };

        var ctxOverrideAbsent = NewContext();
        ctxOverrideAbsent.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001", ModifierOverridePresent = false },
            },
        };

        var stage = NewStage();
        var resultPresent = await stage.ExecuteAsync(ctxOverridePresent, CancellationToken.None);
        var resultAbsent = await stage.ExecuteAsync(ctxOverrideAbsent, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, resultPresent.Outcome);
        Assert.Equal(ClaimAdjudicationOutcome.Pend, resultAbsent.Outcome);
        Assert.Equal(AiInvocationStatus.Triggered, ctxOverridePresent.AiExaminationResult!.Status);
        Assert.Equal(AiInvocationStatus.Triggered, ctxOverrideAbsent.AiExaminationResult!.Status);
    }

    [Fact]
    public async Task EligibleEditFailureCount_counts_only_addressable_edits()
    {
        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001" },     // addressable
                new() { EditType = "Mue", RuleId = "NE002" },          // not addressable
                new() { EditType = "NcciPair", RuleId = "NE001" },     // addressable
                new() { EditType = "NcciPair", RuleId = "NE003" },     // not addressable (different rule id)
            },
        };

        await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, ctx.AiExaminationResult!.EligibleEditFailureCount);
    }

    [Fact]
    public async Task Disabled_mode_short_circuits_with_NotApplicable_telemetry_reason()
    {
        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001" },
            },
        };

        var result = await NewStage(mode: AiEnforcementMode.Disabled).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.NotNull(ctx.AiExaminationResult);
        Assert.Equal(AiInvocationStatus.NotApplicable, ctx.AiExaminationResult!.Status);
        Assert.Equal(AiExaminationStage.DisabledByPolicyReason, ctx.AiExaminationResult.Reason);
        await _publisher.DidNotReceive()
            .PublishClaimPendedAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publisher_exception_does_not_fail_the_stage_and_records_Triggered()
    {
        // Defensive — under today's contract this branch is unreachable
        // (IClaimEventPublisher.PublishClaimPendedAsync swallows everything).
        // Test exercises the catch block in case a future contract change
        // surfaces failures.
        _publisher
            .When(p => p.PublishClaimPendedAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Throw(new InvalidOperationException("simulated broker failure"));

        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001" },
            },
        };

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        // Pipeline still continues; AI advisory is the only thing lost.
        // Pend outcome stands so PersistenceStage records the pend.
        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue);
        Assert.Equal(AiInvocationStatus.Triggered, ctx.AiExaminationResult!.Status);
    }

    [Fact]
    public async Task PendCode_match_is_case_insensitive()
    {
        // Mirrors ExaminerOrchestrator's OrdinalIgnoreCase comparison so
        // producer and consumer stay aligned on casing variants.
        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "ncci",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001" },
            },
        };

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(AiInvocationStatus.Triggered, ctx.AiExaminationResult!.Status);
    }

    [Fact]
    public async Task Cancellation_token_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _publisher
            .When(p => p.PublishClaimPendedAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new OperationCanceledException(cts.Token));

        var ctx = NewContext();
        ctx.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "NcciPair", RuleId = "NE001" },
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewStage().ExecuteAsync(ctx, cts.Token));
    }

    private static AdapterClaim DefaultClaim() => new()
    {
        Id = "claim-ai-1",
        ClaimNumber = "CLM-AI-1",
        MemberId = "MEM-AI-1",
        BillingProviderNPI = "1234567890",
        ServiceDateFrom = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ClaimAdjudicationContext NewContext(AdapterClaim? claim = null) =>
        new()
        {
            TenantId = TenantId,
            ClaimVersionId = ClaimVersionId,
            Claim = claim ?? DefaultClaim(),
        };
}
