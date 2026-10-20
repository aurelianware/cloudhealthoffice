using System.Net;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.NcciEngine.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using FeeModels = CloudHealthOffice.FeeScheduleEngine.Models;
using NcciServices = CloudHealthOffice.NcciEngine.Services;

namespace CloudHealthOffice.ClaimsService.Tests.Integration;

/// <summary>
/// #1299 re-verify B1, through the real pipeline (orchestrator, Pricing,
/// BenefitCalculation, NcciEdits, Persistence) driving the PAUNAVAIL bulk
/// re-run. Owner decision Q-15: the re-run denies (CARC 197) only on a
/// definitive PA NotFound / Invalid, never after a failed check. Each case
/// where some check failed on the run must leave the claim pended.
/// </summary>
public sealed class PriorAuthRerunPipelineTests
{
    private const string Tenant = "tenant-1";
    private static readonly Guid PlanGuid = Guid.NewGuid();
    private static readonly DateTime ServiceDate = new(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IAuthorizationValidationClient _authorization = Substitute.For<IAuthorizationValidationClient>();
    private readonly IFeeSchedulePricingClient _pricing = Substitute.For<IFeeSchedulePricingClient>();
    private readonly AdjudicationTenantContext _tenantContext = new();
    private readonly Claim _stored;

    public PriorAuthRerunPipelineTests()
    {
        _stored = new Claim
        {
            Id = "claim-pa", ClaimVersionId = "claim-pa", TenantId = Tenant, ClaimNumber = "CLM-PA",
            MemberId = "MEM-1", SubscriberId = "MEM-1", BenefitPlanId = PlanGuid.ToString(),
            BillingProviderNPI = "1234567893", ClaimType = ClaimType.Institutional,
            LineOfBusiness = LineOfBusiness.Medicaid, PlaceOfServiceCode = "21",
            PriorAuthorizationNumber = "AUTH-1",
            ServiceDateFrom = ServiceDate, ServiceDateTo = ServiceDate, SubmittedDate = ServiceDate,
            Status = ClaimStatus.Pended, VersionState = ClaimVersionState.Submitted, VersionNumber = 1,
            PendDetails = new PendDetails
            {
                PendCode = BenefitCalculationStage.PriorAuthorizationUnavailablePendCode,
                PendReason = BenefitCalculationStage.PriorAuthorizationUnavailableReason + " (line 1: 503)",
            },
            ClaimLines =
            [
                new ClaimLine { LineNumber = 1, ProcedureCode = "99223", ChargeAmount = 100m, Units = 1,
                    ServiceDateFrom = ServiceDate, ServiceDateTo = ServiceDate },
            ],
        };

        _adapter.Platform.Returns("cho");
        _adapter.GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ClaimAdapterResponse { Platform = "cho", Claim = AdapterClaim.From(_stored) });

        _claims.FindPendedByPendCodeAsync(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { _stored });
        _claims.FindPendedByPendCodeAsync(BenefitCalculationStage.PlanUnavailablePendCode, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Claim>());
        _claims.TryAcquireResolutionLockAsync(default!, default!, default!, default!, default, default, default).ReturnsForAnyArgs(true);
        _claims.GetForAccumulatorOutboxAsync(Tenant, _stored.Id, Arg.Any<CancellationToken>()).Returns(_stored);
        _claims.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Claim>());
        _claims.UpdateAdjudicationProjectionAsync(
                default!, default!, default!, default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(true);

        PricingAnswers(priced: true);
    }

    private void PricingAnswers(bool priced)
    {
        var call = _pricing.ResolveBatchAsync(Tenant, Arg.Any<IReadOnlyList<FeeModels.PricingRequest>>(), Arg.Any<CancellationToken>());
        if (!priced)
        {
            call.ThrowsAsync(new HttpRequestException("fee-schedule service down"));
            return;
        }
        call.Returns(new FeeModels.PricingResultSet
        {
            LineResults =
            [
                new FeeModels.PricingResult
                {
                    LineNumber = 1, ProcedureCode = "99223", AllowedAmount = 80m, BilledAmount = 100m,
                    RateSource = CloudHealthOffice.FeeScheduleEngine.Domain.RateSource.ContractedRate,
                },
            ],
        });
    }

    private void PriorAuthAnswers(AuthorizationLookupResult answer) =>
        _authorization.ValidateAsync(default!, default!, default, default, default, "MEM-1", default).ReturnsForAnyArgs(answer);

    private static AuthorizationLookupResult Valid() => AuthorizationLookupResult.Valid(
        new AuthorizationValidationResult("AUTH-1", true, "Approved", null, null, null, 1, "Authorization valid"));

