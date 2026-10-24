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
using PlanEditsNs = global::ClaimsService.Services.Adjudication.PlanEdits;
using Contracts = global::CloudHealthOffice.PlanEdits.Contracts;

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

    [Theory]
    [InlineData("found", MemberLookupOutcome.Found)]
    [InlineData("missing", MemberLookupOutcome.NotFound)]
    [InlineData("outage", MemberLookupOutcome.Unavailable)]
    public async Task TheMemberLookup_TellsAMissingMemberFromAnOutage(string answer, MemberLookupOutcome expected)
    {
        var seen = MemberLookupOutcome.NotLookedUp;
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("BenefitCalculation", 300, isRequired: false, [], ctx =>
            {
                seen = ctx.MemberLookup;
                return ClaimAdjudicationStageResult.Pass("BenefitCalculation");
            }),
            new RecordingStage("Persistence", 999, isRequired: true, []),
        };
        SetupAdapterReturningClaim();
        switch (answer)
        {
            case "found":
                _memberResolver.GetMemberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true });
                break;
            case "missing":
                _memberResolver.GetMemberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns((ResolvedMember?)null);
                break;
            default:
                _memberResolver.GetMemberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns<ResolvedMember?>(_ => throw new MemberUnavailableException("member-service answered 503"));
                break;
        }

        await BuildOrchestrator(stages).AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(expected, seen);
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
    public async Task Adjudicate_StageThrows_TreatedAsPendAndPipelineContinuesToPersistence()
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

    /// <summary>
    /// A non-required stage that throws is an outage, not a decision: the
    /// claim is persisted Pended (SVCUNAVAIL, stage named in the reason),
    /// never Denied, and the approval re-run counts it as a failed check.
    /// </summary>
    [Fact]
    public async Task Adjudicate_StageThrows_PersistsPendedWithTransientCode_NotDenied()
    {
        SetupAdapterReturningClaim();
        ClaimStatus? persisted = null;
        PendDetails? persistedPend = null;
        var claims = Substitute.For<global::ClaimsService.Repositories.IClaimRepository>();
        claims.UpdateAdjudicationProjectionAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdjudicationResult>(),
                Arg.Any<IReadOnlyList<LineAdjudicationResult>>(), Arg.Any<CancellationToken>(),
                Arg.Any<PendDetails?>(), Arg.Any<bool>(), Arg.Any<ClaimStatus?>(), Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(ci =>
            {
                persistedPend = ci.ArgAt<PendDetails?>(5);
                persisted = ci.ArgAt<ClaimStatus?>(7);
                return true;
            });
        string? outcome = null;
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(), Arg.Any<ClaimVersionAdjudicatedMessage>(), Arg.Any<SendOptions?>(), Arg.Any<CancellationToken>()))
            .Do(ci => outcome = ci.Arg<ClaimVersionAdjudicatedMessage>().Outcome);
        var executed = new List<string>();
        var orch = BuildOrchestrator(
        [
            new RecordingStage(NetworkCredentialingStage.StageName, 200, isRequired: false, executed,
                _ => throw new HttpRequestException("provider-service down")),
            new RecordingStage(BenefitCalculationStage.StageName, 300, isRequired: false, executed),
            new PersistenceStage(claims, NullLogger<PersistenceStage>.Instance),
        ]);

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(ClaimStatus.Pended, persisted);
        Assert.Equal(ClaimAdjudicationOrchestrator.ServiceUnavailablePendCode, persistedPend?.PendCode);
        Assert.Contains(NetworkCredentialingStage.StageName, persistedPend?.PendReason);
        Assert.Contains("HttpRequestException", persistedPend?.PendReason);
        Assert.Equal("Pend", outcome);
        // Later optional stages are skipped: nothing prices on the missing check.
        Assert.Equal(new[] { NetworkCredentialingStage.StageName }, executed);
    }

    /// <summary>
    /// The examiner approval never overrides a SVCUNAVAIL pend: the re-run
    /// retries the check, and the claim stays pended while it still fails.
    /// </summary>
    [Fact]
    public async Task ApprovalRerun_StageStillThrows_StaysPended_AndReportsACheckFailed()
    {
        SetupAdapterReturningClaim();
        var reviewed = new PendDetails
        {
            PendCode = ClaimAdjudicationOrchestrator.ServiceUnavailablePendCode,
            PendReason = $"{ClaimAdjudicationOrchestrator.ServiceUnavailableReason}: {ScrubbingStage.StageName} threw HttpRequestException; " +
                         "retried on an examiner approval re-run",
        };
        var orch = BuildOrchestrator(
        [
            new RecordingStage(ScrubbingStage.StageName, 100, isRequired: false, [],
                _ => throw new HttpRequestException("still down")),
            new RecordingStage("Persistence", 999, isRequired: true, []),
        ]);

        var rerun = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ExaminerId = "examiner-1", ReviewedPend = reviewed }, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, rerun.Outcome);
        Assert.True(rerun.CheckFailed);
        Assert.Empty(rerun.OverriddenPends);
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

    /// <summary>#1299 S-A: the approval re-run names the stage that denied, not the first non-passing stage.</summary>
    [Fact]
    public async Task ApprovalRerun_Deny_ReportsTheDenyingStagesReason()
    {
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator(
        [
            new RecordingStage(CoordinationOfBenefitsStage.StageName, 275, isRequired: false, [], ctx =>
            {
                ctx.PendDetails = new PendDetails { PendCode = "COB", PendReason = "confirm the payer order" };
                return ClaimAdjudicationStageResult.Pend(CoordinationOfBenefitsStage.StageName, "confirm the payer order");
            }),
            new RecordingStage(BenefitCalculationStage.StageName, 300, isRequired: false, [], _ =>
                ClaimAdjudicationStageResult.Deny(BenefitCalculationStage.StageName, BenefitCalculationStage.PriorAuthorizationRequiredReason)),
            new RecordingStage("Persistence", 999, isRequired: true, []),
        ]);

        var result = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1", new ExaminerApproval(), CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal($"{BenefitCalculationStage.StageName}: {BenefitCalculationStage.PriorAuthorizationRequiredReason}", result.DenialReason);
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
            .ResolveCoverageAsync("tenant-1", "MEM-1", claim.ServiceDateFrom, "HLT", Arg.Any<CancellationToken>())
            .Returns(new CoverageResolution("resolved-plan-guid"));

        var orch = BuildOrchestrator(stages);
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal("resolved-plan-guid", claim.BenefitPlanId);
        await _planResolver.Received(1).GetPlanAsync("tenant-1", "resolved-plan-guid", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #1299 S2: only a plan lookup without a definitive answer
    /// (<see cref="BenefitPlanUnavailableException"/>) is flagged. A definitive
    /// 404 (resolver returns null) keeps main's behaviour: not flagged.
    /// </summary>
    [Theory]
    [InlineData("resolves", false)]
    [InlineData("not-found", false)]
    [InlineData("unavailable", true)]
    public async Task Adjudicate_FlagsOnlyATransientPlanResolutionFailure(string plan, bool flagged)
    {
        bool? observed = null;
        ResolvedBenefitPlan? seen = null;
        var stages = new IClaimAdjudicationStage[]
        {
            new RecordingStage("Observe", 100, isRequired: false, new List<string>(), ctx =>
            {
                observed = ctx.PlanResolutionFailed;
                seen = ctx.ResolvedPlan;
                return ClaimAdjudicationStageResult.Pass("Observe");
            }),
            new RecordingStage("Persistence", 999, isRequired: true, new List<string>()),
        };
        var claim = BuildAdapterClaim();
        SetupAdapterReturning(claim);
        var call = _planResolver.GetPlanAsync("tenant-1", claim.BenefitPlanId!, Arg.Any<CancellationToken>());
        switch (plan)
        {
            case "resolves": call.Returns(new ResolvedBenefitPlan { Id = claim.BenefitPlanId! }); break;
            case "not-found": call.Returns((ResolvedBenefitPlan?)null); break;
            default: call.ThrowsAsync(new BenefitPlanUnavailableException("benefit-plan-service returned 503")); break;
        }

        await BuildOrchestrator(stages).AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal(flagged, observed);
        Assert.Equal(plan == "resolves", seen is not null);
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
            .ResolveCoverageAsync("tenant-1", "MEM-1", claim.ServiceDateFrom, "HLT", Arg.Any<CancellationToken>())
            .Returns((CoverageResolution?)null);

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
            .ResolveCoverageAsync(default!, default!, default, default, default);
    }

    /// <summary>
    /// PR #1279 review B1: examiner A's approval re-run outlives its lock and
    /// examiner B takes over. A's fenced write is refused; that run used to
    /// emit a Reject (audit, Service Bus, adjustment callback), moving an
    /// in-flight adjustment AwaitingReadjudication → Failed, so B's Pass
    /// callback was an idempotent no-op and the predecessor was never
    /// reversed. A lost-lock run now emits nothing; B's finalize moves the
    /// adjustment to PendingReversal.
    /// </summary>
    [Fact]
    public async Task ApprovalRerun_LostLock_EmitsNothing_AndTheNewHoldersPassMovesTheAdjustment()
    {
        SetupAdapterReturningClaim();
        var claims = Substitute.For<global::ClaimsService.Repositories.IClaimRepository>();
        claims.UpdateAdjudicationProjectionAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdjudicationResult>(),
                Arg.Any<IReadOnlyList<LineAdjudicationResult>>(), Arg.Any<CancellationToken>(),
                Arg.Any<PendDetails?>(), Arg.Any<bool>(), Arg.Any<ClaimStatus?>(), Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(ci => ci.ArgAt<string?>(9) == "lock-b"); // B holds the lock now
        var adjustment = new ClaimAdjustment
        {
            Id = "adj-1", TenantId = "tenant-1", ClaimVersionId = "chain-1", PredecessorClaimId = "pred-1",
            PredecessorVersionId = "pred-1", NewClaimId = "ver-1", AdjustmentReason = "correction",
            IdempotencyKey = "idem-1", RequestHash = "hash", CreatedBy = "actor",
            Status = ClaimAdjustmentStatus.AwaitingReadjudication,
        };
        var adjustments = Substitute.For<global::ClaimsService.Repositories.IClaimAdjustmentRepository>();
        adjustments.GetByNewClaimIdAsync("tenant-1", "ver-1", Arg.Any<CancellationToken>()).Returns(adjustment);
        var adjustmentService = new ClaimAdjustmentService(
            claims, adjustments, Substitute.For<IClaimSubmissionService>(), _eventPublisher, _messageBus,
            NullLogger<ClaimAdjustmentService>.Instance);
        var orch = BuildOrchestrator(
            [new PersistenceStage(claims, NullLogger<PersistenceStage>.Instance)],
            adjustmentService: adjustmentService);

        var a = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ExaminerId = "examiner-a", ResolutionLockToken = "lock-a" }, CancellationToken.None);

        Assert.True(a.ResolutionLockLost);
        Assert.NotEqual(ClaimAdjudicationOutcome.Pass, a.Outcome);
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishVersionAdjudicatedAsync(default!, default!, default, default);
        await _messageBus.DidNotReceiveWithAnyArgs().SendAsync<ClaimVersionAdjudicatedMessage>(default!, default!, default, default);
        Assert.Equal(ClaimAdjustmentStatus.AwaitingReadjudication, adjustment.Status);
        await adjustments.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);

        var b = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ExaminerId = "examiner-b", ResolutionLockToken = "lock-b" }, CancellationToken.None);

        Assert.False(b.ResolutionLockLost);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, b.Outcome);
        Assert.Equal(ClaimAdjustmentStatus.PendingReversal, adjustment.Status);
        await _eventPublisher.ReceivedWithAnyArgs(1).PublishVersionAdjudicatedAsync(default!, default!, default, default);
    }

    // ── The plan's own edits (ADR 019) ────────────────────────────────────

    private static PlanEditsStage PlanEdits(params Contracts.PlanEdit[] edits) =>
        new(new FixedPlanEditClient(edits), NullLogger<PlanEditsStage>.Instance);

    private static readonly Contracts.PlanEdit PlanDenial =
        new() { RuleId = "PLAN-NC-7", Message = "Not covered", LineNumbers = [1], SuggestedCarc = "96" };

    private async Task<PendDetails> PlanEditPendAsync()
    {
        ClaimAdjudicationContext? seen = null;
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[]
        {
            PlanEdits(PlanDenial),
            new RecordingStage("Persistence", 999, isRequired: true, [], ctx => { seen = ctx; return ClaimAdjudicationStageResult.Pass("Persistence"); }),
        });
        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);
        return seen!.PendDetails!;
    }

    [Fact]
    public async Task PlanEdit_pend_is_recorded_with_its_edits()
    {
        var pend = await PlanEditPendAsync();

        Assert.Equal("PLANEDIT", pend.PendCode);
        Assert.Equal("Plan edit PLAN-NC-7: Not covered", pend.PendReason);
        Assert.Equal("PLAN|PLAN-NC-7|1", Assert.Single(pend.PlanEditFindings).EditKey);
    }

    [Fact]
    public async Task PlanEdit_pend_is_never_passed_on_a_reviewed_reason_alone()
    {
        var pend = await PlanEditPendAsync();
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[] { PlanEdits(PlanDenial) });

        // The examiner reviewed exactly this pend but decided no edit.
        var result = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1",
            new ExaminerApproval { ExaminerId = "examiner-1", ReviewedPend = pend }, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Empty(result.OverriddenPends);
    }

    [Fact]
    public async Task PlanEdit_on_new_lines_pends_even_when_its_reason_matches_the_reviewed_one()
    {
        var pend = await PlanEditPendAsync();
        var widened = PlanDenial with { LineNumbers = [1, 2] };
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[] { PlanEdits(widened) });

        var result = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1", new ExaminerApproval
        {
            ExaminerId = "examiner-1",
            ReviewedPend = pend,
            EditDispositions = [new EditDisposition { EditKey = "PLAN|PLAN-NC-7|1", Decision = EditDecision.Override, Reason = "ok" }],
        }, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
    }

    [Fact]
    public async Task PlanEdit_decided_by_the_examiner_passes_on_the_approval_re_run()
    {
        var pend = await PlanEditPendAsync();
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[] { PlanEdits(PlanDenial) });

        var result = await orch.ReadjudicateForApprovalAsync("tenant-1", "ver-1", new ExaminerApproval
        {
            ExaminerId = "examiner-1",
            ReviewedPend = pend,
            EditDispositions = [new EditDisposition { EditKey = "PLAN|PLAN-NC-7|1", Decision = EditDecision.Uphold, UpheldByDefault = true }],
        }, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Contains(result.OverriddenPends, p => p.Contains("PLAN-NC-7 upheld by examiner"));
    }

    [Fact]
    public async Task PlanEdit_after_an_earlier_pend_is_added_to_it_and_keeps_its_edits()
    {
        ClaimAdjudicationContext? seen = null;
        SetupAdapterReturningClaim();
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[]
        {
            new RecordingStage(NetworkCredentialingStage.StageName, 200, isRequired: false, [],
                _ => ClaimAdjudicationStageResult.Pend(NetworkCredentialingStage.StageName, "Credentialing: lapsed")),
            PlanEdits(PlanDenial),
            new RecordingStage("Persistence", 999, isRequired: true, [], ctx => { seen = ctx; return ClaimAdjudicationStageResult.Pass("Persistence"); }),
        });

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildContext(), CancellationToken.None);

        Assert.Equal("NETWORK", seen!.PendDetails!.PendCode);
        Assert.Contains("PLANEDIT: Plan edit PLAN-NC-7: Not covered", seen.PendDetails.AdditionalPendReasons);
        Assert.Single(seen.PendDetails.PlanEditFindings);
    }

    private sealed class FixedPlanEditClient(params Contracts.PlanEdit[] edits)
        : PlanEditsNs.IPlanEditServiceClient
    {
        public PlanEditsNs.PlanEditTenantOptions? For(string tenantId) =>
            new() { Endpoint = "https://edits.plan.example/v1" };

        public Task<PlanEditsNs.PlanEditCallResult> EvaluateAsync(
            string tenantId, Contracts.PlanEditRequest request,
            IReadOnlyCollection<int> claimLineNumbers, CancellationToken ct) =>
            Task.FromResult<PlanEditsNs.PlanEditCallResult>(
                new PlanEditsNs.PlanEditCallResult.Answered(edits));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private ClaimAdjudicationOrchestrator BuildOrchestrator(
        IEnumerable<IClaimAdjudicationStage> stages,
        AdjudicationPipelineOptions? options = null,
        IClaimAdjustmentService? adjustmentService = null)
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
            adjustmentService ?? Substitute.For<IClaimAdjustmentService>(),
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
