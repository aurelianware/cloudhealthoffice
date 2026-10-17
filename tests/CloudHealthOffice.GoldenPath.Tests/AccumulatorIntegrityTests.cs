using System.Net;
using System.Web;
using BenefitPlanService.Services;
using ClaimsService.Adapters;
using ClaimsService.Controllers;
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
using CloudHealthOffice.BenefitEngine.Persistence;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.GoldenPath.Tests.Harness;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using CloudHealthOffice.Testing.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;
using StackExchange.Redis;
using Claims = ClaimsService.Models;

namespace CloudHealthOffice.GoldenPath.Tests;

/// <summary>
/// Accumulator integrity of the claims pipeline, end to end on real stores: claims-service
/// (its claim repository, the orchestrator, the real <see cref="ClaimsController.ResolvePendedClaim"/>,
/// the accumulator outbox and its dispatcher) and benefit-plan-service's engine reached over
/// its HTTP contract, on two backends:
/// <see cref="AccumulatorIntegrityMongoTests"/> (the engine's Mongo store) and
/// <see cref="AccumulatorIntegrityRedisTests"/> — the
/// store benefit-plan-service runs, <c>RedisAccumulatorService</c> on a real redis-server,
/// rebuilding from claims-service's own <c>accumulator-totals</c>.
/// <list type="bullet">
///   <item>A claim pended after benefit calculation (NCCI at Order 400) writes nothing while
///     pended; an approval writes it exactly once; a denial leaves nothing.</item>
///   <item>An approval re-run whose lock expires mid-run, the new holder denying: zero.</item>
///   <item>The outbox: the commit (or a denial's reversal) is on the claim with the write that
///     finalizes it; a crash before the commit, or benefit-plan-service down, is re-driven by
///     the dispatcher with backoff — once.</item>
///   <item>A commit clamped at a limit by a concurrent claim raises an adjustment review.</item>
/// </list>
/// Claim: golden 01 (99213, allowed $100), no prior accumulators: the member owes the $100 as
/// deductible, so an applied claim shows deductible 100 / OOP 100.
/// </summary>
public abstract class AccumulatorIntegrityTests : IAsyncLifetime
{
    protected const string Tenant = GoldenScenario.TenantId;
    protected const string Member = "MBR-GOLD-01";
    private const string NcciReason = "NCCI PTP edit: 99213 is bundled into a procedure on this date (modifier not allowed).";

    /// <summary>The golden plan's plan year (service date 2026-04-15).</summary>
    protected const string PlanYear = "2026";

