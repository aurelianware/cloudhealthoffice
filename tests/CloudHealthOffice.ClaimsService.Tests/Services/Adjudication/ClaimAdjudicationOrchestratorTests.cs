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
