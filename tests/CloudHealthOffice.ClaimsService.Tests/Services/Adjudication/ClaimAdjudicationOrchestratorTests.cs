using System.Net.Http;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// Capability 5.5 — orchestrator-level tests using stub stages so the
/// pipeline contract (ordering, short-circuit, persistence-always-runs,
/// adjudicated-event emission, idempotency) is exercised without
/// touching <see cref="BenefitCalculationStage"/> or
/// <see cref="PersistenceStage"/> internals.
/// </summary>
public class ClaimAdjudicationOrchestratorTests
{
    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IBenefitPlanResolver _planResolver = Substitute.For<IBenefitPlanResolver>();
    private readonly IMemberResolver _memberResolver = Substitute.For<IMemberResolver>();
    private readonly ICoverageResolver _coverageResolver = Substitute.For<ICoverageResolver>();
    private readonly IClaimVersionEventPublisher _eventPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ClaimAdapterFactory _factory;

    public ClaimAdjudicationOrchestratorTests()
    {
        _adapter.Platform.Returns("cho");

        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);

        _factory = new ClaimAdapterFactory(
            new[] { _adapter },
            cache,
            NullLogger<ClaimAdapterFactory>.Instance);
    }

    [Fact]
    public async Task Adjudicate_RunsStagesInOrderAscending()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, executed),
            new RecordingStage("BenefitCalculation", 300, isRequired: false, executed),
            new RecordingStage("Scrubbing", 100, isRequired: false, executed),
        };
        SetupAdapterReturningClaim();

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(new[] { "Scrubbing", "BenefitCalculation", "Persistence" }, executed);
    }

    [Fact]
    public async Task Adjudicate_TerminalStage_ShortCircuitsButPersistenceStillRuns()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Scrubbing", 100, isRequired: false, executed,
                _ => ClaimAdjudicationStageResult.Reject("Scrubbing", "structural defect")),
            new RecordingStage("BenefitCalculation", 300, isRequired: false, executed),
            new RecordingStage("NcciEdits", 400, isRequired: false, executed),
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        SetupAdapterReturningClaim();

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(new[] { "Scrubbing", "Persistence" }, executed);
    }

    [Fact]
    public async Task Adjudicate_PendOutcome_DoesNotShortCircuit()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Scrubbing", 100, isRequired: false, executed,
                _ => ClaimAdjudicationStageResult.Pend("Scrubbing", "manual review")),
            new RecordingStage("BenefitCalculation", 300, isRequired: false, executed),
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        SetupAdapterReturningClaim();

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(new[] { "Scrubbing", "BenefitCalculation", "Persistence" }, executed);
    }

    [Fact]
    public async Task Adjudicate_StageThrows_TreatedAsRejectAndPipelineContinuesToPersistence()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Scrubbing", 100, isRequired: false, executed,
                _ => throw new InvalidOperationException("boom")),
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        SetupAdapterReturningClaim();

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(new[] { "Scrubbing", "Persistence" }, executed);
    }

    [Fact]
    public async Task Adjudicate_DisabledStage_IsSkipped()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Scrubbing", 100, isRequired: false, executed),
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        SetupAdapterReturningClaim();

        var options = new AdjudicationPipelineOptions();
        options.EnabledStages["Scrubbing"] = false;

        var orch = BuildOrchestrator(stages, options);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(new[] { "Persistence" }, executed);
    }

    [Fact]
    public async Task Adjudicate_RequiredStage_RunsEvenIfDisabled()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        SetupAdapterReturningClaim();

        var options = new AdjudicationPipelineOptions();
        options.EnabledStages["Persistence"] = false;

        var orch = BuildOrchestrator(stages, options);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(new[] { "Persistence" }, executed);
    }

    [Fact]
    public async Task Adjudicate_EmitsClaimVersionAdjudicatedMessage()
    {
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, new List<string>()),
        };
        SetupAdapterReturningClaim();

        ClaimVersionAdjudicatedMessage? captured = null;
        SendOptions? capturedOptions = null;
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimVersionAdjudicatedMessage>(),
                Arg.Any<SendOptions?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                captured = ci.Arg<ClaimVersionAdjudicatedMessage>();
                capturedOptions = ci.Arg<SendOptions?>();
            });

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("ver-1", captured!.ClaimVersionId);
        Assert.Equal("Pass", captured.Outcome);
        Assert.Equal("adjudicated:ver-1", capturedOptions?.MessageId);
        Assert.Equal("ClaimVersionAdjudicated", capturedOptions?.Properties?["MessageType"]);
    }

    // ── PR #1278 round 3 ──────────────────────────────────────────────

    /// <summary>
    /// M6: the approval re-run's Adjudicated message has its own MessageId,
    /// or Service Bus duplicate detection drops it as a repeat of the first
    /// run's "adjudicated:{ClaimVersionId}".
    /// </summary>
    [Fact]
    public async Task ApprovalRerun_AdjudicatedMessage_HasADistinctMessageId()
    {
        var ids = new List<string?>();
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(), Arg.Any<ClaimVersionAdjudicatedMessage>(), Arg.Any<SendOptions?>(), Arg.Any<CancellationToken>()))
            .Do(ci => ids.Add(ci.Arg<SendOptions?>()?.MessageId));
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator([new RecordingStage("Persistence", 999, isRequired: true, [])]);

        var approval = new ExaminerApproval { ExaminerId = "examiner-1" };
        await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1", approval, CancellationToken.None);
        await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1", new ExaminerApproval { ExaminerId = "examiner-1" }, CancellationToken.None);

        Assert.Equal($"adjudicated:ver-1:approval:{approval.ApprovalId}", ids[0]);
        Assert.NotEqual(ids[0], ids[1]);
        Assert.All(ids, id => Assert.NotEqual("adjudicated:ver-1", id));
    }

    /// <summary>L8: the advisory AI examination does not run on the examiner's own approval re-run.</summary>
    [Fact]
    public async Task ApprovalRerun_SkipsTheAiExamination()
    {
        var executed = new List<string>();
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator(
        [
            new RecordingStage(AiExaminationStage.StageName, 600, isRequired: false, executed),
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        ]);

        await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1", new ExaminerApproval(), CancellationToken.None);

        Assert.Equal(new[] { "Persistence" }, executed);
    }

    /// <summary>
    /// A stage that pends without recording a code (network credentialing)
    /// gets one recorded, so the examiner sees it and an approval can name
    /// it; the re-run overrides it only when that pend was reviewed.
    /// </summary>
    [Fact]
    public async Task NetworkPend_IsRecorded_AndOverriddenOnlyWhenReviewed()
    {
        ClaimAdjudicationContext? seen = null;
        SetupAdapterReturningClaim();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage(NetworkCredentialingStage.StageName, 200, isRequired: false, [],
                _ => ClaimAdjudicationStageResult.Pend(NetworkCredentialingStage.StageName, "Credentialing: lapsed (mode=PendForReview)")),
            new RecordingStage("Persistence", 999, isRequired: true, [], ctx => { seen = ctx; return ClaimAdjudicationStageResult.Pass("Persistence"); }),
        };
        var orch = BuildOrchestrator(stages);

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);
        Assert.Equal("NETWORK", seen!.PendDetails!.PendCode);

        var unreviewed = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ReviewedPend = new PendDetails { PendCode = "DUPLICATE", PendReason = "x" } }, CancellationToken.None);
        var reviewed = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ReviewedPend = seen.PendDetails }, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, unreviewed.Outcome);
        Assert.Empty(unreviewed.OverriddenPends);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, reviewed.Outcome);
        Assert.Equal(new[] { "NetworkCredentialing: NETWORK: Credentialing: lapsed (mode=PendForReview)" }, reviewed.OverriddenPends);
    }

    /// <summary>
    /// Round-3 verification, blocker 1 (scenario B): COB pends at 275, then
    /// benefit calculation replaces PendDetails with a retro plan change.
    /// Both are stored — COB stays the routing pend, RETROELIG is added — so
    /// an approval can cover both (and payerSequence is accepted).
    /// </summary>
    [Fact]
    public async Task SecondPendReplacingTheFirst_BothAreStored_FirstStaysTheRoutingPend()
    {
        ClaimAdjudicationContext? seen = null;
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator(
        [
            new RecordingStage(CoordinationOfBenefitsStage.StageName, 275, isRequired: true, [], ctx =>
            {
                ctx.PendDetails = new PendDetails { PendCode = "COB", PendReason = "cob-payer-order-mismatch" };
                return ClaimAdjudicationStageResult.Pend(CoordinationOfBenefitsStage.StageName, "mismatch");
            }),
            new RecordingStage(BenefitCalculationStage.StageName, 300, isRequired: false, [], ctx =>
            {
                ctx.PendDetails = new PendDetails { PendCode = "RETROELIG", PendReason = "retro plan change 2026-03-01" };
                return ClaimAdjudicationStageResult.Pend(BenefitCalculationStage.StageName, "retro plan change 2026-03-01");
            }),
            new RecordingStage("Persistence", 999, isRequired: true, [], ctx => { seen = ctx; return ClaimAdjudicationStageResult.Pass("Persistence"); }),
        ]);

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal("COB", seen!.PendDetails!.PendCode);
        Assert.Equal("cob-payer-order-mismatch", seen.PendDetails.PendReason);
        Assert.Equal(new[] { "RETROELIG: retro plan change 2026-03-01" }, seen.PendDetails.AdditionalPendReasons);
        Assert.Equal(new[] { ("COB", (string?)"cob-payer-order-mismatch"), ("RETROELIG", "retro plan change 2026-03-01") },
            ExaminerApproval.ReviewedFrom(seen.PendDetails));
    }

    /// <summary>
    /// Scenario A at unit level: DUPLICATE then MEDREVIEW. Reviewing the full
    /// stored set passes; reviewing only the last pend (what used to be
    /// stored) does not — the loop the verifier found.
    /// </summary>
    [Fact]
    public async Task TwoPends_ApprovalOfTheStoredSetPasses_OfTheLastOnlyDoesNot()
    {
        ClaimAdjudicationContext? seen = null;
        SetupAdapterReturningClaim();
        IClaimAdjudicationStage[] Stages() =>
        [
            new RecordingStage(DuplicateClaimStage.StageName, 120, isRequired: false, [], ctx =>
            {
                ctx.PendDetails = new PendDetails { PendCode = "DUPLICATE", PendReason = "Line 1 duplicates CLM-1." };
                return ClaimAdjudicationStageResult.Pend(DuplicateClaimStage.StageName, "Line 1 duplicates CLM-1.");
            }),
            new RecordingStage(ProviderIntegrityStage.StageName, 150, isRequired: false, [], ctx =>
            {
                ctx.PendDetails = new PendDetails { PendCode = "MEDREVIEW", PendReason = "Billing: manual review required." };
                return ClaimAdjudicationStageResult.Pend(ProviderIntegrityStage.StageName, "Billing: manual review required.");
            }),
            new RecordingStage("Persistence", 999, isRequired: true, [], ctx => { seen = ctx; return ClaimAdjudicationStageResult.Pass("Persistence"); }),
        ];
        var orch = BuildOrchestrator(Stages());
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);
        var stored = seen!.PendDetails!;

        var lastOnly = await BuildOrchestrator(Stages()).ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ReviewedPend = new PendDetails { PendCode = "MEDREVIEW", PendReason = "Billing: manual review required." } },
            CancellationToken.None);
        var full = await BuildOrchestrator(Stages()).ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ReviewedPend = stored }, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, lastOnly.Outcome);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, full.Outcome);
        Assert.Equal(2, full.OverriddenPends.Count);
    }

    /// <summary>M4: NCCI needs the exact reason reviewed, not just the code.</summary>
    [Fact]
    public async Task Ncci_DifferentEditOnTheRerun_IsNotOverridden()
    {
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator(
        [
            new RecordingStage(NcciEditsStage.StageName, 400, isRequired: false, [], ctx =>
            {
                ctx.PendDetails = new PendDetails { PendCode = "NCCI", PendReason = "NE002 99214/99215" };
                return ClaimAdjudicationStageResult.Pend(NcciEditsStage.StageName, "NE002 99214/99215");
            }),
            new RecordingStage("Persistence", 999, isRequired: true, []),
        ]);

        var result = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ReviewedPend = new PendDetails { PendCode = "NCCI", PendReason = "NE001 99213/99214" } },
            CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
    }

    /// <summary>
    /// Provider integrity (an exact-reason pend): reviewed "manual review",
    /// re-run says "could not be reached" — not the reviewed finding.
    /// </summary>
    [Fact]
    public async Task ProviderIntegrity_DifferentReasonOnTheRerun_IsNotOverridden()
    {
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator(
        [
            new RecordingStage(ProviderIntegrityStage.StageName, 150, isRequired: false, [], ctx =>
            {
                ctx.PendDetails = new PendDetails { PendCode = "MEDREVIEW", PendReason = "Billing: Provider integrity check could not be reached." };
                return ClaimAdjudicationStageResult.Pend(ProviderIntegrityStage.StageName, "Billing: Provider integrity check could not be reached.");
            }),
            new RecordingStage("Persistence", 999, isRequired: true, []),
        ]);

        var result = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval
            {
                ReviewedPend = new PendDetails { PendCode = "MEDREVIEW", PendReason = "Billing: manual review required." },
            }, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
    }

    [Fact]
    public async Task Adjudicate_FinalOutcomeReflectsHighestPrecedenceFailure()
    {
        // Pend → Reject → Deny: Reject wins over Pend, Deny is suppressed
        // when an earlier Reject already short-circuited. With Reject first
        // the rest of the non-persistence stages don't run; Reject is the
        // final outcome.
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Scrubbing", 100, isRequired: false, new List<string>(),
                _ => ClaimAdjudicationStageResult.Reject("Scrubbing", "bad")),
            new RecordingStage("BenefitCalculation", 300, isRequired: false, new List<string>(),
                _ => ClaimAdjudicationStageResult.Pend("BenefitCalculation", "needs review")),
            new RecordingStage("Persistence", 999, isRequired: true, new List<string>()),
        };
        SetupAdapterReturningClaim();

        ClaimVersionAdjudicatedMessage? captured = null;
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimVersionAdjudicatedMessage>(),
                Arg.Any<SendOptions?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<ClaimVersionAdjudicatedMessage>());

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Reject", captured!.Outcome);
        Assert.Equal("bad", captured.Reason);
    }

    [Fact]
    public async Task Adjudicate_AlreadyAdjudicatedClaim_SkipsPipeline()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Scrubbing", 100, isRequired: false, executed),
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        var alreadyAdjudicated = BuildAdapterClaim();
        alreadyAdjudicated.AdjudicationResult = new AdapterAdjudicationResult { AllowedAmount = 100m };
        SetupAdapterReturning(alreadyAdjudicated);

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Empty(executed);
        await _messageBus.DidNotReceiveWithAnyArgs().SendAsync(
            default!, default(ClaimVersionAdjudicatedMessage)!, default, default);
    }

    [Fact]
    public async Task Adjudicate_SubmittedClaimWithEmptyAdjudicationPlaceholder_RunsPipeline()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Scrubbing", 100, isRequired: false, executed),
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        var submittedWithPlaceholder = BuildAdapterClaim();
        submittedWithPlaceholder.Status = ClaimStatus.Submitted;
        submittedWithPlaceholder.AdjudicationResult = new AdapterAdjudicationResult();
        SetupAdapterReturning(submittedWithPlaceholder);

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(new[] { "Scrubbing", "Persistence" }, executed);
    }

    [Fact]
    public async Task Adjudicate_ClaimNotFoundViaAdapter_SkipsPipelineCleanly()
    {
        var executed = new List<string>();
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, executed),
        };
        _adapter
            .GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse { Platform = "cho", Claim = null });

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Empty(executed);
    }

    [Fact]
    public async Task Adjudicate_AdjudicatedEventPublisherFails_DoesNotBlockServiceBusEmission()
    {
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, new List<string>()),
        };
        SetupAdapterReturningClaim();
        _eventPublisher
            .PublishVersionAdjudicatedAsync(
                Arg.Any<Claim>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Mongo down"));

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        // Service Bus emission must still run despite Mongo failure.
        await _messageBus.Received(1).SendAsync(
            "claim-version-events",
            Arg.Any<ClaimVersionAdjudicatedMessage>(),
            Arg.Any<SendOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adjudicate_ClaimWithoutBenefitPlanId_ResolvesViaCoverage_ThenResolvesPlan()
    {
        // The X12 837 on-ramp submits claims with a blank BenefitPlanId
        // (X12837ClaimMapper deliberately doesn't guess it) — the
        // orchestrator must resolve it from the member's active coverage
        // before plan resolution runs, so a correctly-enrolled member's
        // claim still reaches a real plan instead of rejecting.
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, new List<string>()),
        };
        var claim = BuildAdapterClaim();
        claim.BenefitPlanId = null;
        SetupAdapterReturning(claim);
        _coverageResolver
            .ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", claim.ServiceDateFrom, "HLT", Arg.Any<CancellationToken>())
            .Returns("resolved-plan-guid");

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal("resolved-plan-guid", claim.BenefitPlanId);
        await _planResolver.Received(1).GetPlanAsync("tenant-1", "resolved-plan-guid", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adjudicate_ClaimWithoutBenefitPlanId_NoActiveCoverage_LeavesBenefitPlanIdBlank()
    {
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, new List<string>()),
        };
        var claim = BuildAdapterClaim();
        claim.BenefitPlanId = null;
        SetupAdapterReturning(claim);
        _coverageResolver
            .ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", claim.ServiceDateFrom, "HLT", Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Null(claim.BenefitPlanId);
        await _planResolver.DidNotReceiveWithAnyArgs().GetPlanAsync(default!, default!, default);
    }

    [Fact]
    public async Task Adjudicate_ClaimWithExistingBenefitPlanId_DoesNotCallCoverageResolver()
    {
        // JSON /import and MCC submissions already carry BenefitPlanId —
        // coverage resolution must not override or even query in that case.
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Persistence", 999, isRequired: true, new List<string>()),
        };
        SetupAdapterReturningClaim();

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        await _coverageResolver.DidNotReceiveWithAnyArgs()
            .ResolveBenefitPlanIdAsync(default!, default!, default, default, default);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private ClaimAdjudicationOrchestrator BuildOrchestrator(
        IEnumerable<IClaimAdjudicationStage> stages,
        AdjudicationPipelineOptions? options = null)
    {
        return new ClaimAdjudicationOrchestrator(
            _factory,
            _planResolver,
            _memberResolver,
            _coverageResolver,
            stages,
            _eventPublisher,
            _messageBus,
            new AdjudicationTenantContext(),
            Substitute.For<IClaimAdjustmentService>(),
            Options.Create(options ?? new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
    }

    private void SetupAdapterReturningClaim()
        => SetupAdapterReturning(BuildAdapterClaim());

    private void SetupAdapterReturning(AdapterClaim claim)
    {
        _adapter
            .GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse { Platform = "cho", Claim = claim });
    }

    private static ClaimVersionSubmittedMessage BuildSubmittedMessage() => new()
    {
        TenantId = "tenant-1",
        ClaimId = "ver-1",
        ClaimVersionId = "ver-1",
        VersionNumber = 1,
        ActorId = "actor",
        CorrelationId = "corr-1",
    };

    private static MessageContext BuildContext() => new(
        MessageId: "submitted:ver-1",
        CorrelationId: "corr-1",
        DeliveryCount: 1,
        Properties: new Dictionary<string, string> { ["MessageType"] = "ClaimVersionSubmitted" });

    private static AdapterClaim BuildAdapterClaim()
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        return new AdapterClaim
        {
            TenantId = "tenant-1",
            Id = "ver-1",
            ClaimNumber = "CLM-1",
            ClaimVersionId = "ver-1",
            VersionNumber = 1,
            VersionState = ClaimVersionState.Submitted,
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567890",
            BenefitPlanId = Guid.NewGuid().ToString(),
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
                        ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate },
            },
        };
    }

    private sealed class RecordingStage : IClaimAdjudicationStage
    {
        private readonly List<string> _log;
        private readonly Func<ClaimAdjudicationContext, ClaimAdjudicationStageResult>? _behavior;

        public RecordingStage(
            string name,
            int order,
            bool isRequired,
            List<string> log,
            Func<ClaimAdjudicationContext, ClaimAdjudicationStageResult>? behavior = null)
        {
            Name = name;
            Order = order;
            IsRequired = isRequired;
            _log = log;
            _behavior = behavior;
        }

        public string Name { get; }
        public int Order { get; }
        public bool IsRequired { get; }

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(
            ClaimAdjudicationContext context, CancellationToken ct)
        {
            _log.Add(Name);
            var result = _behavior is null
                ? ClaimAdjudicationStageResult.Pass(Name)
                : _behavior(context);
            return Task.FromResult(result);
        }
    }
}
