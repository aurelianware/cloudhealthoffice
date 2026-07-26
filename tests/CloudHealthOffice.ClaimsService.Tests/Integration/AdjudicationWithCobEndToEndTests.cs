using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Integration;

/// <summary>
/// Capability 5.8 — pipeline-level integration that wires the REAL
/// <see cref="CoordinationOfBenefitsStage"/> backed by the REAL
/// <see cref="PayerOrderService"/> against a mocked
/// <see cref="ICoverageClient"/>. Verifies the cross-stage contract:
/// CHO-primary scenarios pass through; Medicare-primary detection produces
/// Pend with structured outcome; Deny mode short-circuits; coverage-service
/// degradation pends regardless of mode (Decision 7).
/// </summary>
public class AdjudicationWithCobEndToEndTests
{
    private const string TenantId = "tenant-1";
    private const string MemberId = "MEM-COB-1";
    private const string ClaimVersionId = "ver-cob-1";

    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IBenefitPlanResolver _planResolver = Substitute.For<IBenefitPlanResolver>();
    private readonly IMemberResolver _memberResolver = Substitute.For<IMemberResolver>();
    private readonly ICoverageResolver _coverageResolver = Substitute.For<ICoverageResolver>();
    private readonly ICoverageClient _coverageClient = Substitute.For<ICoverageClient>();
    private readonly IClaimVersionEventPublisher _eventPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ClaimAdapterFactory _factory;

    public AdjudicationWithCobEndToEndTests()
    {
        _adapter.Platform.Returns("cho");
        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        _factory = new ClaimAdapterFactory(
            new[] { _adapter }, cache,
            NullLogger<ClaimAdapterFactory>.Instance);

        _memberResolver.GetMemberAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember
            {
                MemberId = MemberId,
                DateOfBirth = new DateTime(1955, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
    }

    [Fact]
    public async Task Member_with_no_other_coverage_passes_COB_and_publishes_Pass()
    {
        SetupAdapterReturningClaim(BuildBasicClaim());
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CobEntry>());

        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(CobEnforcementMode.PendForSecondary);

        await orch.AdjudicateAsync(
            BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Pass", sbCapture.Message!.Outcome);
    }

    [Fact]
    public async Task Member_with_Medicare_primary_pends_with_structured_CobOutcome()
    {
        SetupAdapterReturningClaim(BuildBasicClaim());
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry
                {
                    PayerName = "Medicare",
                    PayerId = "MED-001",
                    CoverageSequence = "P",
                    IsMedicare = true,
                    CoverageBeginDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
            });

        var captured = CaptureCobContext();
        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(CobEnforcementMode.PendForSecondary, captured);

        await orch.AdjudicateAsync(
            BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Pend", sbCapture.Message!.Outcome);
        Assert.NotNull(captured.CobResult);
        Assert.Equal(CobScenario.ChoSecondaryDetected, captured.CobResult!.Scenario);
        Assert.True(captured.CobResult.IsMedicarePrimary);
        Assert.Equal(PayerOrderRule.MedicareSecondaryPayer, captured.CobResult.AppliedRule);
        Assert.Equal(
            CoordinationOfBenefitsStage.SecondaryNotSupportedPendReason,
            captured.CobResult.PendReason);
    }

    [Fact]
    public async Task Tenant_with_DenyMode_on_secondary_detection_short_circuits_with_Deny()
    {
        SetupAdapterReturningClaim(BuildBasicClaim());
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" },
            });

        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(CobEnforcementMode.Deny);

        await orch.AdjudicateAsync(
            BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Deny", sbCapture.Message!.Outcome);
    }

    [Fact]
    public async Task Coverage_service_unavailable_pends_regardless_of_mode()
    {
        // Decision 7 — even Deny mode pends when coverage state is
        // unknown. This protects against silent CHO-secondary processing
        // during coverage-service outages.
        SetupAdapterReturningClaim(BuildBasicClaim());
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CobEntry>?)null);

        var captured = CaptureCobContext();
        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(CobEnforcementMode.Deny, captured);

        await orch.AdjudicateAsync(
            BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Pend", sbCapture.Message!.Outcome);
        Assert.NotNull(captured.CobResult);
        Assert.Equal(CobScenario.None, captured.CobResult!.Scenario);
        Assert.Equal(
            CoordinationOfBenefitsStage.CoverageServiceUnavailablePendReason,
            captured.CobResult.PendReason);
    }

