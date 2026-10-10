using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using ClaimsService.Adapters;
using ClaimsService.EDI.Inbound;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Events;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;
using PaymentService.Repositories;
using PaymentService.Services;
using Claims = ClaimsService.Models;
using Pay = PaymentService.Models;

namespace CloudHealthOffice.GoldenPath.Tests.Harness;

/// <summary>
/// The inputs a scenario stores before its 837 arrives: the benefit plan
/// document, the provider contracts and fee schedules, the tenant's service
/// category mappings, and what the member already accumulated this plan year.
/// </summary>
internal sealed class GoldenScenario
{
    public const string TenantId = "golden-tenant";

    public required string PlanDocument { get; init; }
    public required IReadOnlyList<FeeSchedule> FeeSchedules { get; init; }
    public required IReadOnlyList<ProviderContract> Contracts { get; init; }
    public required IReadOnlyList<ServiceCategoryMapping> CategoryMappings { get; init; }
    public Action<InMemoryAccumulatorService>? PriorAccumulators { get; init; }

    /// <summary>
    /// The member's other coverage as coverage-service reports it (each
    /// entry's CoverageSequence: "P" = sequenced before this plan, "S" / "T"
    /// = after). Empty = this plan is the only coverage. The real
    /// CoordinationOfBenefitsStage checks it against the 837's SBR01.
    /// </summary>
    public IReadOnlyList<string> OtherCoverage { get; init; } = [];

    /// <summary>
    /// When set, a review stage with this name (e.g. "DuplicateClaim") pends
    /// the claim at Order 120 — standing in for a possible-duplicate /
    /// provider-integrity review. The claim must then pend, write no
    /// accumulators, and finalize only through <see cref="Approvals"/>.
    /// </summary>
    public string? PendingReviewStage { get; init; }

    /// <summary>
    /// Further review stages (Order 120+): each pends with its code and
    /// reason on the first run, <see cref="ReviewStage.RerunReason"/> on an
    /// approval re-run (a different finding), or only on the re-run
    /// (<see cref="ReviewStage.OnlyOnRerun"/> — e.g. provider integrity
    /// unreachable when the approval re-runs).
    /// </summary>
    public IReadOnlyList<ReviewStage> ReviewStages { get; init; } = [];

    /// <summary>Every approval is expected to be refused: the claim stays pended.</summary>
    public bool ExpectApprovalsRefused { get; init; }

    /// <summary>
    /// Examiner approvals tried in order on a pended claim (the controller's
    /// resolve flow): each re-adjudicates through
    /// <see cref="ClaimAdjudicationOrchestrator.ReadjudicateForApprovalAsync"/>.
    /// Refused ones must write no accumulators; the last must pass.
    /// </summary>
    public IReadOnlyList<ExaminerApproval> Approvals { get; init; } = [];

    /// <summary>See <see cref="ReviewStages"/>.</summary>
    /// <param name="AppendsToExisting">Adds its reason to an existing pend (as NCCI does) instead of
    /// replacing <c>PendDetails</c> (as the duplicate, provider-integrity and benefit stages do).</param>
    public sealed record ReviewStage(string Name, string Code, string Reason, string? RerunReason = null, bool OnlyOnRerun = false,
        bool AppendsToExisting = false);

    /// <summary>Payment-run date (BPR16); fixed so the 835 is reproducible.</summary>
    public DateTime PaymentDate { get; init; } = new(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);
}

internal sealed record GoldenPathResult(
    Claims.Claim AdjudicatedClaim,
    Claims.Claim FinalizedClaim,
    ClaimFinalizedEvent FinalizedEvent,
    BenefitResolutionResult BenefitResult,
    Pay.PaymentRun PaymentRun,
    string Edi835)
{
    /// <summary>Outcome of each examiner approval attempt, in order (empty when the claim did not pend).</summary>
    public IReadOnlyList<ClaimAdjudicationOutcome> ApprovalOutcomes { get; init; } = [];

    /// <summary>The unresolved reasons of each approval attempt, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ApprovalReasons { get; init; } = [];

    /// <summary>Accumulator updates the engine had written before the approval that passed.</summary>
    public int AccumulatorUpdatesBeforeApproval { get; init; }

    /// <summary>The pend code the claim pended with (null when it did not pend).</summary>
    public string? PendCode { get; init; }

    /// <summary>Every accumulator update the engine wrote, in order.</summary>
    public IReadOnlyList<AccumulatorUpdate> AccumulatorUpdates { get; init; } = [];
}

