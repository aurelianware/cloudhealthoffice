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
using CloudHealthOffice.Testing.Cosmos;
using CloudHealthOffice.Testing.Mongo;
using CloudHealthOffice.Testing.Redis;
using Microsoft.Azure.Cosmos;
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
/// its HTTP contract, on four backends:
/// <see cref="AccumulatorIntegrityMongoTests"/> and <see cref="AccumulatorIntegrityCosmosTests"/>
/// (the engine's Mongo / Cosmos store), and <see cref="AccumulatorIntegrityRedisTests"/> — the
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
                return await benefitPlanService.SendAsync(request, ct);
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
            .Returns(new ResolvedBenefitPlan { Id = PlanGuid.ToString(), PlanGuid = PlanGuid });
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
            new BenefitCalculationStage(_engine, memberResolver, Substitute.For<IAuthorizationValidationClient>(),
                NullLogger<BenefitCalculationStage>.Instance),
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
    }

    private ClaimAdapterFactory _submit = null!;

    // ── the pipeline and the resolver ─────────────────────────────────────

    /// <summary>Submits golden 01 and runs the pipeline on it (the NCCI stand-in pends it at Order 400 unless told not to).</summary>
    private async Task<Claims.Claim> SubmitAndAdjudicateAsync()
    {
        var parsed = Assert.Single(X12837Parser.Parse(GoldenInputs.Edi837("01-office-visit")));
        var submission = await new ClaimSubmissionService(
                _submit, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(),
                NullLogger<ClaimSubmissionService>.Instance)
            .SubmitAsync(X12837ClaimMapper.Map(parsed, Tenant), Tenant, "submitter", "corr", CancellationToken.None);
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
            Substitute.For<IMpipAdjudicationEnhancer>(),
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
            withOutbox ? _outbox : null);
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
    /// The pended claim is denied: nothing was applied, so nothing is reversed — and the claim
    /// id is fenced, so the write the pended pricing prepared can never be committed later.
    /// </summary>
    [SkippableFact]
    public async Task NcciPendedClaim_Denied_LeavesNothing_AndALateCommitIsRefused()
    {
        var pended = await SubmitAndAdjudicateAsync();

        var denied = await ResolveAsync("examiner-1", pended.Id, "Denied");

        Assert.Equal(200, StatusOf(denied));
        var after = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Denied, after.Status);
        Assert.Null(after.PendingAccumulatorReversal);
        await AssertBalances(0m, 0m);
        await AssertNoWriteForAsync(pended.Id);
        await AssertFencedAsync(pended.Id);

        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed,
            (await _engine.CommitAccumulatorsAsync(_calculated[0].PreparedAccumulatorCommit!)).Outcome);
        await AssertBalances(0m, 0m);
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
        Skip.If(!UndoesLegacyWrites && disposition == "Approved",
            "The Redis store kept no journal for writes made before deferred commits (deploy notes).");
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
        Assert.Equal(Claims.ClaimStatus.Denied, final.Status);
        Assert.Equal("Denied", Assert.Single(final.ExaminerResolutions).Disposition);
        Assert.Null(final.PendingAccumulatorCommit);
        await AssertBalances(0m, 0m);
        Assert.Equal(0m, await Balance(AccumulatorType.FamilyDeductible, AccumulatorScope.Family));
        await AssertFencedAsync(pended.Id);

        // A's prepared write (from its re-run) cannot land now either.
        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed,
            (await _engine.CommitAccumulatorsAsync(_calculated[^1].PreparedAccumulatorCommit!)).Outcome);
        await AssertBalances(0m, 0m);
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
        var pended = await SubmitAndAdjudicateAsync();
        var legacy = _calculated[0].PreparedAccumulatorCommit!;
        await AssertBalances(0m, 0m);
        await _accumulators.ApplyUpdatesAsync(legacy.MemberId, legacy.SubscriberId, legacy.BenefitPlanId, legacy.PlanYear,
            pended.Id, legacy.Updates);
        _accumulatorWritesDown = true;

        Assert.Equal(200, StatusOf(await ResolveAsync("examiner-1", pended.Id, "Denied")));

        var denied = (await Claim(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Denied, denied.Status);
        Assert.NotNull(denied.PendingAccumulatorReversal);
        await AssertBalances(100m, 100m);

        _accumulatorWritesDown = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _dispatcher.RunOnceAsync(CancellationToken.None));
        Assert.Null((await Claim(pended.Id))!.PendingAccumulatorReversal);
        await AssertBalances(0m, 0m);
        await AssertFencedAsync(pended.Id);
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
/// <see cref="AccumulatorIntegrityTests"/> on Cosmos DB (the emulator): claims-service's
/// <see cref="ClaimRepository"/> (its System.Text.Json serializer, conditional patch and
/// ETag fences, the outbox patches) and the engine's <see cref="AccumulatorRepositoryCosmos"/>.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class AccumulatorIntegrityCosmosTests(CosmosEmulatorFixture cosmos) : AccumulatorIntegrityChoTests
{
    private const string ClaimsContainer = "ClaimsV2";
    private Container _claimsContainer = null!;
    private Container _accumulatorContainer = null!;

    protected override async Task<(IClaimRepository Claims, IAccumulatorService Accumulators)> CreateStoresAsync(
        AdjudicationTenantContext tenantContext)
    {
        cosmos.SkipIfUnavailable();
        // claims-service: the serializer AddChoDatabase registers.
        var claimsClient = cosmos.CreateClient(new CloudHealthOffice.Infrastructure.Serialization.CosmosSystemTextJsonSerializer());
        var claimsDb = await cosmos.CreateDatabaseAsync(claimsClient, "integrity_claims");
        _claimsContainer = await CosmosEmulatorFixture.CreateContainerAsync(claimsDb, ClaimsContainer);
        var claimsConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CosmosDb:DatabaseName"] = claimsDb.Id,
            ["CosmosDb:ContainerName"] = ClaimsContainer,
        }).Build();
        // The tenant is read from the request context at call time; a plain
        // (non-AsyncLocal) accessor carries it into every test method.
        var claims = new ClaimRepository(claimsClient, claimsConfig, new FixedAccessor(Tenant), NullLogger<ClaimRepository>.Instance);

        var accumulatorClient = cosmos.CreateClient(serializerOptions: new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        });
        var accumulatorDb = await cosmos.CreateDatabaseAsync(accumulatorClient, "integrity_accumulators");
        _accumulatorContainer = await CosmosEmulatorFixture.CreateContainerAsync(accumulatorDb, "Accumulators");
        var accumulatorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CosmosDb:DatabaseName"] = accumulatorDb.Id,
        }).Build();
        return (claims, new ChoAccumulatorService(
            new AccumulatorRepositoryCosmos(accumulatorClient, accumulatorConfig, NullLogger<AccumulatorRepositoryCosmos>.Instance),
            new FixedTenant(Tenant), NullLogger<ChoAccumulatorService>.Instance));
    }

    protected override async Task ExpireResolutionLockAsync(string claimId)
    {
        var response = await _claimsContainer.PatchItemAsync<System.Text.Json.JsonElement>(
            claimId, new PartitionKey(Tenant),
            [PatchOperation.Set("/resolutionLock/expiresAt", DateTime.UtcNow.AddMinutes(-1))]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    protected override async Task<List<AccumulatorDocument>> AccumulatorDocumentsAsync()
    {
        var docs = new List<AccumulatorDocument>();
        using var feed = _accumulatorContainer.GetItemQueryIterator<AccumulatorDocument>("SELECT * FROM c");
        while (feed.HasMoreResults) docs.AddRange(await feed.ReadNextAsync());
        return docs;
    }

    public override Task DisposeAsync() => Task.CompletedTask;
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

    public override async Task DisposeAsync()
    {
        if (_db is not null) await mongo.DropDatabaseAsync(_db);
        _connection?.Dispose();
    }
}

/// <summary>A request context with a fixed tenant that flows into every test method (not AsyncLocal).</summary>
internal sealed class FixedAccessor : IHttpContextAccessor
{
    public FixedAccessor(string tenant)
    {
        HttpContext = new DefaultHttpContext();
        HttpContext.Items["TenantId"] = tenant;
    }

    public HttpContext? HttpContext { get; set; }
}