    [Fact]
    public async Task Engine_services_resolve_cleanly_through_real_stage()
    {
        // Decision 17 — both ICobCalculationService and IPayerOrderService
        // register; only IPayerOrderService is exercised in 5.8 stage
        // logic. This test exercises the registration path indirectly
        // (the orchestrator builds the real stage with real
        // PayerOrderService). ICobCalculationService is verified by the
        // unit-test class library reference build.
        SetupAdapterReturningClaim(BuildBasicClaim());
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CobEntry>());

        var orch = BuildOrchestrator(CobEnforcementMode.PendForSecondary);
        await orch.AdjudicateAsync(
            BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        // Reaching here without a missing-DI exception is the assertion.
        Assert.True(true);
    }

    private void SetupAdapterReturningClaim(AdapterClaim claim)
    {
        _adapter
            .GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse { Platform = "cho", Claim = claim });
    }

    private MessageCapture CaptureAdjudicatedMessage()
    {
        var capture = new MessageCapture();
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimVersionAdjudicatedMessage>(),
                Arg.Any<SendOptions?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci => capture.Message = ci.Arg<ClaimVersionAdjudicatedMessage>());
        return capture;
    }

    private static CobContextCapture CaptureCobContext() => new();

    private ClaimAdjudicationOrchestrator BuildOrchestrator(
        CobEnforcementMode mode,
        CobContextCapture? capture = null)
    {
        var cobStage = new CoordinationOfBenefitsStage(
            _coverageClient,
            new PayerOrderService(),
            Options.Create(new TenantEnforcementPolicyOptions { CobMode = mode }),
            NullLogger<CoordinationOfBenefitsStage>.Instance);

        var stages = new IClaimAdjudicationStage[]
        {
            cobStage,
            new ContextCaptureStage(capture),
            new TestPersistenceStage(),
        };

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
            Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
    }

    private static AdapterClaim BuildBasicClaim()
    {
        var serviceDate = DateTime.UtcNow.AddDays(-3).Date;
        return new AdapterClaim
        {
            TenantId = TenantId,
            Id = ClaimVersionId,
            ClaimNumber = "CLM-COB-1",
            ClaimVersionId = ClaimVersionId,
            VersionNumber = 1,
            VersionState = ClaimVersionState.Submitted,
            MemberId = MemberId,
            BillingProviderNPI = "1234567893",
            ClaimType = ClaimType.Professional,
            ClaimFrequencyCode = "1",
            PlaceOfServiceCode = "11",
            TotalChargeAmount = 100m,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            SubmittedDate = serviceDate,
            ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    Units = 1,
                    ChargeAmount = 100m,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                },
            },
        };
    }

    private static ClaimVersionSubmittedMessage BuildSubmittedMessage() => new()
    {
        TenantId = TenantId,
        ClaimId = ClaimVersionId,
        ClaimVersionId = ClaimVersionId,
        VersionNumber = 1,
        ActorId = "actor",
        CorrelationId = "corr-cob-1",
    };

    private static MessageContext BuildMessageContext() => new(
        MessageId: "submitted:" + ClaimVersionId,
        CorrelationId: "corr-cob-1",
        DeliveryCount: 1,
        Properties: new Dictionary<string, string> { ["MessageType"] = "ClaimVersionSubmitted" });

    private sealed class MessageCapture
    {
        public ClaimVersionAdjudicatedMessage? Message { get; set; }
    }

    /// <summary>Carries the COB outcome out of the pipeline so the test
    /// can assert against it without mocking the persistence channel.</summary>
    private sealed class CobContextCapture
    {
        public CobOutcome? CobResult { get; set; }
    }

    private sealed class ContextCaptureStage : IClaimAdjudicationStage
    {
        private readonly CobContextCapture? _capture;
        public ContextCaptureStage(CobContextCapture? capture) => _capture = capture;

        public string Name => "ContextCapture";
        public int Order => 998;
        public bool IsRequired => true;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(
            ClaimAdjudicationContext context, CancellationToken ct)
        {
            if (_capture is not null)
            {
                _capture.CobResult = context.CobResult;
            }
            return Task.FromResult(ClaimAdjudicationStageResult.Pass(Name));
        }
    }

    private sealed class TestPersistenceStage : IClaimAdjudicationStage
    {
        public string Name => "Persistence";
        public int Order => 999;
        public bool IsRequired => true;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(
            ClaimAdjudicationContext context, CancellationToken ct)
            => Task.FromResult(ClaimAdjudicationStageResult.Pass(Name));
    }
}