/// <summary>
/// Runs one synthetic 837 down the money path with the production code at
/// every step:
/// <list type="number">
///   <item>claims-service: <see cref="X12837Parser"/> → <see cref="X12837ClaimMapper"/>
///     → <see cref="ClaimSubmissionService"/> (CHO adapter, Mongo repository)
///     → <see cref="ClaimAdjudicationOrchestrator"/> with <see cref="PricingStage"/>,
///     <see cref="BenefitCalculationStage"/> and <see cref="PersistenceStage"/>.</item>
///   <item>benefit-plan-service, reached over its HTTP contracts
///     (<see cref="HttpFeeSchedulePricingClient"/>, <see cref="HttpBenefitCalculationEngineClient"/>)
///     with its MVC wire format: the real <see cref="RateResolutionService"/> over
///     in-memory contract / fee-schedule stores, and the real
///     <see cref="BenefitCalculationEngine"/> with <see cref="ChoBenefitPlanProvider"/>
///     projecting the stored plan document, <see cref="ServiceCategoryResolver"/>
///     and <see cref="BenefitRuleGate"/>.</item>
///   <item>payment-service: <see cref="PaymentRunService.ExecutePaymentRunAsync"/>
///     fetching the claim from claims-service's search wire, the real
///     <see cref="CarcRarcMappingService"/> and <see cref="BatchEraGeneratorService"/>,
///     and its finalize call answered by the real <see cref="ClaimFinalizationService"/>,
///     whose Kafka publish yields the <see cref="ClaimFinalizedEvent"/>.</item>
/// </list>
/// Stubbed: member / coverage / plan resolution (an active subscriber on the
/// scenario's plan), trading-partner lookup, payment-service persistence, and
/// the stages outside the money path (scrubbing, NCCI, network, COB, AI).
/// </summary>
internal sealed class GoldenPathHarness
{
    private readonly MongoRunnerFixture _mongo;

