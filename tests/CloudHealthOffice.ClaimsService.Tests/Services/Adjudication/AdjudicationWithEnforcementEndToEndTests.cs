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
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// Capability 5.6 — pipeline-level integration that wires the REAL
/// <see cref="NetworkCredentialingStage"/> into the orchestrator with
/// substituted upstream clients. Verifies the cross-stage contract:
/// enforcement outcomes flow onto the context, the stage's
/// pass/pend/deny decision drives the orchestrator's short-circuit
/// behavior, and the final adjudicated message captures the outcome.
/// </summary>
public class AdjudicationWithEnforcementEndToEndTests
{
    private const string TenantId = "tenant-1";
    private const string Network1 = "net-1";
    private const string Npi = "1234567890";
    private const string ProviderId = "p-001";

    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IBenefitPlanResolver _planResolver = Substitute.For<IBenefitPlanResolver>();
    private readonly IMemberResolver _memberResolver = Substitute.For<IMemberResolver>();
    private readonly ICoverageResolver _coverageResolver = Substitute.For<ICoverageResolver>();
    private readonly IClaimVersionEventPublisher _eventPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly IProviderMembershipClient _membership = Substitute.For<IProviderMembershipClient>();
    private readonly ICredentialingStatusClient _credentialing = Substitute.For<ICredentialingStatusClient>();
    private readonly ClaimAdapterFactory _factory;

    public AdjudicationWithEnforcementEndToEndTests()
    {
        _adapter.Platform.Returns("cho");
        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        _factory = new ClaimAdapterFactory(
            new[] { _adapter }, cache,
            NullLogger<ClaimAdapterFactory>.Instance);
    }

    [Fact]
    public async Task ApprovedMember_ApprovedCredentialing_pipeline_passes_through_to_persistence()
    {
        // Given: the billing provider is an active member of tier 1 and
        // credentialed-Approved on the service date.
        SetupAdapterReturningClaim();
        SetupPlanWithTier(Network1, "InNetwork", level: 1);
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot
            {
                ProviderId = ProviderId, Status = "Approved",
            });

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestratorWithRealStage();

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Pass", captured.Message!.Outcome);
    }

    [Fact]
    public async Task OutOfNetwork_provider_denies_at_enforcement_stage()
    {
        SetupAdapterReturningClaim();
        SetupPlanWithTier(Network1, "InNetwork", level: 1);
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestratorWithRealStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.FailClosed,
        });

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Deny", captured.Message!.Outcome);
    }

    [Fact]
    public async Task PreCredentialing_serviceDate_denies_at_enforcement_stage()
    {
        // Membership is active; credentialing is Pending as of the
        // claim's service date — the cross-service consumer's primary
        // motivating case.
        SetupAdapterReturningClaim();
        SetupPlanWithTier(Network1, "InNetwork", level: 1);
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot { ProviderId = ProviderId, Status = "Pending" });

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestratorWithRealStage();

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Deny", captured.Message!.Outcome);
    }

    [Fact]
    public async Task FailOpen_membership_pends_instead_of_denies()
    {
        SetupAdapterReturningClaim();
        SetupPlanWithTier(Network1, "InNetwork", level: 1);
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestratorWithRealStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.FailOpen,
        });

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Pend", captured.Message!.Outcome);
    }

    [Fact]
    public async Task SoftValidation_passes_through_observation_only()
    {
        SetupAdapterReturningClaim();
        SetupPlanWithTier(Network1, "InNetwork", level: 1);
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestratorWithRealStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.SoftValidation,
        });

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        // Soft-validation = enforcement records an observation; the
        // pipeline keeps running. With no other stages registered the
        // outcome is Pass.
        Assert.NotNull(captured.Message);
        Assert.Equal("Pass", captured.Message!.Outcome);
    }

    private void SetupAdapterReturningClaim()
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            TenantId = TenantId,
            Id = "ver-1",
            ClaimNumber = "CLM-1",
            ClaimVersionId = "ver-1",
            VersionNumber = 1,
            VersionState = ClaimVersionState.Submitted,
            MemberId = "MEM-1",
            BillingProviderNPI = Npi,
            BenefitPlanId = "plan-1",
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
        _adapter
            .GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse { Platform = "cho", Claim = claim });
    }

    private void SetupPlanWithTier(string networkId, string tierName, int level)
    {
        _planResolver
            .GetPlanAsync(TenantId, "plan-1", Arg.Any<CancellationToken>())
            .Returns(new ResolvedBenefitPlan
            {
                Id = "plan-1",
                NetworkTiers = new[]
                {
                    new ResolvedNetworkTier
                    {
                        TierName = tierName,
                        TierLevel = level,
                        NetworkId = networkId,
                    },
                },
            });
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

    private ClaimAdjudicationOrchestrator BuildOrchestratorWithRealStage(
        TenantEnforcementPolicyOptions? policyOptions = null)
    {
        var stage = new NetworkCredentialingStage(
            _membership, _credentialing,
            Options.Create(policyOptions ?? new TenantEnforcementPolicyOptions()),
            NullLogger<NetworkCredentialingStage>.Instance);

        // Persistence stand-in keeps the orchestrator's "always run
        // PersistenceStage" contract; it doesn't write to a repo here
        // because we're asserting against the emitted message, not the
        // database state.
        var persistence = new TestPersistenceStage();

        return new ClaimAdjudicationOrchestrator(
            _factory,
            _planResolver,
            _memberResolver,
            _coverageResolver,
            new IClaimAdjudicationStage[] { stage, persistence },
            _eventPublisher,
            _messageBus,
            new AdjudicationTenantContext(),
            Substitute.For<IClaimAdjustmentService>(),
            Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
    }

    private static ClaimVersionSubmittedMessage BuildSubmittedMessage() => new()
    {
        TenantId = TenantId,
        ClaimId = "ver-1",
        ClaimVersionId = "ver-1",
        VersionNumber = 1,
        ActorId = "actor",
        CorrelationId = "corr-1",
    };

    private static MessageContext BuildMessageContext() => new(
        MessageId: "submitted:ver-1",
        CorrelationId: "corr-1",
        DeliveryCount: 1,
        Properties: new Dictionary<string, string> { ["MessageType"] = "ClaimVersionSubmitted" });

    private sealed class MessageCapture
    {
        public ClaimVersionAdjudicatedMessage? Message { get; set; }
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
