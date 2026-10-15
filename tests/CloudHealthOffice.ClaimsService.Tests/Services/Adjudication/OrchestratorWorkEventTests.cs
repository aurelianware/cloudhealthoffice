using System.Net.Http;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
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
/// The adjudication pipeline tells work-management-service a claim needs an
/// examiner exactly when a run ends Pended (claims.work.v1 work-needed), and
/// a publish failure never fails adjudication.
/// </summary>
public class OrchestratorWorkEventTests
{
    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IClaimWorkEventPublisher _work = Substitute.For<IClaimWorkEventPublisher>();
    private readonly ClaimAdapterFactory _factory;

    public OrchestratorWorkEventTests()
    {
        _adapter.Platform.Returns("cho");
        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(), Substitute.For<IConfiguration>(), NullLogger<ClaimTenantConfigCache>.Instance);
        _factory = new ClaimAdapterFactory(new[] { _adapter }, cache, NullLogger<ClaimAdapterFactory>.Instance);
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        _adapter.GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse
            {
                Platform = "cho",
                Claim = new AdapterClaim
                {
                    TenantId = "tenant-1", Id = "ver-1", ClaimNumber = "CLM-1", ClaimVersionId = "ver-1",
                    VersionNumber = 1, VersionState = ClaimVersionState.Submitted, MemberId = "MEM-1",
                    BillingProviderNPI = "1234567890", BenefitPlanId = Guid.NewGuid().ToString(),
                    LineOfBusiness = LineOfBusiness.Commercial, ClaimType = ClaimType.Professional,
                    TotalChargeAmount = 100m, ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate,
                    ClaimLines = [new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
                                          ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate }],
                },
            });
    }

    private ClaimAdjudicationOrchestrator Build(params IClaimAdjudicationStage[] stages) => new(
        _factory,
        Substitute.For<IBenefitPlanResolver>(),
        Substitute.For<IMemberResolver>(),
        Substitute.For<ICoverageResolver>(),
        stages,
        Substitute.For<IClaimVersionEventPublisher>(),
        Substitute.For<IMessageBus>(),
        new AdjudicationTenantContext(),
        Substitute.For<IClaimAdjustmentService>(),
        Options.Create(new AdjudicationPipelineOptions()),
        NullLogger<ClaimAdjudicationOrchestrator>.Instance,
        outbox: null,
        workEvents: _work);

    private static Task Run(ClaimAdjudicationOrchestrator orchestrator) => orchestrator.AdjudicateAsync(
        new ClaimVersionSubmittedMessage { TenantId = "tenant-1", ClaimId = "ver-1", ClaimVersionId = "ver-1", VersionNumber = 1, ActorId = "actor" },
        new MessageContext("m-1", "c-1", 1, new Dictionary<string, string>()),
        CancellationToken.None);

    [Fact]
    public async Task APendedRun_PublishesWorkNeeded_WithThePend()
    {
        await Run(Build(
            new Stage("Scrubbing", 100, _ => ClaimAdjudicationStageResult.Pend("Scrubbing", "manual review")),
            new Stage("Persistence", 999, required: true)));

        await _work.Received(1).PublishWorkNeededAsync(
            Arg.Is<Claim>(c => c.Id == "ver-1" && c.Status == ClaimStatus.Pended && c.PendDetails != null),
            "tenant-1", "actor", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("deny")]
    public async Task ARunThatDoesNotPend_IsNotWork(string outcome)
    {
        await Run(Build(
            new Stage("Scrubbing", 100, _ => outcome == "deny"
                ? ClaimAdjudicationStageResult.Deny("Scrubbing", "not covered")
                : ClaimAdjudicationStageResult.Pass("Scrubbing")),
            new Stage("Persistence", 999, required: true)));

        await _work.DidNotReceiveWithAnyArgs().PublishWorkNeededAsync(default!, default!, default);
    }

    [Fact]
    public async Task APublishFailure_DoesNotFailAdjudication()
    {
        _work.PublishWorkNeededAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("bus down"));

        var run = Run(Build(
            new Stage("Scrubbing", 100, _ => ClaimAdjudicationStageResult.Pend("Scrubbing", "manual review")),
            new Stage("Persistence", 999, required: true)));

        await run; // does not throw
        await _work.ReceivedWithAnyArgs(1).PublishWorkNeededAsync(default!, default!, default);
    }

    private sealed class Stage(
        string name, int order, Func<ClaimAdjudicationContext, ClaimAdjudicationStageResult>? behavior = null, bool required = false)
        : IClaimAdjudicationStage
    {
        public string Name => name;
        public int Order => order;
        public bool IsRequired => required;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct) =>
            Task.FromResult(behavior?.Invoke(context) ?? ClaimAdjudicationStageResult.Pass(name));
    }
}