    private IClaimRepository _claims = null!;
    private IAccumulatorService _accumulators = null!;
    private HttpBenefitCalculationEngineClient _engine = null!;
    private ClaimAdjudicationOrchestrator _orchestrator = null!;
    private AccumulatorOutboxProcessor _outbox = null!;
    private AccumulatorOutboxDispatcher _dispatcher = null!;
    // The outbox entries are stamped with the wall clock when they are written; the
    // processor's clock starts just after it (whole seconds: the stores keep ms).
    private readonly ManualClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2));
    protected Guid PlanGuid { get; private set; }
    private readonly List<BenefitResolutionResult> _calculated = new();

    /// <summary>Runs once, inside the next approval re-run, between COB (275) and benefit calculation (300).</summary>
    private Func<Task>? _midRerun;

    /// <summary>Runs once, inside the next approval re-run, after benefit calculation (Order 350).</summary>
    private Func<Task>? _afterPricing;

    /// <summary>The NCCI stand-in pends (default) or passes.</summary>
    private bool _ncciPends = true;

    /// <summary>benefit-plan-service answers 503 to commit-accumulators and reverse-claim.</summary>
    private bool _accumulatorWritesDown;

    /// <summary>benefit-plan-service is older than claims-service: its pricing prepares no commit.</summary>
    private bool _noPreparedCommit;

    /// <summary>Runs once, right after the next commit-accumulators answer and before the caller sees it.</summary>
    private Func<Task>? _afterCommitAnswer;

    /// <summary>Procedure codes the golden plan flags as requiring prior authorization (G-25); none by default.</summary>
    private IReadOnlySet<string> _priorAuthCodes = new HashSet<string>();

    /// <summary>authorization-service as the claims pipeline reaches it (D-04).</summary>
    private readonly IAuthorizationValidationClient _authorization = Substitute.For<IAuthorizationValidationClient>();

    /// <summary>Applied to the mapped 837 before it is submitted.</summary>
    private Action<Claims.AdapterClaim>? _beforeSubmit;

    private ClaimFinalizationService _finalization = null!;
    private AccumulatorOperationsService _operations = null!;

    /// <summary>The backend: the claim repository and the engine's accumulator store.</summary>
    protected abstract Task<(IClaimRepository Claims, IAccumulatorService Accumulators)> CreateStoresAsync(
        AdjudicationTenantContext tenantContext);

    /// <summary>Moves the claim's resolution lock into the past, in the backend's own store.</summary>
    protected abstract Task ExpireResolutionLockAsync(string claimId);

    /// <summary>The store holds no write for <paramref name="claimId"/> (balances are checked separately).</summary>
    protected abstract Task AssertNoWriteForAsync(string claimId);

    /// <summary>The store holds exactly one commit of <paramref name="claimId"/>.</summary>
    protected abstract Task AssertCommittedOnceAsync(string claimId);

    /// <summary>The store fences <paramref name="claimId"/> (reversed terminally).</summary>
    protected abstract Task AssertFencedAsync(string claimId);

    /// <summary>The store does not fence <paramref name="claimId"/> (a later commit of it is accepted).</summary>
    protected abstract Task AssertNotFencedAsync(string claimId);

    /// <summary>
    /// A write made before deferred commits (a direct Production apply) can be undone by a
    /// later commit. True for the journalled Mongo / Cosmos store; the Redis store kept no
    /// journal for those writes (documented: counted twice in the cache until it is rebuilt).
    /// </summary>
    protected virtual bool UndoesLegacyWrites => true;

    public abstract Task DisposeAsync();

    protected IAccumulatorService Accumulators => _accumulators;

    public async Task InitializeAsync()
    {
        var tenantContext = new AdjudicationTenantContext { TenantId = Tenant };
        (_claims, _accumulators) = await CreateStoresAsync(tenantContext);

        var scenario = GoldenInputs.Scenario(null);
        PlanGuid = Guid.Parse(GoldenPathHarness.LoadPlan(scenario.PlanDocument).Id);
        var benefitPlanService = new HttpMessageInvoker(
            GoldenPathHarness.BenefitPlanServiceHandler(scenario, scenario.PlanDocument, _accumulators, r => _calculated.Add(r)));
        var http = new RoutingHttpClientFactory()
            .Route(UpstreamClientNames.BenefitPlanService, new DelegatingServiceHandler(async (request, ct) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (_accumulatorWritesDown && path is "/api/v1/adjudication/commit-accumulators" or "/api/v1/adjudication/reverse-claim")
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var response = await benefitPlanService.SendAsync(request, ct);
                if (_noPreparedCommit && path == "/api/v1/adjudication/calculate-benefits")
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
                    foreach (var key in json.Select(kv => kv.Key).Where(k => k.Equals("preparedAccumulatorCommit", StringComparison.OrdinalIgnoreCase)).ToList())
                        json.Remove(key);
                    response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                    };
                }
                if (path == "/api/v1/adjudication/commit-accumulators" && _afterCommitAnswer is { } hook)
                {
                    _afterCommitAnswer = null;
                    await response.Content.LoadIntoBufferAsync();
                    await hook();
                }
                return response;
            }));
        _engine = new HttpBenefitCalculationEngineClient(http, new HttpContextAccessor(), tenantContext,
            NullLogger<HttpBenefitCalculationEngineClient>.Instance);

        var adapterFactory = new ClaimAdapterFactory(
            new IClaimAdapter[] { new ChoClaimAdapter(_claims, NullLogger<ChoClaimAdapter>.Instance) },
            new ClaimTenantConfigCache(http, new ConfigurationBuilder().Build(), NullLogger<ClaimTenantConfigCache>.Instance),
            NullLogger<ClaimAdapterFactory>.Instance);

        var member = new ResolvedMember
        {
            MemberId = Member,
            IsSubscriber = true,
            EnrollmentStatus = "Active",
            EffectiveDate = new DateTime(2026, 1, 1),
            DateOfBirth = new DateTime(1980, 6, 1),
        };
        var memberResolver = Substitute.For<IMemberResolver>();
        memberResolver.GetMemberAsync(Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        var coverageResolver = Substitute.For<ICoverageResolver>();
        coverageResolver.ResolveBenefitPlanIdAsync(Tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(PlanGuid.ToString());
        var planResolver = Substitute.For<IBenefitPlanResolver>();
        planResolver.GetPlanAsync(Tenant, PlanGuid.ToString(), Arg.Any<CancellationToken>())
            .Returns(_ => new ResolvedBenefitPlan
            {
                Id = PlanGuid.ToString(), PlanGuid = PlanGuid, PriorAuthRequiredProcedureCodes = _priorAuthCodes,
            });
        var coverageClient = Substitute.For<ICoverageClient>();
        coverageClient.GetCobEntriesAsync(Tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<CobEntry>());

        var outboxOptions = Options.Create(new AccumulatorOutboxOptions());
        _outbox = new AccumulatorOutboxProcessor(_claims, _engine, tenantContext, outboxOptions,
            NullLogger<AccumulatorOutboxProcessor>.Instance, _clock);
        var services = new ServiceCollection();
        services.AddScoped(_ => _claims);
        services.AddScoped<IAccumulatorOutboxProcessor>(_ => _outbox);
        _dispatcher = new AccumulatorOutboxDispatcher(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), outboxOptions,
            NullLogger<AccumulatorOutboxDispatcher>.Instance, _clock);

        var stages = new IClaimAdjudicationStage[]
        {
            new CoordinationOfBenefitsStage(
                coverageClient, new CloudHealthOffice.CobEngine.Services.PayerOrderService(),
                Options.Create(new TenantEnforcementPolicyOptions()), NullLogger<CoordinationOfBenefitsStage>.Instance),
            new HookStage("MidRerunStall", 280, () => { var hook = _midRerun; _midRerun = null; return hook; }),
            new PricingStage(
                new HttpFeeSchedulePricingClient(http, NullLogger<HttpFeeSchedulePricingClient>.Instance),
                NullLogger<PricingStage>.Instance),
            new BenefitCalculationStage(_engine, memberResolver, _authorization,
                NullLogger<BenefitCalculationStage>.Instance,
                Options.Create(new PriorAuthAdjudicationOptions { UsePlanFlaggedCodes = true })),
            new HookStage("AfterPricing", 350, () => { var hook = _afterPricing; _afterPricing = null; return hook; }),
            new NcciPendStage(() => _ncciPends),
            new PersistenceStage(_claims, NullLogger<PersistenceStage>.Instance),
        };
        _orchestrator = new ClaimAdjudicationOrchestrator(
            adapterFactory, planResolver, memberResolver, coverageResolver, stages,
            Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(), tenantContext,
            Substitute.For<IClaimAdjustmentService>(), Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance, _outbox);
        _submit = adapterFactory;
        _finalization = new ClaimFinalizationService(_claims, Substitute.For<IClaimVersionEventPublisher>(),
            Substitute.For<IClaimEventPublisher>(), Substitute.For<IClaimAdjustmentService>(),
            NullLogger<ClaimFinalizationService>.Instance, _engine, _outbox);
        _operations = new AccumulatorOperationsService(_claims, Substitute.For<IClaimVersionEventPublisher>(),
            Substitute.For<IClaimEventPublisher>(), NullLogger<AccumulatorOperationsService>.Instance,
            _orchestrator, _outbox, _engine);
    }

    private ClaimAdapterFactory _submit = null!;

    // ── the pipeline and the resolver ─────────────────────────────────────

    /// <summary>Submits golden 01 and runs the pipeline on it (the NCCI stand-in pends it at Order 400 unless told not to).</summary>
    private async Task<Claims.Claim> SubmitAndAdjudicateAsync()
    {
        var parsed = Assert.Single(X12837Parser.Parse(GoldenInputs.Edi837("01-office-visit")));
        var mapped = X12837ClaimMapper.Map(parsed, Tenant);
        _beforeSubmit?.Invoke(mapped);
        var submission = await new ClaimSubmissionService(
                _submit, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(),
                NullLogger<ClaimSubmissionService>.Instance)
            .SubmitAsync(mapped, Tenant, "submitter", "corr", CancellationToken.None);
        Assert.True(submission.Success);
        var submitted = submission.Claim!;
        await _orchestrator.AdjudicateAsync(
            new ClaimVersionSubmittedMessage
            {
                TenantId = Tenant, ClaimId = submitted.Id, ClaimVersionId = submitted.ClaimVersionId,
                VersionNumber = submitted.VersionNumber, ActorId = "submitter", CorrelationId = "corr",
            },
            new MessageContext("m1", "corr", 1, new Dictionary<string, string>()),
            CancellationToken.None);
        return (await Claim(submitted.Id))!;
    }

    private Task<Claims.Claim?> Claim(string id) => _claims.GetForAccumulatorOutboxAsync(Tenant, id);

    private ClaimsController Controller(string examiner, bool withOutbox = true)
    {
        var actor = Substitute.For<ICurrentActor>();
        actor.UserId.Returns(examiner);
        actor.TenantId.Returns(Tenant);
        actor.IsAuthenticated.Returns(true);
        actor.HasPermission(Arg.Any<string>()).Returns(true);
        var controller = new ClaimsController(
            _claims,
            Substitute.For<IMassAdjudicationRunRepository>(),
            Substitute.For<IAiExaminationAuditRepository>(),
            Substitute.For<IClaimAcknowledgmentService>(),
            Substitute.For<IClaimEventPublisher>(),
            Substitute.For<IClaimVersionEventPublisher>(),
            Substitute.For<IClaimVersionEventReader>(),
            Substitute.For<IClaimSubmissionService>(),
            Substitute.For<IClaimFinalizationService>(),
            Substitute.For<IClaimDiagnosisMetadataEnricher>(),
            new ConfigurationBuilder().Build(),
            actor,
            NullLogger<ClaimsController>.Instance,
            _orchestrator,
            _engine,
            withOutbox ? _outbox : null,
            _engine);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary><c>POST /api/claims/work-queue/{id}/resolve</c> through the real controller.</summary>
    /// <param name="withOutbox">False: the request dies right after its final write (no immediate attempt).</param>
    private async Task<IActionResult> ResolveAsync(string examiner, string claimId, string disposition, bool withOutbox = true)
    {
        var claim = (await Claim(claimId))!;
        return await Controller(examiner, withOutbox).ResolvePendedClaim(claimId, new ResolvePendedClaimRequest
        {
            Disposition = disposition,
            Reason = $"{examiner} reviewed the NCCI edit",
            PendFingerprint = Claims.PendDetails.ComputeFingerprint(claim.PendDetails),
        });
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private async Task<decimal> Balance(AccumulatorType type, AccumulatorScope scope = AccumulatorScope.Individual)
    {
        var snapshots = await _accumulators.GetAccumulatorsAsync(Member, Member, PlanGuid, PlanYear);
        return snapshots.Where(s => s.Type == type && s.Scope == scope).Sum(s => s.AccumulatedAmountAfter);
    }

    private async Task AssertBalances(decimal deductible, decimal oop)
    {
        Assert.Equal(deductible, await Balance(AccumulatorType.IndividualDeductible));
        Assert.Equal(oop, await Balance(AccumulatorType.IndividualOutOfPocketMax));
    }

    // ── no write while pended ─────────────────────────────────────────────

    /// <summary>
    /// NCCI pends the claim at Order 400, after benefit calculation priced it at 300. Nothing
    /// is written until the examiner approves; the approval writes it once ($100 deductible,
    /// $100 OOP) through the outbox, and repeating the commit, or redelivering the submission,
    /// changes nothing.
    /// </summary>
    [SkippableFact]
    public async Task NcciPendedClaim_WritesNothingUntilApproved_ThenExactlyOnce()
    {
        var pended = await SubmitAndAdjudicateAsync();

        Assert.Equal(Claims.ClaimStatus.Pended, pended.Status);
        Assert.Equal("NCCI", pended.PendDetails!.PendCode);
        Assert.Null(pended.PendingAccumulatorCommit);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);
        Assert.Equal(PlanYear, _calculated[0].PreparedAccumulatorCommit!.PlanYear);
        Assert.Equal(100m, _calculated[0].PreparedAccumulatorCommit!.Updates
            .Where(u => u.Type == AccumulatorType.IndividualDeductible).Sum(u => u.Amount));

        var approved = await ResolveAsync("examiner-1", pended.Id, "Approved");

        Assert.Equal(200, StatusOf(approved));
        var after = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Approved, after.Status);
        Assert.Null(after.PendingAccumulatorCommit); // driven at once and cleared
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(pended.Id);

        // Exactly once: the same commit again, the dispatcher, and a redelivered submission write nothing.
        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted,
            (await _engine.CommitAccumulatorsAsync(_calculated[^1].PreparedAccumulatorCommit!)).Outcome);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await _orchestrator.AdjudicateAsync(
            new ClaimVersionSubmittedMessage { TenantId = Tenant, ClaimId = pended.Id, ClaimVersionId = pended.ClaimVersionId },
            new MessageContext("m1", "corr", 2, new Dictionary<string, string>()), CancellationToken.None);
        await AssertBalances(100m, 100m);
    }

    /// <summary>
    /// The pended claim is denied: nothing was applied, so no reversal is queued at all, and
    /// the claim id is not fenced — Deny is reprocessable until the payment cycle, and a later
    /// approval commits it exactly once.
    /// </summary>
    [SkippableFact]
    public async Task NcciPendedClaim_Denied_QueuesNothing_ThenReprocessedToPay_CommitsOnce()
    {
        var pended = await SubmitAndAdjudicateAsync();

        var denied = await ResolveAsync("examiner-1", pended.Id, "Denied");

        Assert.Equal(200, StatusOf(denied));
        var after = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Deny, after.Status);
        Assert.Null(after.PendingAccumulatorReversal);
        Assert.Null(after.PendingAccumulatorCommit);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);
        await AssertNotFencedAsync(pended.Id);

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-2", pended.Id, "Approved")));
        Assert.Equal(Claims.ClaimStatus.Approved, (await Claim(pended.Id))!.Status);
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(pended.Id);
    }

    // ── Pay → Deny → Pay: the non-terminal reversal ───────────────────────

    /// <summary>
    /// A Pay claim whose commit was applied is denied before the payment cycle: its
    /// contribution is reversed now (balances back to zero) without a fence. Reprocessed back
    /// to Pay, its new commit (a new commit id) is accepted — no refusal, no clamp review —
    /// and counted once.
    /// </summary>
    [SkippableFact]
    public async Task PayCommitted_Deny_ReversesNow_ThenPayAgain_CommitsOnce()
    {
        _ncciPends = false;
        var paid = await SubmitAndAdjudicateAsync();
        Assert.Equal(Claims.ClaimStatus.Approved, paid.Status);
        await AssertBalances(100m, 100m);
        var firstCommitId = _calculated[^1].PreparedAccumulatorCommit!.CommitId;

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", paid.Id, "Denied")));

        var denied = (await Claim(paid.Id))!;
        Assert.Equal(Claims.ClaimStatus.Deny, denied.Status);
        Assert.Null(denied.PendingAccumulatorReversal); // driven at once and cleared
        await AssertBalances(0m, 0m);
        await AssertNotFencedAsync(paid.Id);

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-2", paid.Id, "Approved")));

        var repaid = (await Claim(paid.Id))!;
        Assert.Equal(Claims.ClaimStatus.Approved, repaid.Status);
        Assert.Null(repaid.PendingAccumulatorCommit);
        Assert.Null(repaid.AccumulatorClampReview);
        Assert.NotEqual(firstCommitId, _calculated[^1].PreparedAccumulatorCommit!.CommitId);
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(paid.Id);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await AssertBalances(100m, 100m);
    }

    // ── commit-scoped reversals: deterministic interleavings ──────────────

    /// <summary>
    /// Pay (commit X applied) → Deny, whose reversal of X is saved but not yet driven → back
    /// to Pay (commit Y). The re-Pay write cancels the queued reversal; the copy already in
    /// flight, dispatched after Y landed, is a no-op (Superseded): Y stays counted once.
    /// </summary>
    [SkippableFact]
    public async Task Interleaving_DenyReversalDispatchedAfterTheRePayCommit_IsANoOp()
    {
        _ncciPends = false;
        var paid = await SubmitAndAdjudicateAsync();
        var commitX = _calculated[^1].PreparedAccumulatorCommit!.CommitId;
        Assert.Equal(commitX, (await Claim(paid.Id))!.LatestAccumulatorCommitId);
        await AssertBalances(100m, 100m);

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", paid.Id, "Denied", withOutbox: false)));
        var inFlight = (await Claim(paid.Id))!.PendingAccumulatorReversal!.Reversal!;
        Assert.False(inFlight.Terminal);
        Assert.Equal(commitX, inFlight.CommitId);
        await AssertBalances(100m, 100m); // not driven yet

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-2", paid.Id, "Approved")));
        var repaid = (await Claim(paid.Id))!;
        Assert.Null(repaid.PendingAccumulatorReversal); // cancelled with the Pay write
        var commitY = repaid.LatestAccumulatorCommitId;
        Assert.NotEqual(commitX, commitY);
        await AssertBalances(100m, 100m);

        var outcome = await _engine.ReverseClaimForReprocessingAsync(inFlight.MemberId, inFlight.SubscriberId,
            Guid.Parse(inFlight.BenefitPlanId), DateOnly.FromDateTime(inFlight.ServiceDate), inFlight.ClaimId, inFlight.CommitId);

        Assert.Equal(AccumulatorReversalOutcome.Superseded, outcome);
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(paid.Id);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
    }

    /// <summary>
    /// Pay with commit X still queued (benefit-plan-service down) → Deny. The Deny write
    /// cancels the queued commit and queues the reversal of X; the reversal lands first and
    /// fences X. A copy of commit X still in flight, arriving afterwards, is refused and counts
    /// nothing; the claim can still go back to Pay with a new commit.
    /// </summary>
    [SkippableFact]
    public async Task Interleaving_PayCommitDispatchedAfterTheDeny_IsDropped()
    {
        _ncciPends = false;
        _accumulatorWritesDown = true;
        var paid = await SubmitAndAdjudicateAsync();
        var commitX = _calculated[^1].PreparedAccumulatorCommit!;
        Assert.Equal(commitX.CommitId, (await Claim(paid.Id))!.PendingAccumulatorCommit!.Id);
        _accumulatorWritesDown = false;

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", paid.Id, "Denied")));
        var denied = (await Claim(paid.Id))!;
        Assert.Equal(Claims.ClaimStatus.Deny, denied.Status);
        Assert.Null(denied.PendingAccumulatorCommit);
        Assert.Null(denied.PendingAccumulatorReversal); // driven: X fenced, nothing to take back

        var late = await _engine.CommitAccumulatorsAsync(commitX);

        Assert.Equal(AccumulatorCommitOutcome.RefusedCommitReversed, late.Outcome);
        await AssertBalances(0m, 0m);
        await AssertNotFencedAsync(paid.Id);

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-2", paid.Id, "Approved")));
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(paid.Id);
    }

    /// <summary>
    /// Legacy sentinel: a claim whose commit is unknown (written before commits were
    /// recorded on the claim) is denied — the reversal names no commit and is blind, as
    /// before: it takes back what the claim holds and fences nothing.
    /// </summary>
    [SkippableFact]
    public async Task Interleaving_LegacyUnknownCommit_DenyReversesBlind()
    {
        Skip.If(!UndoesLegacyWrites,
            "The Redis store kept no journal for writes made before deferred commits: its lookup cannot report them.");
        var pended = await SubmitAndAdjudicateAsync();
        var legacy = _calculated[0].PreparedAccumulatorCommit!;
        await AssertBalances(0m, 0m);
        await _accumulators.ApplyUpdatesAsync(legacy.MemberId, legacy.SubscriberId, legacy.BenefitPlanId, legacy.PlanYear,
            pended.Id, legacy.Updates);
        await AssertBalances(100m, 100m);

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", pended.Id, "Denied", withOutbox: false)));
        var order = (await Claim(pended.Id))!.PendingAccumulatorReversal!.Reversal!;
        Assert.Equal(Claims.AccumulatorReversalOrder.UnknownCommit, order.CommitId);
        Assert.Null(order.EngineCommitId);

        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await AssertBalances(0m, 0m);
        await AssertNotFencedAsync(pended.Id);
    }

    /// <summary>
    /// Deny → Denied: the payment run's $0 remittance finalizes the denial and does nothing
    /// to the accumulators — the reversal already happened when the claim was denied.
    /// </summary>
    [SkippableFact]
    public async Task PayCommitted_Deny_ThenFinalizedDenied_StaysReversed()
    {
        _ncciPends = false;
        var paid = await SubmitAndAdjudicateAsync();
        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", paid.Id, "Denied")));
        await AssertBalances(0m, 0m);

        var finalized = await _finalization.FinalizeAsync(paid.Id,
            new ClaimFinalizationRequest { CheckNumber = "PR-1-D1", PayerPayment = 0m }, Tenant, "payment-service", null);

        Assert.Equal(ClaimFinalizationOutcome.Finalized, finalized.Outcome);
        var stored = (await Claim(paid.Id))!;
        Assert.Equal(Claims.ClaimStatus.Denied, stored.Status);
        Assert.Equal(Claims.ClaimVersionState.Denied, stored.VersionState);
        Assert.Null(stored.PendingAccumulatorReversal);
        Assert.Null(stored.PendingAccumulatorCommit);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await AssertBalances(0m, 0m);
    }

    /// <summary>
    /// Pay → Deny → Pay, then paid and voided: the void's terminal reversal takes back the
    /// second commit and fences the claim id, exactly as for a claim never denied.
    /// </summary>
    [SkippableFact]
    public async Task DenyThenPay_ThenPaidAndVoided_ReversesTerminally()
    {
        _ncciPends = false;
        var paid = await SubmitAndAdjudicateAsync();
        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", paid.Id, "Denied")));
        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-2", paid.Id, "Approved")));
        await AssertBalances(100m, 100m);
        Assert.Equal(ClaimFinalizationOutcome.Finalized, (await _finalization.FinalizeAsync(paid.Id,
            new ClaimFinalizationRequest { CheckNumber = "CHK-2", PayerPayment = 0m }, Tenant, "payment-service", null)).Outcome);

        var voided = await _finalization.VoidAsync(paid.Id, new ClaimVoidRequest { Reason = "duplicate" }, Tenant, "operator", null);

        Assert.Equal(Claims.ClaimStatus.Voided, voided.Claim!.Status);
        Assert.Null((await Claim(paid.Id))!.PendingAccumulatorReversal);
        await AssertBalances(0m, 0m);
        await AssertFencedAsync(paid.Id);
    }

    /// <summary>
    /// A claim pended before this change had written its accumulators at benefit calculation.
    /// Approving it replaces that write (deductible 100, not 200); denying it backs it out.
    /// </summary>
    [SkippableTheory]
    [InlineData("Approved", 100)]
    [InlineData("Denied", 0)]
    public async Task ClaimPendedBeforeTheChange_WithAccumulatorsWritten_IsReplacedOrReversed(string disposition, int expected)
    {
        Skip.If(!UndoesLegacyWrites,
            "The Redis store kept no journal for writes made before deferred commits (deploy notes): its lookup " +
            "cannot report them, so a denial queues no reversal; the cache drops them on its next rebuild " +
            "(claims-service's accumulator totals exclude Deny / Denied).");
        var pended = await SubmitAndAdjudicateAsync();
        var legacy = _calculated[0].PreparedAccumulatorCommit!;
        await AssertBalances(0m, 0m); // warm the cache, as pricing does before a Production write
        await _accumulators.ApplyUpdatesAsync(legacy.MemberId, legacy.SubscriberId, legacy.BenefitPlanId, legacy.PlanYear,
            pended.Id, legacy.Updates);
        await AssertBalances(100m, 100m);

        var result = await ResolveAsync("examiner-1", pended.Id, disposition);

        Assert.Equal(200, StatusOf(result));
        await AssertBalances(expected, expected);
    }

    // ── lock expiry mid-run ───────────────────────────────────────────────

    /// <summary>
    /// Examiner A approves. A's re-run stalls before benefit calculation for longer than its
    /// resolution lock; examiner B takes the lock and denies the claim; then A's re-run
    /// resumes. Its fenced persistence is refused (409), no commit is ever written to the
    /// claim, and B's denial fenced the claim id: zero.
    /// </summary>
    [SkippableFact]
    public async Task ApprovalRerun_LockExpiresMidRun_NewHolderDenies_LeavesNoAccumulators()
    {
        var pended = await SubmitAndAdjudicateAsync();
        IActionResult? bDenied = null;
        _midRerun = async () =>
        {
            await ExpireResolutionLockAsync(pended.Id);
            bDenied = await ResolveAsync("examiner-B", pended.Id, "Denied");
        };

        var aApproved = await ResolveAsync("examiner-A", pended.Id, "Approved");

        Assert.NotNull(bDenied);
        Assert.Equal(200, StatusOf(bDenied!));
        Assert.Equal(409, StatusOf(aApproved));
        var final = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Deny, final.Status);
        Assert.Equal("Deny", Assert.Single(final.ExaminerResolutions).Disposition);
        Assert.Null(final.PendingAccumulatorCommit);
        Assert.Null(final.PendingAccumulatorReversal); // a pended claim contributed nothing
        await AssertBalances(0m, 0m);
        Assert.Equal(0m, await Balance(AccumulatorType.FamilyDeductible, AccumulatorScope.Family));
        // A's prepared write never reached the claim (its final write was refused), so
        // nothing can drive it; Deny is reprocessable, so the claim id is not fenced.
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await AssertBalances(0m, 0m);
        await AssertNotFencedAsync(pended.Id);
    }

    /// <summary>
    /// The store fence on its own: a commit prepared before a denial and arriving after it is
    /// refused inside the store's write; a commit that lands first is reversed by the denial.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommitAndDenial_InEitherOrder_LeaveNothing(bool commitFirst)
    {
        var pended = await SubmitAndAdjudicateAsync();
        var commit = _calculated[0].PreparedAccumulatorCommit!;
        await AssertBalances(0m, 0m);

        if (commitFirst)
            Assert.Equal(AccumulatorCommitOutcome.Committed, (await _engine.CommitAccumulatorsAsync(commit)).Outcome);
        await _engine.ReverseClaimAsync(commit.MemberId, commit.SubscriberId, commit.BenefitPlanId,
            DateOnly.FromDateTime(pended.ServiceDateFrom), pended.Id);
        if (!commitFirst)
            Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, (await _engine.CommitAccumulatorsAsync(commit)).Outcome);

        await AssertBalances(0m, 0m);
        Assert.Equal(0m, await Balance(AccumulatorType.FamilyDeductible, AccumulatorScope.Family));
    }

    // ── the outbox ────────────────────────────────────────────────────────

    /// <summary>A clean claim passes: its commit is written with its Approved status and driven once.</summary>
    [SkippableFact]
    public async Task CleanClaim_CommitsOnceThroughTheOutbox()
    {
        _ncciPends = false;

        var approved = await SubmitAndAdjudicateAsync();

        Assert.Equal(Claims.ClaimStatus.Approved, approved.Status);
        Assert.Null(approved.PendingAccumulatorCommit);
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(approved.Id);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await AssertBalances(100m, 100m);
    }

    /// <summary>
    /// The resolver dies right after its final write (no immediate attempt). The claim is
    /// Approved and carries the commit; the dispatcher commits it, once.
    /// </summary>
    [SkippableFact]
    public async Task Approval_CrashAfterTheFinalWrite_TheDispatcherCommitsOnce()
    {
        var pended = await SubmitAndAdjudicateAsync();

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", pended.Id, "Approved", withOutbox: false)));

        var waiting = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Approved, waiting.Status);
        Assert.NotNull(waiting.PendingAccumulatorCommit);
        await AssertBalances(0m, 0m);
        // A new entry is left to its writer for the inline grace (2 min).
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        _clock.Advance(Claims.AccumulatorOutboxItem.InlineGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));
        Assert.Null((await Claim(pended.Id))!.PendingAccumulatorCommit);
        await AssertBalances(100m, 100m);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(pended.Id);
    }

    /// <summary>
    /// benefit-plan-service refuses the commit (503) when the claim passes, and on the first
    /// retry: the entry stays on the claim with its attempts and backoff (30 s, then 60 s), the
    /// dispatcher does not retry before it is due, and once the service is back the commit
    /// lands — once.
    /// </summary>
    [SkippableFact]
    public async Task CommitWhileBenefitPlanServiceIsDown_IsRetriedWithBackoff_ThenLandsOnce()
    {
        _ncciPends = false;
        _accumulatorWritesDown = true;

        var approved = await SubmitAndAdjudicateAsync();

        Assert.Equal(Claims.ClaimStatus.Approved, approved.Status);
        var entry = approved.PendingAccumulatorCommit!;
        Assert.Equal(1, entry.Attempts);
        Assert.Equal("HttpRequestException 503", entry.LastError);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddSeconds(30), entry.NextAttemptAt);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None)); // not due yet
        await AssertBalances(0m, 0m);

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None)); // still down
        entry = (await Claim(approved.Id))!.PendingAccumulatorCommit!;
        Assert.Equal(2, entry.Attempts);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddSeconds(60), entry.NextAttemptAt);

        _accumulatorWritesDown = false;
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));
        Assert.Null((await Claim(approved.Id))!.PendingAccumulatorCommit);
        await AssertBalances(100m, 100m);
        Assert.Equal(0, await _dispatcher.RunOnceAsync(CancellationToken.None));
        await AssertBalances(100m, 100m);
    }

    /// <summary>
    /// A denial's reversal fails (benefit-plan-service down): it stays on the denied claim
    /// and the dispatcher re-drives it — a Denied claim can never be voided, so nothing else
    /// would. The pre-deploy write is backed out and the claim id fenced.
    /// </summary>
    [SkippableFact]
    public async Task DenialReversal_FailsThenIsRedrivenByTheDispatcher()
    {
        Skip.If(!UndoesLegacyWrites,
            "The Redis store kept no journal for writes made before deferred commits: its lookup cannot report them.");
        var pended = await SubmitAndAdjudicateAsync();
        var legacy = _calculated[0].PreparedAccumulatorCommit!;
        await AssertBalances(0m, 0m);
        await _accumulators.ApplyUpdatesAsync(legacy.MemberId, legacy.SubscriberId, legacy.BenefitPlanId, legacy.PlanYear,
            pended.Id, legacy.Updates);
        _accumulatorWritesDown = true;

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", pended.Id, "Denied")));

        var denied = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Deny, denied.Status);
        Assert.NotNull(denied.PendingAccumulatorReversal);
        await AssertBalances(100m, 100m);

        _accumulatorWritesDown = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));
        Assert.Null((await Claim(pended.Id))!.PendingAccumulatorReversal);
        await AssertBalances(0m, 0m);
        // Non-terminal: Deny stays reprocessable.
        await AssertNotFencedAsync(pended.Id);
    }

    // ── clamp at write time ───────────────────────────────────────────────

    /// <summary>
    /// A concurrent claim for the same member commits deductible 450 and OOP 2,950 after this
    /// claim's approval re-run priced it (deductible 100, OOP 100 against empty balances). The
    /// store adds only what is left under the limits (50 and 50 — never past the $500
    /// deductible or the $3,000 OOP maximum), the claim's paid amounts are left alone, and an
    /// adjustment review with both clamps is raised and listed in the work queue.
    /// </summary>
    [SkippableFact]
    public async Task ConcurrentClaimTakesTheRoom_TheCommitIsClamped_AndAReviewIsRaised()
    {
        var pended = await SubmitAndAdjudicateAsync();
        await AssertBalances(0m, 0m);
        _afterPricing = async () =>
        {
            var concurrent = await _accumulators.CommitAsync(new AccumulatorCommit
            {
                CommitId = Guid.NewGuid().ToString("N"), ClaimId = "CONCURRENT", MemberId = Member, SubscriberId = Member,
                BenefitPlanId = PlanGuid, PlanYear = PlanYear,
                Updates =
                [
                    new AccumulatorUpdate { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                        NetworkTier = NetworkTier.InNetwork, Amount = 450m, Source = "Deductible", ClampAtLimit = 500m },
                    new AccumulatorUpdate { Type = AccumulatorType.IndividualOutOfPocketMax, Scope = AccumulatorScope.Individual,
                        NetworkTier = NetworkTier.InNetwork, Amount = 2950m, Source = "OOP", ClampAtLimit = 3000m },
                ],
            });
            Assert.Equal(AccumulatorCommitOutcome.Committed, concurrent.Outcome);
        };

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", pended.Id, "Approved")));

        await AssertBalances(500m, 3000m);
        var claim = (await Claim(pended.Id))!;
        Assert.Equal(100m, claim.AdjudicationResult!.DeductibleAmount); // not changed automatically
        var review = claim.AccumulatorClampReview!;
        Assert.False(review.Resolved);
        Assert.Contains(review.Clamps, c => c.Type == AccumulatorType.IndividualDeductible && c.Requested == 100m && c.Applied == 50m);
        Assert.Contains(review.Clamps, c => c.Type == AccumulatorType.IndividualOutOfPocketMax && c.Requested == 100m && c.Applied == 50m);

        var queue = await Controller("examiner-2").GetAccumulatorAdjustments();
        var item = Assert.Single(Assert.IsAssignableFrom<IEnumerable<AccumulatorAdjustmentItem>>(((OkObjectResult)queue.Result!).Value));
        Assert.Equal(pended.Id, item.ClaimId);
        Assert.IsType<NoContentResult>(await Controller("examiner-2").ResolveAccumulatorAdjustment(pended.Id));
        Assert.True((await Claim(pended.Id))!.AccumulatorClampReview!.Resolved);
    }

    /// <summary>
    /// S1: the dispatcher drives the same commit while the inline attempt waits for its answer
    /// (a slow request outliving the inline grace). The dispatcher's attempt gets
    /// AlreadyCommitted — carrying the first attempt's clamps — saves the review and clears
    /// the entry; the inline attempt's clear then finds nothing. The review is saved once,
    /// whichever attempt finishes first.
    /// </summary>
    [SkippableFact]
    public async Task ClampReview_IsSaved_WhenTheDispatcherFinishesTheCommitFirst()
    {
        var pended = await SubmitAndAdjudicateAsync();
        _afterPricing = async () => Assert.Equal(AccumulatorCommitOutcome.Committed, (await _accumulators.CommitAsync(new AccumulatorCommit
        {
            CommitId = Guid.NewGuid().ToString("N"), ClaimId = "CONCURRENT", MemberId = Member, SubscriberId = Member,
            BenefitPlanId = PlanGuid, PlanYear = PlanYear,
            Updates =
            [
                new AccumulatorUpdate { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                    NetworkTier = NetworkTier.InNetwork, Amount = 450m, Source = "Deductible", ClampAtLimit = 500m },
            ],
        })).Outcome);
        AccumulatorOutboxResult? dispatcherSaw = null;
        _afterCommitAnswer = async () =>
        {
            _clock.Advance(Claims.AccumulatorOutboxItem.InlineGrace + TimeSpan.FromSeconds(1));
            var due = (await Claim(pended.Id))!.PendingAccumulatorCommit!;
            dispatcherSaw = await _outbox.ProcessAsync(Tenant, pended.Id, Claims.AccumulatorOutboxKind.Commit, due.Id, CancellationToken.None);
        };

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", pended.Id, "Approved")));

        Assert.Equal(AccumulatorOutboxResult.AlreadyCommitted, dispatcherSaw);
        var claim = (await Claim(pended.Id))!;
        Assert.Null(claim.PendingAccumulatorCommit);
        var review = claim.AccumulatorClampReview!;
        Assert.Equal(Claims.AccumulatorClampReview.Clamped, review.Kind);
        Assert.Contains(review.Clamps, c => c.Type == AccumulatorType.IndividualDeductible && c.Requested == 100m && c.Applied == 50m);
        await AssertBalances(500m, 100m);
    }

    /// <summary>
    /// S2: a version whose commit is still pending (benefit-plan-service down) is superseded by
    /// a replacement before the commit runs. The commit is dropped, never written — the
    /// replacement's own commit counts the claim.
    /// </summary>
    [SkippableFact]
    public async Task ASupersededVersionsPendingCommit_IsDropped()
    {
        _ncciPends = false;
        _accumulatorWritesDown = true;
        var approved = await SubmitAndAdjudicateAsync();
        Assert.NotNull(approved.PendingAccumulatorCommit);

        Assert.True(await _claims.MarkSupersededProjectionAsync(Tenant, approved.Id, "V2", DateTime.UtcNow, "adjuster"));
        _accumulatorWritesDown = false;
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));

        Assert.Null((await Claim(approved.Id))!.PendingAccumulatorCommit);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(approved.Id);
    }

    /// <summary>
    /// S2 (in flight): the replacement's commit fences the claim it replaces, so the
    /// original's commit arriving after it — driven before the version was marked superseded —
    /// is refused instead of counting the claim twice.
    /// </summary>
    [SkippableFact]
    public async Task TheOriginalsCommit_AfterItsReplacementCommitted_IsRefused()
    {
        _ncciPends = false;
        _accumulatorWritesDown = true;
        var original = await SubmitAndAdjudicateAsync();
        var originalCommit = original.PendingAccumulatorCommit!.Commit!;
        _accumulatorWritesDown = false;

        var replacement = originalCommit with { CommitId = Guid.NewGuid().ToString("N"), ClaimId = "V2", ReplacesClaimId = original.Id };
        Assert.Equal(AccumulatorCommitOutcome.Committed, (await _engine.CommitAccumulatorsAsync(replacement)).Outcome);
        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, (await _engine.CommitAccumulatorsAsync(originalCommit)).Outcome);

        await AssertBalances(100m, 100m);
    }

    /// <summary>
    /// S3: a void saves its terminal reversal with the void write (outbox). benefit-plan-service
    /// is down at the void: the void stands, the entry stays on the claim, and the dispatcher
    /// reverses and fences it once the service is back — the voided claim is not left counted.
    /// </summary>
    [SkippableFact]
    public async Task Void_ReversalFailing_IsSavedWithTheVoid_AndRedriven()
    {
        _ncciPends = false;
        var approved = await SubmitAndAdjudicateAsync();
        await AssertBalances(100m, 100m);
        Assert.Equal(ClaimFinalizationOutcome.Finalized, (await _finalization.FinalizeAsync(approved.Id,
            new ClaimFinalizationRequest { CheckNumber = "CHK-1", PayerPayment = 50m }, Tenant, "payment-service", null)).Outcome);
        _accumulatorWritesDown = true;

        var voided = await _finalization.VoidAsync(approved.Id, new ClaimVoidRequest { Reason = "duplicate" }, Tenant, "operator", null);

        Assert.Equal(Claims.ClaimStatus.Voided, voided.Claim!.Status);
        var stored = (await Claim(approved.Id))!;
        Assert.Equal(Claims.ClaimStatus.Voided, stored.Status);
        var entry = stored.PendingAccumulatorReversal!;
        Assert.Equal(1, entry.Attempts);
        await AssertBalances(100m, 100m);

        _accumulatorWritesDown = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));
        Assert.Null((await Claim(approved.Id))!.PendingAccumulatorReversal);
        await AssertBalances(0m, 0m);
        await AssertFencedAsync(approved.Id);
    }

    /// <summary>
    /// The store refuses the commit of a claim that is still Approved (its id was fenced): the
    /// claim's paid amounts were priced with a cost share the member's accumulators do not hold,
    /// so a review is raised in the work queue (not only a log line).
    /// </summary>
    [SkippableFact]
    public async Task ARefusedCommit_OfAnApprovedClaim_RaisesAWorkQueueItem()
    {
        _ncciPends = false;
        _accumulatorWritesDown = true;
        var approved = await SubmitAndAdjudicateAsync();
        var commit = approved.PendingAccumulatorCommit!.Commit!;
        await _accumulators.ReverseTerminallyAsync(commit.MemberId, commit.SubscriberId, commit.BenefitPlanId, commit.PlanYear, approved.Id);
        _accumulatorWritesDown = false;
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));

        var claim = (await Claim(approved.Id))!;
        Assert.Null(claim.PendingAccumulatorCommit);
        Assert.Equal(Claims.AccumulatorClampReview.Refused, claim.AccumulatorClampReview!.Kind);
        var queue = await Controller("examiner-2").GetAccumulatorAdjustments();
        var item = Assert.Single(Assert.IsAssignableFrom<IEnumerable<AccumulatorAdjustmentItem>>(((OkObjectResult)queue.Result!).Value));
        Assert.Equal(Claims.AccumulatorClampReview.Refused, item.Kind);
        // (Balances: the journalled store holds nothing; the Redis cache rebuilds from
        // claims-service, which counts the Approved claim — the inconsistency the review is for.)
    }

    /// <summary>
    /// A misordered deploy (claims-service before benefit-plan-service) pends passing claims
    /// ACCUMCOMMIT. After the upgrade the bulk re-run approves them — a clean re-adjudication,
    /// nothing overridden — and commits each once; a second run finds nothing to do.
    /// </summary>
    [SkippableFact]
    public async Task AccumCommitPends_AreApprovedByTheBulkRerun_AfterTheUpgrade()
    {
        _ncciPends = false;
        _noPreparedCommit = true;
        var pended = await SubmitAndAdjudicateAsync();
        Assert.Equal(Claims.ClaimStatus.Pended, pended.Status);
        Assert.Equal(BenefitCalculationStage.DeferredCommitUnsupportedPendCode, pended.PendDetails!.PendCode);

        // Still old: the re-run leaves it pended.
        var early = await _operations.RerunAccumulatorCommitPendsAsync(Tenant, "ops-1", 10, CancellationToken.None);
        Assert.Equal(1, early.StillPended);
        Assert.Equal(Claims.ClaimStatus.Pended, (await Claim(pended.Id))!.Status);
        await AssertBalances(0m, 0m);

        _noPreparedCommit = false;
        var run = await _operations.RerunAccumulatorCommitPendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(1, run.Approved);
        var approved = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Approved, approved.Status);
        Assert.Null(approved.PendingAccumulatorCommit);
        Assert.Contains(approved.ExaminerResolutions, r => r.ApproverIds.Single().StartsWith(AccumulatorOperationsService.RerunActorPrefix));
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(pended.Id);
        Assert.Equal(0, (await _operations.RerunAccumulatorCommitPendsAsync(Tenant, "ops-1", 10, CancellationToken.None)).Examined);
    }

    /// <summary>
    /// D-04 / Q-9: the plan requires prior authorization for 99213 and authorization-service
    /// is down. The claim pends PAUNAVAIL before benefit calculation — nothing priced, no
    /// commit prepared, nothing written. An approval while the lookup still fails is refused
    /// (the check is retried, never overridden) and writes nothing. Once the lookup answers
    /// Valid, the approval re-run prices the claim and commits it exactly once.
    /// </summary>
    [SkippableFact]
    public async Task PriorAuthLookupUnavailable_PendsWithNoWrite_ApprovalRetriesTheLookup_ThenCommitsOnce()
    {
        _ncciPends = false;
        _priorAuthCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99213" };
        _beforeSubmit = claim => claim.PriorAuthorizationNumber = "AUTH-GOLD-01";
        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.Unavailable("authorization-service returned 503"));

        var pended = await SubmitAndAdjudicateAsync();

        Assert.Equal(Claims.ClaimStatus.Pended, pended.Status);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, pended.PendDetails!.PendCode);
        Assert.Null(pended.PendingAccumulatorCommit);
        Assert.Empty(_calculated);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);
        await _authorization.Received(1).ValidateAsync(Tenant, "AUTH-GOLD-01", "99213",
            Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        // Still down: the approval is refused and nothing is written.
        var refused = await ResolveAsync("examiner-1", pended.Id, "Approved");
        Assert.Equal(409, StatusOf(refused));
        Assert.Equal(Claims.ClaimStatus.Pended, (await Claim(pended.Id))!.Status);
        Assert.Empty(_calculated);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);

        // authorization-service answers: the approval re-run prices and commits once.
        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.Valid(new AuthorizationValidationResult(
                "AUTH-GOLD-01", true, "Approved", null, null, null, 1, "Authorization valid")));
        var approved = await ResolveAsync("examiner-1", pended.Id, "Approved");

        Assert.Equal(200, StatusOf(approved));
        Assert.Equal(Claims.ClaimStatus.Approved, (await Claim(pended.Id))!.Status);
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(pended.Id);
    }

    /// <summary>
    /// #1299 S1: claims pended PAUNAVAIL while authorization-service was down are
    /// re-run in bulk once it answers, under the service identity (the lookup
    /// always carries claims-service's token). A run while it is still down
    /// leaves them pended and writes nothing; once it answers Valid they are
    /// approved, each committed once; a second run finds nothing to do.
    /// </summary>
    [SkippableFact]
    public async Task PaUnavailablePends_AreApprovedByTheBulkRerun_OnceTheLookupAnswers()
    {
        _ncciPends = false;
        _priorAuthCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99213" };
        _beforeSubmit = claim => claim.PriorAuthorizationNumber = "AUTH-GOLD-01";
        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.Unavailable("authorization-service returned 503"));
        var pended = await SubmitAndAdjudicateAsync();
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, pended.PendDetails!.PendCode);

        var early = await _operations.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);
        Assert.Equal(1, early.StillPended);
        Assert.Equal(Claims.ClaimStatus.Pended, (await Claim(pended.Id))!.Status);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);

        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.Valid(new AuthorizationValidationResult(
                "AUTH-GOLD-01", true, "Approved", null, null, null, 1, "Authorization valid")));
        var run = await _operations.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(1, run.Approved);
        var approved = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Approved, approved.Status);
        Assert.Contains(approved.ExaminerResolutions, r => r.ApproverIds.Single().StartsWith(AccumulatorOperationsService.PriorAuthRerunActorPrefix));
        await AssertBalances(100m, 100m);
        await AssertCommittedOnceAsync(pended.Id);
        Assert.Equal(0, (await _operations.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None)).Examined);
    }

    /// <summary>
    /// #1299 S1: when the recovered lookup answers definitively that the number
    /// does not exist for the tenant, the bulk re-run's denial (CARC 197) stands,
    /// and nothing is written to the accumulators.
    /// </summary>
    [SkippableFact]
    public async Task PaUnavailablePend_BulkRerun_DeniesOnADefinitiveNotFound()
    {
        _ncciPends = false;
        _priorAuthCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99213" };
        _beforeSubmit = claim => claim.PriorAuthorizationNumber = "AUTH-GOLD-UNKNOWN";
        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.Unavailable("authorization-service returned 503"));
        var pended = await SubmitAndAdjudicateAsync();

        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.NotFound());
        var run = await _operations.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(1, run.Denied);
        var denied = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Deny, denied.Status);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, denied.AdjudicationResult!.DenialReasonCode);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);
    }

    /// <summary>
    /// #1299 S-B, under the Pay/Deny lifecycle: the bulk re-run's denial of a pended claim
    /// (which wrote nothing) queues no reversal and fences nothing — Deny is reprocessable. A
    /// stale prepared commit can only reach the store through the claim's outbox, which drops
    /// a commit on a denied claim (claims-service tests).
    /// </summary>
    [SkippableFact]
    public async Task PaUnavailablePend_BulkDenied_QueuesNothing_AndIsNotFenced()
    {
        // A priced claim of the same member: its prepared commit is the template for the
        // late write, re-addressed to the denied claim.
        _ncciPends = true;
        var template = await SubmitAndAdjudicateAsync();
        Assert.Equal("NCCI", template.PendDetails!.PendCode);
        var prepared = _calculated[^1].PreparedAccumulatorCommit!;

        _ncciPends = false;
        _priorAuthCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99213" };
        _beforeSubmit = claim => claim.PriorAuthorizationNumber = "AUTH-GOLD-UNKNOWN";
        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.Unavailable("authorization-service returned 503"));
        var pended = await SubmitAndAdjudicateAsync();
        _authorization.ValidateAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(AuthorizationLookupResult.NotFound());
        var run = await _operations.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);
        Assert.Equal(1, run.Denied);
        Assert.NotNull(prepared);
        var denied = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Deny, denied.Status);
        Assert.Null(denied.PendingAccumulatorReversal);
        await AssertNotFencedAsync(pended.Id);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);
    }

    /// <summary>
    /// The post-rollout sweep lists finalized claims with no pending entry and no write record
    /// in the store (an old pod dropped the entry), and nothing for a committed claim.
    /// </summary>
    [SkippableFact]
    public async Task Sweep_ListsAFinalizedClaimWhoseEntryWasDropped()
    {
        _ncciPends = false;
        var committed = await SubmitAndAdjudicateAsync();
        _accumulatorWritesDown = true;
        var dropped = await SubmitAndAdjudicateAsync();
        // An old claims-service pod rewrote the claim without the outbox field.
        Assert.True(await _claims.CompleteAccumulatorOutboxAsync(Tenant, dropped.Id, Claims.AccumulatorOutboxKind.Commit, dropped.PendingAccumulatorCommit!.Id));
        _accumulatorWritesDown = false;

        var sweep = await _operations.FindMissingCommitsAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), 100, CancellationToken.None);

        Assert.Equal(2, sweep.Examined);
        var missing = Assert.Single(sweep.Missing);
        Assert.Equal(dropped.Id, missing.ClaimId);
        Assert.DoesNotContain(sweep.Missing, m => m.ClaimId == committed.Id);
    }

    // ── stages standing in for NCCI and for a stall ───────────────────────

    /// <summary>
    /// The NCCI stage's pend (code NCCI, Order 400 — after benefit calculation), the same
    /// finding on every run, so the examiner's approval overrides it on the re-run.
    /// </summary>
    private sealed class NcciPendStage(Func<bool> pends) : IClaimAdjudicationStage
    {
        public string Name => NcciEditsStage.StageName;
        public int Order => 400;
        public bool IsRequired => false;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
        {
            if (!pends()) return Task.FromResult(ClaimAdjudicationStageResult.Pass(Name));
            if (context.PendDetails is null)
                context.PendDetails = new Claims.PendDetails { PendCode = "NCCI", PendReason = NcciReason, PendedAt = DateTime.UtcNow };
            else
                context.PendDetails.AdditionalPendReasons.Add($"NCCI: {NcciReason}");
            return Task.FromResult(ClaimAdjudicationStageResult.Pend(Name, NcciReason));
        }
    }

    /// <summary>On an approval re-run, runs the test's hook once (a stall, or a concurrent write).</summary>
    private sealed class HookStage(string name, int order, Func<Func<Task>?> takeHook) : IClaimAdjudicationStage
    {
        public string Name => name;
        public int Order => order;
        public bool IsRequired => false;

        public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
        {
            if (context.ExaminerApproval is not null && takeHook() is { } hook)
                await hook();
            return ClaimAdjudicationStageResult.Pass(Name);
        }
    }

    protected sealed class FixedTenant(string tenantId) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = tenantId;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>Assertions on the journalled engine store (<see cref="AccumulatorDocument"/>, Mongo or Cosmos).</summary>