    public GoldenPathHarness(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task<GoldenPathResult> RunAsync(GoldenScenario scenario, string edi837)
    {
        var tenant = GoldenScenario.TenantId;
        var ct = CancellationToken.None;
        var database = _mongo.CreateDatabase("golden_path");
        try
        {
            var tenantContext = new AdjudicationTenantContext { TenantId = tenant };
            var claimRepository = new ClaimRepositoryMongo(
                database, new HttpContextAccessor(), NullLogger<ClaimRepositoryMongo>.Instance, tenantContext);

            // ── benefit-plan-service ────────────────────────────────────
            var planJson = scenario.PlanDocument;
            var planGuid = Guid.Parse(LoadPlan(planJson).Id);
            var accumulators = new InMemoryAccumulatorService();
            scenario.PriorAccumulators?.Invoke(accumulators);
            BenefitResolutionResult? benefitResult = null;
            var benefitPlanService = BenefitPlanServiceHandler(scenario, planJson, accumulators, r => benefitResult = r);

            var http = new RoutingHttpClientFactory()
                .Route(UpstreamClientNames.BenefitPlanService, benefitPlanService);

            // ── claims-service: submit the 837 ──────────────────────────
            var adapterFactory = new ClaimAdapterFactory(
                new IClaimAdapter[] { new ChoClaimAdapter(claimRepository, NullLogger<ChoClaimAdapter>.Instance) },
                new ClaimTenantConfigCache(http, new ConfigurationBuilder().Build(), NullLogger<ClaimTenantConfigCache>.Instance),
                NullLogger<ClaimAdapterFactory>.Instance);

            var parsed = Assert.Single(X12837Parser.Parse(edi837));
            var inbound = X12837ClaimMapper.Map(parsed, tenant);
            var submission = await new ClaimSubmissionService(
                    adapterFactory, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(),
                    NullLogger<ClaimSubmissionService>.Instance)
                .SubmitAsync(inbound, tenant, "golden-submitter", "golden-correlation", ct);
            Assert.True(submission.Success, string.Join("; ", submission.Errors.Select(e => e.Message)));
            var submitted = submission.Claim!;

            // ── claims-service: adjudicate ──────────────────────────────
            var member = new ResolvedMember
            {
                MemberId = submitted.MemberId,
                SubscriberMemberId = submitted.SubscriberId,
                IsSubscriber = true,
                EnrollmentStatus = "Active",
                EffectiveDate = new DateTime(2026, 1, 1),
                DateOfBirth = new DateTime(1980, 6, 1),
            };
            var memberResolver = Substitute.For<IMemberResolver>();
            memberResolver.GetMemberAsync(tenant, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
            var coverageResolver = Substitute.For<ICoverageResolver>();
            coverageResolver.ResolveBenefitPlanIdAsync(tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(planGuid.ToString());
            var planResolver = Substitute.For<IBenefitPlanResolver>();
            planResolver.GetPlanAsync(tenant, planGuid.ToString(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedBenefitPlan { Id = planGuid.ToString(), PlanGuid = planGuid });

            var coverageClient = Substitute.For<ICoverageClient>();
            coverageClient.GetCobEntriesAsync(tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(scenario.OtherCoverage
                    .Select((seq, i) => new CobEntry { PayerName = $"OTHER{i + 1}", PayerId = $"OTHER{i + 1}", CoverageSequence = seq })
                    .ToList());

            var stageList = new List<IClaimAdjudicationStage>();
            if (scenario.PendingReviewStage is { } reviewStage)
                stageList.Add(new PendingReviewStage(new GoldenScenario.ReviewStage(
                    reviewStage, "DUPLICATE", "Possible duplicate of an earlier claim.")));
            var reviewOrder = 121;
            foreach (var review in scenario.ReviewStages)
                stageList.Add(new PendingReviewStage(review, reviewOrder++));
            var engineClient = new HttpBenefitCalculationEngineClient(http, new HttpContextAccessor(), tenantContext,
                NullLogger<HttpBenefitCalculationEngineClient>.Instance);
            var stages = new IClaimAdjudicationStage[]
            {
                new CoordinationOfBenefitsStage(
                    coverageClient,
                    new CloudHealthOffice.CobEngine.Services.PayerOrderService(),
                    Options.Create(new TenantEnforcementPolicyOptions()),
                    NullLogger<CoordinationOfBenefitsStage>.Instance),
                new PricingStage(
                    new HttpFeeSchedulePricingClient(http, NullLogger<HttpFeeSchedulePricingClient>.Instance),
                    NullLogger<PricingStage>.Instance),
                new BenefitCalculationStage(
                    new HttpBenefitCalculationEngineClient(http, new HttpContextAccessor(), tenantContext,
                        NullLogger<HttpBenefitCalculationEngineClient>.Instance),
                    memberResolver,
                    Substitute.For<IAuthorizationValidationClient>(),
                    NullLogger<BenefitCalculationStage>.Instance),
                new PersistenceStage(claimRepository, NullLogger<PersistenceStage>.Instance),
            };

            stageList.AddRange(stages);
            // The accumulator outbox: a passing claim's commit is written with
            // its Approved status and driven right after the run.
            var outbox = new AccumulatorOutboxProcessor(claimRepository, engineClient, tenantContext,
                Options.Create(new AccumulatorOutboxOptions()), NullLogger<AccumulatorOutboxProcessor>.Instance);
            var orchestrator = new ClaimAdjudicationOrchestrator(
                adapterFactory, planResolver, memberResolver, coverageResolver, stageList,
                Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(), tenantContext,
                Substitute.For<IClaimAdjustmentService>(),
                Options.Create(new AdjudicationPipelineOptions()),
                NullLogger<ClaimAdjudicationOrchestrator>.Instance,
                outbox);

            await orchestrator.AdjudicateAsync(
                new ClaimVersionSubmittedMessage
                {
                    TenantId = tenant,
                    ClaimId = submitted.Id,
                    ClaimVersionId = submitted.ClaimVersionId,
                    VersionNumber = submitted.VersionNumber,
                    ActorId = "golden-submitter",
                    CorrelationId = "golden-correlation",
                },
                new MessageContext("golden-message", "golden-correlation", 1, new Dictionary<string, string>()),
                ct);

            var adjudicated = (await claimRepository.GetByIdAsync(submitted.Id))!;

            // ── examiner resolve flow (pended claims) ───────────────────
            var approvalOutcomes = new List<ClaimAdjudicationOutcome>();
            var approvalReasons = new List<IReadOnlyList<string>>();
            var updatesBeforeApproval = accumulators.Applied.Count;
            string? pendCode = null;
            if (scenario.Approvals.Count > 0)
            {
                Assert.True(adjudicated.Status == Claims.ClaimStatus.Pended,
                    $"expected the claim to pend; it is {adjudicated.Status}");
                pendCode = adjudicated.PendDetails?.PendCode;
                // A pended claim is priced read-only: nothing written.
                Assert.Empty(accumulators.Applied);
                // ClaimsController.ResolvePendedClaim passes the persisted
                // pend — what the examiner reviewed.
                var reviewed = adjudicated.PendDetails;
                ApprovalReadjudicationResult? passed = null;
                foreach (var approval in scenario.Approvals)
                {
                    var rerun = await orchestrator.ReadjudicateForApprovalAsync(
                        tenant, submitted.Id, approval with { ReviewedPend = reviewed }, ct);
                    approvalOutcomes.Add(rerun.Outcome);
                    approvalReasons.Add(rerun.UnresolvedReasons ?? []);
                    // The re-run commits nothing itself (the resolver does, after its final write).
                    Assert.Empty(accumulators.Applied);
                    if (rerun.Outcome == ClaimAdjudicationOutcome.Pass)
                    {
                        passed = rerun;
                        break;
                    }
                    // A refused approval writes no accumulators either.
                    Assert.Empty(accumulators.Applied);
                }
                if (scenario.ExpectApprovalsRefused)
                {
                    Assert.DoesNotContain(ClaimAdjudicationOutcome.Pass, approvalOutcomes);
                    return new GoldenPathResult(adjudicated, null!, null!, null!, null!, string.Empty)
                    {
                        ApprovalOutcomes = approvalOutcomes,
                        ApprovalReasons = approvalReasons,
                        AccumulatorUpdatesBeforeApproval = updatesBeforeApproval,
                        PendCode = pendCode,
                        AccumulatorUpdates = accumulators.Applied.ToList(),
                    };
                }
                Assert.Equal(ClaimAdjudicationOutcome.Pass, approvalOutcomes[^1]);

                // ClaimsController.ResolvePendedClaim: the re-run persisted
                // the result; the examiner's disposition sets Approved.
                var resolved = (await claimRepository.GetByIdAsync(submitted.Id))!;
                resolved.Status = Claims.ClaimStatus.Approved;
                resolved.VersionState = ClaimRepository.MapStatusToVersionState(Claims.ClaimStatus.Approved);
                resolved.AdjudicatedDate = DateTime.UtcNow;
                // …with the commit the re-run prepared in its accumulator
                // outbox, written by that same final write, then driven.
                Assert.NotNull(passed!.PreparedAccumulatorCommit);
                resolved.PendingAccumulatorCommit = Claims.AccumulatorOutboxItem.ForCommit(passed.PreparedAccumulatorCommit!, DateTime.UtcNow);
                await claimRepository.UpdateAsync(resolved);
                Assert.Equal(AccumulatorOutboxResult.Committed, await outbox.ProcessAsync(
                    tenant, submitted.Id, Claims.AccumulatorOutboxKind.Commit, passed.PreparedAccumulatorCommit!.CommitId, ct));
                adjudicated = (await claimRepository.GetByIdAsync(submitted.Id))!;
            }

            Assert.True(adjudicated.Status == Claims.ClaimStatus.Approved,
                $"claim adjudicated {adjudicated.Status}: pend {adjudicated.PendDetails?.PendCode} {adjudicated.PendDetails?.PendReason}; " +
                $"denial {adjudicated.AdjudicationResult?.DenialReasonCode} {adjudicated.AdjudicationResult?.DenialReason}");
            Assert.NotNull(benefitResult);

            // ── payment-service: pay and remit ──────────────────────────
            Claims.Claim? finalizedClaim = null;
            var kafka = Substitute.For<IClaimEventPublisher>();
            kafka.When(k => k.PublishClaimFinalizedAsync(Arg.Any<Claims.Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
                .Do(ci => finalizedClaim = ci.Arg<Claims.Claim>());
            var finalization = new ClaimFinalizationService(
                claimRepository, Substitute.For<IClaimVersionEventPublisher>(), kafka,
                Substitute.For<IClaimAdjustmentService>(), NullLogger<ClaimFinalizationService>.Instance);

            http.Route(ClaimsServiceClient.HttpClientName, ClaimsServiceHandler(claimRepository, finalization, tenant));

            var (run, edi835) = await RunPaymentAsync(scenario, http, tenant);

            Assert.NotNull(finalizedClaim);
            return new GoldenPathResult(
                adjudicated, finalizedClaim!, ClaimEventPublisher.BuildFinalizedEvent(finalizedClaim!, tenant),
                benefitResult!, run, edi835)
            {
                ApprovalOutcomes = approvalOutcomes,
                ApprovalReasons = approvalReasons,
                AccumulatorUpdatesBeforeApproval = updatesBeforeApproval,
                PendCode = pendCode,
                AccumulatorUpdates = accumulators.Applied.ToList(),
            };
        }
        finally
        {
            await _mongo.DropDatabaseAsync(database);
        }
    }

    /// <summary>
    /// A review stage that pends the claim (a possible duplicate, a provider
    /// integrity review, …) — what an examiner resolves by approving. It
    /// records its pend the way the real stages do: a new
    /// <see cref="Claims.PendDetails"/>, or an additional reason when the
    /// claim is already pended.
    /// </summary>
    private sealed class PendingReviewStage(GoldenScenario.ReviewStage review, int order = 120) : IClaimAdjudicationStage
    {
        public string Name => review.Name;
        public int Order => order;
        public bool IsRequired => false;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
        {
            var rerun = context.ExaminerApproval is not null;
            if (review.OnlyOnRerun && !rerun)
                return Task.FromResult(ClaimAdjudicationStageResult.Pass(review.Name));
            var reason = rerun && review.RerunReason is { } changed ? changed : review.Reason;
            if (context.PendDetails is null || !review.AppendsToExisting)
            {
                context.PendDetails = new Claims.PendDetails
                {
                    PendCode = review.Code,
                    PendReason = reason,
                    PendedAt = DateTime.UtcNow,
                };
            }
            else
            {
                context.PendDetails.AdditionalPendReasons.Add($"{review.Code}: {reason}");
            }
            return Task.FromResult(ClaimAdjudicationStageResult.Pend(review.Name, reason));
        }
    }

    /// <summary>The stored plan document, read as benefit-plan-service's store returns it.</summary>
    internal static BenefitPlan LoadPlan(string json)
        => JsonSerializer.Deserialize<BenefitPlan>(json, Wire.BenefitPlanService)!;

    /// <summary>
    /// benefit-plan-service's adjudication endpoints the claims pipeline
    /// calls (resolve-rates, calculate-benefits, commit-accumulators,
    /// reverse-claim), over the real engine and <paramref name="accumulators"/>.
    /// </summary>
    internal static DelegatingServiceHandler BenefitPlanServiceHandler(
        GoldenScenario scenario, string planJson, IAccumulatorService accumulators,
        Action<BenefitResolutionResult> onCalculated)
    {
        var rates = new RateResolutionService(
            new InMemoryFeeScheduleRepository(scenario.FeeSchedules),
            new InMemoryProviderContractRepository(scenario.Contracts),
            NullLogger<RateResolutionService>.Instance);

        var planRepository = Substitute.For<IBenefitPlanRepository>();
        planRepository.GetByIdAsync(Arg.Any<string>(), GoldenScenario.TenantId)
            .Returns(ci => LoadPlan(planJson) is { } p && p.Id == ci.ArgAt<string>(0) ? p : null);

        var planProvider = new ChoBenefitPlanProvider(
            planRepository,
            new FixedTenant(GoldenScenario.TenantId),
            new FixedAcaLimits(),
            new PlanYearResolver(),
            new MemoryCache(Options.Create(new MemoryCacheOptions())),
            NullLogger<ChoBenefitPlanProvider>.Instance);

        var engine = new BenefitCalculationEngine(
            new ServiceCategoryResolver(
                new InMemoryServiceCategoryMappingRepository(scenario.CategoryMappings),
                NullLogger<ServiceCategoryResolver>.Instance),
            planProvider,
            accumulators,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        return new DelegatingServiceHandler(async (request, ct) =>
        {
            var tenant = request.Headers.GetValues("X-Tenant-ID").Single();
            Assert.Equal(GoldenScenario.TenantId, tenant);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/v1/adjudication/resolve-rates":
                {
                    // AdjudicationController.ResolveRates: the tenant comes from the caller.
                    var lines = (await request.Content!.ReadFromJsonAsync<List<PricingRequest>>(Wire.BenefitPlanService, ct))!;
                    var priced = await rates.ResolveBatchAsync(lines.Select(l => l with { TenantId = tenant }).ToList(), ct);
                    return DelegatingServiceHandler.Json(priced, Wire.BenefitPlanService);
                }
                case "/api/v1/adjudication/calculate-benefits":
                {
                    var benefitRequest = (await request.Content!.ReadFromJsonAsync<BenefitResolutionRequest>(Wire.BenefitPlanService, ct))!;
                    var result = await engine.CalculateAsync(benefitRequest, ct);
                    onCalculated(result);
                    return DelegatingServiceHandler.Json(result, Wire.BenefitPlanService);
                }
                case "/api/v1/adjudication/commit-accumulators":
                {
                    // AdjudicationController.CommitAccumulators.
                    var commit = (await request.Content!.ReadFromJsonAsync<AccumulatorCommit>(Wire.BenefitPlanService, ct))!;
                    var result = await engine.CommitAccumulatorsAsync(commit, ct);
                    return DelegatingServiceHandler.Json(
                        new BenefitPlanService.Controllers.AccumulatorCommitResponse { Outcome = result.Outcome, Clamped = result.Clamped },
                        Wire.BenefitPlanService);
                }
                case "/api/v1/adjudication/reverse-claim":
                {
                    // AdjudicationController.ReverseClaim.
                    var reverse = (await request.Content!.ReadFromJsonAsync<BenefitPlanService.Controllers.ReverseClaimRequest>(Wire.BenefitPlanService, ct))!;
                    await engine.ReverseClaimAsync(reverse.MemberId, reverse.SubscriberId ?? string.Empty,
                        reverse.BenefitPlanId, reverse.ServiceDate, reverse.OriginalClaimId, ct);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
    }

    /// <summary>
    /// claims-service's side of payment-service's calls:
    /// <c>GET /api/claims/search</c> (ClaimsController.SearchClaims over the
    /// repository) and <c>POST /api/claims/{id}/remittance</c>
    /// (ClaimsController.ProcessRemittance → ClaimFinalizationService).
    /// </summary>
    private static DelegatingServiceHandler ClaimsServiceHandler(
        IClaimRepository repository, ClaimFinalizationService finalization, string tenant)
        => new(async (request, ct) =>
        {
            Assert.Equal(tenant, request.Headers.GetValues("X-Tenant-ID").Single());
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/claims/search")
            {
                var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
                var status = (Claims.ClaimStatus)int.Parse(query["status"]!);
                var claims = await repository.SearchAsync(null, null, null, null, status, null, 1, 1000);
                return DelegatingServiceHandler.Json(claims.ToList(), Wire.ClaimsService);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/remittance", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path.Split('/')[3]);
                var remittance = (await request.Content!.ReadFromJsonAsync<Claims.RemittanceUpdate>(Wire.ClaimsService, ct))!;
                Assert.True(remittance.PaymentAmount > 0m, "zero-payment remittances take the legacy Denied path, not exercised here");
                var result = await finalization.FinalizeAsync(id, new ClaimFinalizationRequest
                {
                    CheckNumber = remittance.CheckNumber ?? string.Empty,
                    PaymentDate = remittance.PaymentDate,
                    PayerPayment = remittance.PaymentAmount,
                    PaymentRunId = remittance.PaymentRunId,
                    EraEnvelopeId = remittance.EraEnvelopeId,
                    EdiControlNumber = remittance.ControlNumber,
                }, tenant, "payment-service", null, ct);
                return result.Outcome == ClaimFinalizationOutcome.Finalized
                    ? DelegatingServiceHandler.Json(result.Claim!, Wire.ClaimsService)
                    : new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static async Task<(Pay.PaymentRun Run, string Edi835)> RunPaymentAsync(
        GoldenScenario scenario, RoutingHttpClientFactory http, string tenant)
    {
        var run = new Pay.PaymentRun
        {
            Id = "golden-run",
            TenantId = tenant,
            PaymentRunNumber = "PR-GOLDEN-0001",
            Status = Pay.PaymentRunStatus.Pending,
            Criteria = new Pay.PaymentRunCriteria { GroupByProvider = true, IncludeDeniedClaims = true },
            NextCheckNumber = 1000001,
            PaymentDate = scenario.PaymentDate,
            PaymentMethod = "CHK",
            CreatedBy = "golden-maker",
        };

        var runs = Substitute.For<IPaymentRunRepository>();
        runs.GetByIdAsync(run.Id).Returns(run);
        runs.TryStartAsync(run.Id, Arg.Any<string>(), Arg.Any<DateTime>()).Returns(true);
        runs.UpdateAsync(Arg.Any<Pay.PaymentRun>()).Returns(ci => ci.Arg<Pay.PaymentRun>());

        var payments = Substitute.For<IPaymentRepository>();
        payments.CreateAsync(Arg.Any<Pay.Payment>()).Returns(ci => ci.Arg<Pay.Payment>());
        payments.UpdateAsync(Arg.Any<Pay.Payment>()).Returns(ci => ci.Arg<Pay.Payment>());
        payments.GetClaimIdsWithPaymentAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>())
            .Returns(Array.Empty<string>());

        var envelopes = new List<Pay.EraEnvelopeRecord>();
        var envelopeRepository = Substitute.For<IEraEnvelopeRepository>();
        envelopeRepository.CreateAsync(Arg.Any<Pay.EraEnvelopeRecord>()).Returns(ci =>
        {
            var record = ci.Arg<Pay.EraEnvelopeRecord>();
            record.Id = $"env-{envelopes.Count + 1}";
            envelopes.Add(record);
            return record;
        });
        envelopeRepository.GetClaimIdsWithEnvelopeAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>())
            .Returns(Array.Empty<string>());

        var reservations = Substitute.For<IClaimReservationRepository>();
        reservations.TryReserveAsync(Arg.Any<ClaimReservation>()).Returns(true);

        var tradingPartners = Substitute.For<ITradingPartnersClient>();
        tradingPartners.GetByBillingProviderNpiAsync(tenant, Arg.Any<string>(), "Production")
            .Returns(new TradingPartnerSummary
            {
                TradingPartnerId = "TP-GOLDEN",
                X12Config = new X12ConfigDto { SenderId = "CHOGOLDEN", ReceiverId = "PROVIDER01" },
            });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payer:Name"] = "CLOUD HEALTH OFFICE GOLDEN HEALTH PLAN",
                ["Payer:Id"] = "CHOGOLD01",
                ["Era:OriginatingCompanyId"] = "1999999999",
                ["TradingPartners:Environment"] = "Production",
            })
            .Build();

        var actor = new ApprovingUser(tenant);
        var service = new PaymentRunService(
            payments, runs,
            new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance),
            new CarcRarcMappingService(NullLogger<CarcRarcMappingService>.Instance),
            envelopeRepository, tradingPartners, http,
            NullLogger<PaymentRunService>.Instance, configuration, actor,
            new RunSeparationOfDuties(actor, NullLogger<RunSeparationOfDuties>.Instance),
            reservations);

        var executed = await service.ExecutePaymentRunAsync(run.Id);
        Assert.Equal(Pay.PaymentRunStatus.Completed, executed.Status);
        Assert.True(executed.Errors.Count == 0, string.Join("; ", executed.Errors));
        Assert.True(executed.UnbalancedServiceLineClaimIds.Count == 0, string.Join("; ", executed.Warnings));
        var envelope = Assert.Single(envelopes);
        return (executed, envelope.EdiContent);
    }

    private sealed class FixedTenant : IBenefitEngineTenantContext
    {
        public FixedTenant(string tenantId) => TenantId = tenantId;
        public string TenantId { get; }
    }

    /// <summary>2026 ACA individual / family OOP caps (only read for Aggregate plans).</summary>
    private sealed class FixedAcaLimits : IAcaLimitsProvider
    {
        public AcaLimits? GetForPlanYear(int planYear) => new(planYear, 10_150m, 20_300m);
        public IReadOnlyCollection<int> ConfiguredPlanYears => new[] { 2026 };
    }
}
