using System.Net.Http;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.ClaimsScrubEngine.Configuration;
using CloudHealthOffice.ClaimsScrubEngine.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Integration;

/// <summary>
/// Capability 5.4 — pipeline-level integration that wires the REAL
/// <see cref="ScrubbingStage"/> backed by the REAL
/// <c>CloudHealthOffice.ClaimsScrubEngine</c> with default rules.
/// Verifies the cross-stage contract: clean claim flows through to
/// PersistenceStage; structurally invalid claim short-circuits with
/// <c>Reject</c>.
/// </summary>
public class AdjudicationWithScrubbingEndToEndTests
{
    private const string TenantId = "tenant-1";

    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IBenefitPlanResolver _planResolver = Substitute.For<IBenefitPlanResolver>();
    private readonly IMemberResolver _memberResolver = Substitute.For<IMemberResolver>();
    private readonly ICoverageResolver _coverageResolver = Substitute.For<ICoverageResolver>();
    private readonly IClaimVersionEventPublisher _eventPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ClaimAdapterFactory _factory;
    private readonly IClaimRoutingService _engine;

    public AdjudicationWithScrubbingEndToEndTests()
    {
        _adapter.Platform.Returns("cho");
        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        _factory = new ClaimAdapterFactory(
            new[] { _adapter }, cache,
            NullLogger<ClaimAdapterFactory>.Instance);

        // Stand up the real engine the same way Program.cs does so the
        // E2E test exercises the production wiring, not a stand-in.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaimsScrubEngine();
        _engine = services.BuildServiceProvider()
            .GetRequiredService<IClaimRoutingService>();
    }

    [Fact]
    public async Task CleanClaim_passes_through_to_persistence()
    {
        SetupAdapterReturningClaim(BuildCleanClaim());
        SetupResolvedMember(dob: new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator();

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Pass", captured.Message!.Outcome);
    }

    [Fact]
    public async Task ClaimMissingBillingNpi_short_circuits_with_Reject()
    {
        var claim = BuildCleanClaim();
        claim.BillingProviderNPI = string.Empty; // DC003 + PV001 fail
        SetupAdapterReturningClaim(claim);
        SetupResolvedMember(dob: new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator();

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Reject", captured.Message!.Outcome);
    }

    [Fact]
    public async Task ClaimWithNoServiceLines_short_circuits_with_Reject()
    {
        var claim = BuildCleanClaim();
        claim.ClaimLines.Clear(); // DC005 fails (MinServiceLines = 1)
        SetupAdapterReturningClaim(claim);
        SetupResolvedMember(dob: new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator();

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Reject", captured.Message!.Outcome);
    }

    [Fact]
    public async Task NullResolvedMember_rejects_via_DC002()
    {
        // Member resolution miss → mapper has no DOB → engine's DC002
        // (Subscriber DOB Required, Error) fails. The honest behavior:
        // claim rejects rather than silently skipping a load-bearing rule.
        SetupAdapterReturningClaim(BuildCleanClaim());
        _memberResolver.GetMemberAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ResolvedMember?)null);

        var captured = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator();

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.NotNull(captured.Message);
        Assert.Equal("Reject", captured.Message!.Outcome);
    }

    private void SetupAdapterReturningClaim(AdapterClaim claim)
    {
        _adapter
            .GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse { Platform = "cho", Claim = claim });
    }

    private void SetupResolvedMember(DateTime dob)
    {
        _memberResolver.GetMemberAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember
            {
                MemberId = "MEM-1",
                DateOfBirth = dob,
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

    private ClaimAdjudicationOrchestrator BuildOrchestrator()
    {
        var scrubbing = new ScrubbingStage(_engine, NullLogger<ScrubbingStage>.Instance);
        var persistence = new TestPersistenceStage();

        return new ClaimAdjudicationOrchestrator(
            _factory,
            _planResolver,
            _memberResolver,
            _coverageResolver,
            new IClaimAdjudicationStage[] { scrubbing, persistence },
            _eventPublisher,
            _messageBus,
            new AdjudicationTenantContext(),
            Substitute.For<IClaimAdjustmentService>(),
            Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
    }

    private static AdapterClaim BuildCleanClaim()
    {
        var serviceDate = DateTime.UtcNow.AddDays(-7).Date;
        return new AdapterClaim
        {
            TenantId = TenantId,
            Id = "ver-1",
            ClaimNumber = "CLM-1",
            ClaimVersionId = "ver-1",
            VersionNumber = 1,
            VersionState = ClaimVersionState.Submitted,
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567893", // Luhn-valid (PV001)
            ClaimType = ClaimType.Professional,
            ClaimFrequencyCode = "1",
            PlaceOfServiceCode = "11",
            TotalChargeAmount = 100m,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            SubmittedDate = serviceDate,
            DiagnosisCodes = new List<AdapterDiagnosisCode>
            {
                new() { Code = "Z00.00", PointerNumber = 1 },
            },
            ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 100m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                    DiagnosisPointers = new List<int> { 1 },
                },
            },
        };
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