public abstract class AccumulatorIntegrityChoTests : AccumulatorIntegrityTests
{
    protected abstract Task<List<AccumulatorDocument>> AccumulatorDocumentsAsync();

    protected override async Task AssertNoWriteForAsync(string claimId) =>
        Assert.DoesNotContain((await AccumulatorDocumentsAsync()).SelectMany(d => d.Transactions), t => t.ClaimId == claimId && !t.IsReversed);

    protected override async Task AssertCommittedOnceAsync(string claimId)
    {
        var individual = Assert.Single(await AccumulatorDocumentsAsync(), d => d.Scope == "Individual");
        var transaction = Assert.Single(individual.Transactions, t => t.ClaimId == claimId && !t.IsReversed);
        Assert.NotNull(transaction.CommitId);
    }

    protected override async Task AssertFencedAsync(string claimId) =>
        Assert.All((await AccumulatorDocumentsAsync()).Where(d => d.Scope == "Individual"),
            d => Assert.Contains(claimId, d.ReversedClaimIds));

    protected override async Task AssertNotFencedAsync(string claimId) =>
        Assert.All(await AccumulatorDocumentsAsync(), d => Assert.DoesNotContain(claimId, d.ReversedClaimIds));
}

/// <summary><see cref="AccumulatorIntegrityTests"/> on MongoDB: <see cref="ClaimRepositoryMongo"/> and <see cref="AccumulatorRepositoryMongo"/>.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorIntegrityMongoTests(MongoRunnerFixture mongo) : AccumulatorIntegrityChoTests
{
    private IMongoDatabase _db = null!;

    protected override Task<(IClaimRepository Claims, IAccumulatorService Accumulators)> CreateStoresAsync(
        AdjudicationTenantContext tenantContext)
    {
        _db = mongo.CreateDatabase("accumulator_integrity");
        return Task.FromResult<(IClaimRepository, IAccumulatorService)>((
            new ClaimRepositoryMongo(_db, new HttpContextAccessor(), NullLogger<ClaimRepositoryMongo>.Instance, tenantContext),
            new ChoAccumulatorService(
                new AccumulatorRepositoryMongo(_db, new ConfigurationBuilder().Build(), NullLogger<AccumulatorRepositoryMongo>.Instance),
                new FixedTenant(Tenant), NullLogger<ChoAccumulatorService>.Instance)));
    }

    protected override async Task ExpireResolutionLockAsync(string claimId)
    {
        var result = await _db.GetCollection<Claims.Claim>("Claims").UpdateOneAsync(
            Builders<Claims.Claim>.Filter.Eq(c => c.Id, claimId),
            Builders<Claims.Claim>.Update.Set(c => c.ResolutionLock!.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(1, result.ModifiedCount);
    }

    protected override Task<List<AccumulatorDocument>> AccumulatorDocumentsAsync() =>
        _db.GetCollection<AccumulatorDocument>("Accumulators").Find(FilterDefinition<AccumulatorDocument>.Empty).ToListAsync();

    public override Task DisposeAsync() => mongo.DropDatabaseAsync(_db);
}

/// <summary>
/// <see cref="AccumulatorIntegrityTests"/> on the store benefit-plan-service runs:
/// <see cref="RedisAccumulatorService"/> on a real redis-server, rebuilding a cold hash from
/// claims-service's <c>GET /api/claims/accumulator-totals</c> (the real repository query,
/// with its per-claim contributions, over the real <see cref="ClaimsServiceAccumulatorSource"/>),
/// with claims on MongoDB. Skipped when no Redis is reachable (CI provides one).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorIntegrityRedisTests(MongoRunnerFixture mongo, RedisServerFixture redis)
    : AccumulatorIntegrityTests, IClassFixture<RedisServerFixture>
{
    /// <summary>This class's Redis database (flushed per test: the golden tenant's key names are fixed).</summary>
    private const int RedisDatabase = 7;
    private IMongoDatabase _db = null!;
    private ConnectionMultiplexer _connection = null!;

    protected override bool UndoesLegacyWrites => false;

    protected override async Task<(IClaimRepository Claims, IAccumulatorService Accumulators)> CreateStoresAsync(
        AdjudicationTenantContext tenantContext)
    {
        Skip.If(redis.Unavailable is not null, redis.Unavailable);
        _connection = redis.Connect(RedisDatabase);
        foreach (var endpoint in _connection.GetEndPoints())
            await _connection.GetServer(endpoint).FlushDatabaseAsync(RedisDatabase);

        _db = mongo.CreateDatabase("accumulator_integrity_redis");
        var claims = new ClaimRepositoryMongo(_db, new HttpContextAccessor(), NullLogger<ClaimRepositoryMongo>.Instance, tenantContext);
        var claimsService = new HttpClient(new DelegatingServiceHandler(async (request, ct) =>
        {
            // ClaimsController.GetAccumulatorTotals over the repository.
            Assert.Equal("/api/claims/accumulator-totals", request.RequestUri!.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var totals = await claims.GetAccumulatorTotalsAsync(query["ownerId"]!, query["scope"]!, query["benefitPlanId"]!, query["planYear"]!, ct);
            return DelegatingServiceHandler.Json(totals, Wire.ClaimsService);
        }))
        { BaseAddress = new Uri("http://claims-service/") };
        var source = new ClaimsServiceAccumulatorSource(claimsService, NullLogger<ClaimsServiceAccumulatorSource>.Instance);
        return (claims, new RedisAccumulatorService(_connection, source, new FixedTenant(Tenant),
            NullLogger<RedisAccumulatorService>.Instance));
    }

    private RedisKey IndividualKey => $"accum:{Tenant}:IND:{Member}:{PlanGuid}:{PlanYear}";

    protected override async Task ExpireResolutionLockAsync(string claimId)
    {
        var result = await _db.GetCollection<Claims.Claim>("Claims").UpdateOneAsync(
            Builders<Claims.Claim>.Filter.Eq(c => c.Id, claimId),
            Builders<Claims.Claim>.Update.Set(c => c.ResolutionLock!.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(1, result.ModifiedCount);
    }

    protected override async Task AssertNoWriteForAsync(string claimId)
    {
        var record = await _connection.GetDatabase().HashGetAsync(IndividualKey, "__commit:" + claimId);
        Assert.True(record.IsNullOrEmpty || record.ToString().EndsWith("|{}", StringComparison.Ordinal), record.ToString());
    }

    protected override async Task AssertCommittedOnceAsync(string claimId)
    {
        var record = (await _connection.GetDatabase().HashGetAsync(IndividualKey, "__commit:" + claimId)).ToString();
        Assert.False(string.IsNullOrEmpty(record));
        Assert.Contains("IndividualDeductible", record);
        Assert.Equal(100m, (await Accumulators.GetClaimUpdatesAsync(Member, Member, PlanGuid, PlanYear, claimId))
            .Where(u => u.Type == AccumulatorType.IndividualDeductible).Sum(u => u.Amount));
    }

    protected override async Task AssertFencedAsync(string claimId) =>
        Assert.True(await _connection.GetDatabase().HashExistsAsync(IndividualKey, "__fence:" + claimId));

    protected override async Task AssertNotFencedAsync(string claimId) =>
        Assert.False(await _connection.GetDatabase().HashExistsAsync(IndividualKey, "__fence:" + claimId));

    public override async Task DisposeAsync()
    {
        if (_db is not null) await mongo.DropDatabaseAsync(_db);
        _connection?.Dispose();
    }
}