    private static IBenefitCalculationEngine PayingEngine()
    {
        var engine = Substitute.For<IBenefitCalculationEngine>();
        engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult
            {
                Success = true,
                Totals = new ClaimTotals { TotalAllowed = 80m },
                PreparedAccumulatorCommit = new AccumulatorCommit
                {
                    CommitId = "commit-1", ClaimId = "claim-pa", MemberId = "MEM-1", SubscriberId = "MEM-1",
                    BenefitPlanId = PlanGuid, PlanYear = "2026",
                },
            });
        return engine;
    }

    /// <summary>The real HTTP engine client against a benefit-plan-service answering <paramref name="status"/>.</summary>
    private IBenefitCalculationEngine EngineAnswering(HttpStatusCode status) =>
        new HttpBenefitCalculationEngineClient(
            new StaticFactory(new HttpClient(new Respond(status)) { BaseAddress = new Uri("http://benefit-plan-service") }),
            new HttpContextAccessor(), _tenantContext, NullLogger<HttpBenefitCalculationEngineClient>.Instance);

    private static NcciEditsStage Ncci(NcciEnforcementMode mode, bool throws)
    {
        var engine = Substitute.For<NcciServices.INcciEditService>();
        if (throws)
            engine.ScrubAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("NCCI store down"));
        else
            engine.ScrubAsync(default!, default).ReturnsForAnyArgs(new CloudHealthOffice.NcciEngine.Models.NcciScrubResult());
        return new NcciEditsStage(engine,
            Options.Create(new TenantEnforcementPolicyOptions { NcciMode = mode }),
            NullLogger<NcciEditsStage>.Instance);
    }

    private AccumulatorOperationsService Operations(IBenefitCalculationEngine engine, NcciEditsStage ncci)
    {
        var memberResolver = Substitute.For<IMemberResolver>();
        memberResolver.GetMemberAsync(Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true, EnrollmentStatus = "Active",
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        var planResolver = Substitute.For<IBenefitPlanResolver>();
        planResolver.GetPlanAsync(Tenant, PlanGuid.ToString(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedBenefitPlan { Id = PlanGuid.ToString(), PlanGuid = PlanGuid });

        var stages = new IClaimAdjudicationStage[]
        {
            new PricingStage(_pricing, NullLogger<PricingStage>.Instance),
            new BenefitCalculationStage(engine, memberResolver, _authorization, NullLogger<BenefitCalculationStage>.Instance),
            ncci,
            new PersistenceStage(_claims, NullLogger<PersistenceStage>.Instance),
        };
        var factory = new ClaimAdapterFactory(new[] { _adapter },
            new ClaimTenantConfigCache(Substitute.For<IHttpClientFactory>(), Substitute.For<IConfiguration>(),
                NullLogger<ClaimTenantConfigCache>.Instance),
            NullLogger<ClaimAdapterFactory>.Instance);
        var orchestrator = new ClaimAdjudicationOrchestrator(
            factory, planResolver, memberResolver, Substitute.For<ICoverageResolver>(), stages,
            Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(), _tenantContext,
            Substitute.For<IClaimAdjustmentService>(), Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
        return new AccumulatorOperationsService(
            _claims, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IClaimEventPublisher>(),
            NullLogger<AccumulatorOperationsService>.Instance, orchestrator, lookup: NothingRecorded());
    }

    private static IAccumulatorCommitLookup NothingRecorded()
    {
        var lookup = Substitute.For<IAccumulatorCommitLookup>();
        lookup.LookupCommitRecordsAsync(Arg.Any<IReadOnlyList<AccumulatorCommitLookupItem>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, bool>());
        return lookup;
    }

    private async Task AssertStaysPended(AccumulatorOperationsService operations)
    {
        var run = await operations.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(1, run.Examined);
        Assert.Equal(0, run.Denied);
        Assert.Equal(0, run.Approved);
        Assert.Equal(1, run.StillPended);
        // Never finalized: the only write allowed is the re-stamp of a still-Pended claim (S1).
        await _claims.DidNotReceive().UpdateHoldingResolutionLockAsync(
            Arg.Is<Claim>(c => c.Status != ClaimStatus.Pended), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>(a) PA Valid, then benefit-plan-service answers 503 / 429: never finalized Denied(999).</summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task PaValid_BenefitEngineUnavailable_StaysPended(HttpStatusCode status)
    {
        PriorAuthAnswers(Valid());

        await AssertStaysPended(Operations(EngineAnswering(status), Ncci(NcciEnforcementMode.PendForReview, throws: false)));
    }

    /// <summary>(b) Tenant NcciMode = Deny and the NCCI engine throws: a failed check, not a definitive denial.</summary>
    [Fact]
    public async Task PaValid_NcciDenyMode_EngineThrows_StaysPended()
    {
        PriorAuthAnswers(Valid());

        await AssertStaysPended(Operations(PayingEngine(), Ncci(NcciEnforcementMode.Deny, throws: true)));
    }

    /// <summary>(c) PA NotFound while pricing was unavailable on the same run: not finalized.</summary>
    [Fact]
    public async Task PaNotFound_PricingUnavailable_StaysPended()
    {
        PriorAuthAnswers(AuthorizationLookupResult.NotFound());
        PricingAnswers(priced: false);

        await AssertStaysPended(Operations(PayingEngine(), Ncci(NcciEnforcementMode.PendForReview, throws: false)));
    }

    /// <summary>The approved case (Q-15): a definitive NotFound with every other check answering denies, CARC 197.</summary>
    [Fact]
    public async Task PaNotFound_EverythingElseAnswered_IsDenied197()
    {
        PriorAuthAnswers(AuthorizationLookupResult.NotFound());

        var run = await Operations(PayingEngine(), Ncci(NcciEnforcementMode.PendForReview, throws: false))
            .RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(1, run.Denied);
        await _claims.Received(1).UpdateHoldingResolutionLockAsync(
            Arg.Is<Claim>(c => c.Status == ClaimStatus.Deny && c.VersionState == ClaimVersionState.Adjudicated && c.PendingAccumulatorReversal == null),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class Respond(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private sealed class StaticFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
