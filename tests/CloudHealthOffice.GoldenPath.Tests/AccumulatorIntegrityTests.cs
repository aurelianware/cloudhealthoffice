using System.Net;
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
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;
using Claims = ClaimsService.Models;

namespace CloudHealthOffice.GoldenPath.Tests;

/// <summary>
/// Accumulator integrity of the claims pipeline, end to end on a real database —
/// MongoDB (<see cref="AccumulatorIntegrityMongoTests"/>, EphemeralMongo) and Cosmos DB
/// (<see cref="AccumulatorIntegrityCosmosTests"/>, the emulator): claims-service (the claim
/// repository of that backend, the orchestrator, the real
/// <see cref="ClaimsController.ResolvePendedClaim"/>) and benefit-plan-service's
/// engine over the real accumulator store of that backend (<c>ChoAccumulatorService</c>),
/// reached over its HTTP contract.
/// <list type="bullet">
///   <item>Part 1 — a claim pended after benefit calculation (NCCI at Order
///     400) writes nothing while it is pended; an approval writes it exactly
///     once; a denial leaves nothing.</item>
///   <item>Part 2 — an approval re-run whose resolution lock expires mid-run,
///     and whose claim the new lock holder then denies, leaves no accumulators.</item>
/// </list>
/// Claim: golden 01 (99213, allowed $100), no prior accumulators: the member
/// owes the $100 as deductible, so an applied claim shows deductible 100 / OOP 100.
/// </summary>
public abstract class AccumulatorIntegrityTests : IAsyncLifetime
{
    protected const string Tenant = GoldenScenario.TenantId;
    private const string NcciReason = "NCCI PTP edit: 99213 is bundled into a procedure on this date (modifier not allowed).";

    private IClaimRepository _claims = null!;
    private IAccumulatorService _accumulators = null!;
    private HttpBenefitCalculationEngineClient _engine = null!;
    private ClaimAdjudicationOrchestrator _orchestrator = null!;
    private Guid _planGuid;
    private readonly List<BenefitResolutionResult> _calculated = new();

    /// <summary>Runs once, inside the next approval re-run, between COB (275) and benefit calculation (300).</summary>
    private Func<Task>? _midRerun;

    /// <summary>The backend: the claim repository and the engine's accumulator repository.</summary>
    protected abstract Task<(IClaimRepository Claims, IAccumulatorRepository Accumulators)> CreateStoresAsync(
        AdjudicationTenantContext tenantContext);

    /// <summary>Moves the claim's resolution lock into the past, in the backend's own store.</summary>
    protected abstract Task ExpireResolutionLockAsync(string claimId);

    /// <summary>Every engine accumulator document of the test tenant.</summary>
    protected abstract Task<List<AccumulatorDocument>> AccumulatorDocumentsAsync();

    public abstract Task DisposeAsync();

    public async Task InitializeAsync()
    {
        var tenantContext = new AdjudicationTenantContext { TenantId = Tenant };
        var (claimStore, accumulatorStore) = await CreateStoresAsync(tenantContext);
        _claims = claimStore;
        _accumulators = new ChoAccumulatorService(accumulatorStore, new FixedTenant(Tenant), NullLogger<ChoAccumulatorService>.Instance);

        var scenario = GoldenInputs.Scenario(null);
        _planGuid = Guid.Parse(GoldenPathHarness.LoadPlan(scenario.PlanDocument).Id);
        var http = new RoutingHttpClientFactory()
            .Route(UpstreamClientNames.BenefitPlanService,
                GoldenPathHarness.BenefitPlanServiceHandler(scenario, scenario.PlanDocument, _accumulators, r => _calculated.Add(r)));
        _engine = new HttpBenefitCalculationEngineClient(http, new HttpContextAccessor(), tenantContext,
            NullLogger<HttpBenefitCalculationEngineClient>.Instance);

        var adapterFactory = new ClaimAdapterFactory(
            new IClaimAdapter[] { new ChoClaimAdapter(_claims, NullLogger<ChoClaimAdapter>.Instance) },
            new ClaimTenantConfigCache(http, new ConfigurationBuilder().Build(), NullLogger<ClaimTenantConfigCache>.Instance),
            NullLogger<ClaimAdapterFactory>.Instance);

        var member = new ResolvedMember
        {
            MemberId = "MBR-GOLD-01",
            IsSubscriber = true,
            EnrollmentStatus = "Active",
            EffectiveDate = new DateTime(2026, 1, 1),
            DateOfBirth = new DateTime(1980, 6, 1),
        };
        var memberResolver = Substitute.For<IMemberResolver>();
        memberResolver.GetMemberAsync(Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        var coverageResolver = Substitute.For<ICoverageResolver>();
        coverageResolver.ResolveBenefitPlanIdAsync(Tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_planGuid.ToString());
        var planResolver = Substitute.For<IBenefitPlanResolver>();
        planResolver.GetPlanAsync(Tenant, _planGuid.ToString(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedBenefitPlan { Id = _planGuid.ToString(), PlanGuid = _planGuid });
        var coverageClient = Substitute.For<ICoverageClient>();
        coverageClient.GetCobEntriesAsync(Tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<CobEntry>());

        var stages = new IClaimAdjudicationStage[]
        {
            new CoordinationOfBenefitsStage(
                coverageClient, new CloudHealthOffice.CobEngine.Services.PayerOrderService(),
                Options.Create(new TenantEnforcementPolicyOptions()), NullLogger<CoordinationOfBenefitsStage>.Instance),
            new MidRerunStage(() => { var hook = _midRerun; _midRerun = null; return hook; }),
            new PricingStage(
                new HttpFeeSchedulePricingClient(http, NullLogger<HttpFeeSchedulePricingClient>.Instance),
                NullLogger<PricingStage>.Instance),
            new BenefitCalculationStage(_engine, memberResolver, Substitute.For<IAuthorizationValidationClient>(),
                NullLogger<BenefitCalculationStage>.Instance),
            new NcciPendStage(),
            new AccumulatorCommitStage(_engine, NullLogger<AccumulatorCommitStage>.Instance),
            new PersistenceStage(_claims, NullLogger<PersistenceStage>.Instance),
        };
        _orchestrator = new ClaimAdjudicationOrchestrator(
            adapterFactory, planResolver, memberResolver, coverageResolver, stages,
            Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(), tenantContext,
            Substitute.For<IClaimAdjustmentService>(), Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
        _submit = (adapterFactory, http);
    }

    private (ClaimAdapterFactory Adapters, RoutingHttpClientFactory Http) _submit;

    // ── the pipeline and the resolver ─────────────────────────────────────

    /// <summary>Submits golden 01 and runs the pipeline on it (the NCCI stand-in pends it at Order 400).</summary>
    private async Task<Claims.Claim> SubmitAndAdjudicateAsync()
    {
        var parsed = Assert.Single(X12837Parser.Parse(GoldenInputs.Edi837("01-office-visit")));
        var submission = await new ClaimSubmissionService(
                _submit.Adapters, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(),
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
        return (await _claims.GetByIdAsync(submitted.Id))!;
    }

    /// <summary><c>POST /api/claims/work-queue/{id}/resolve</c> through the real controller.</summary>
    private async Task<IActionResult> ResolveAsync(string examiner, string claimId, string disposition)
    {
        var claim = (await _claims.GetByIdAsync(claimId))!;
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
            _engine);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return await controller.ResolvePendedClaim(claimId, new ResolvePendedClaimRequest
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

    /// <summary>The golden plan's plan year (service date 2026-04-15).</summary>
    private const string PlanYear = "2026";

    private async Task<decimal> Balance(AccumulatorType type, AccumulatorScope scope = AccumulatorScope.Individual)
    {
        var snapshots = await _accumulators.GetAccumulatorsAsync("MBR-GOLD-01", "MBR-GOLD-01", _planGuid, PlanYear);
        return snapshots.Where(s => s.Type == type && s.Scope == scope).Sum(s => s.AccumulatedAmountAfter);
    }

    private async Task AssertBalances(decimal deductible, decimal oop)
    {
        Assert.Equal(deductible, await Balance(AccumulatorType.IndividualDeductible));
        Assert.Equal(oop, await Balance(AccumulatorType.IndividualOutOfPocketMax));
    }


    // ── part 1: no write while pended ─────────────────────────────────────

    /// <summary>
    /// NCCI pends the claim at Order 400, after benefit calculation priced it
    /// at 300. Before, that pricing ran in Production (no earlier stage had
    /// pended) and the deductible / OOP were on the member while the claim
    /// waited. Now nothing is written until the examiner approves; the
    /// approval writes it once ($100 deductible, $100 OOP), and repeating the
    /// commit, or redelivering the submission, changes nothing.
    /// </summary>
    [SkippableFact]
    public async Task NcciPendedClaim_WritesNothingUntilApproved_ThenExactlyOnce()
    {
        var pended = await SubmitAndAdjudicateAsync();

        Assert.Equal(Claims.ClaimStatus.Pended, pended.Status);
        Assert.Equal("NCCI", pended.PendDetails!.PendCode);
        Assert.Empty(await AccumulatorDocumentsAsync());
        await AssertBalances(0m, 0m);
        // The pended pricing prepared the write, it did not make it.
        Assert.Equal(PlanYear, _calculated[0].PreparedAccumulatorCommit!.PlanYear);
        Assert.Equal(100m, _calculated[0].PreparedAccumulatorCommit!.Updates
            .Where(u => u.Type == AccumulatorType.IndividualDeductible).Sum(u => u.Amount));

        var approved = await ResolveAsync("examiner-1", pended.Id, "Approved");

        Assert.Equal(200, StatusOf(approved));
        Assert.Equal(Claims.ClaimStatus.Approved, (await _claims.GetByIdAsync(pended.Id))!.Status);
        await AssertBalances(100m, 100m);
        var individual = Assert.Single(await AccumulatorDocumentsAsync(), d => d.Scope == "Individual");
        var transaction = Assert.Single(individual.Transactions, t => !t.IsReversed);
        Assert.Equal(pended.Id, transaction.ClaimId);
        Assert.NotNull(transaction.CommitId);

        // Exactly once: the same commit again, and a redelivered submission, write nothing.
        var rerunCommit = _calculated[^1].PreparedAccumulatorCommit!;
        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted, await _engine.CommitAccumulatorsAsync(rerunCommit));
        await _orchestrator.AdjudicateAsync(
            new ClaimVersionSubmittedMessage { TenantId = Tenant, ClaimId = pended.Id, ClaimVersionId = pended.ClaimVersionId },
            new MessageContext("m1", "corr", 2, new Dictionary<string, string>()), CancellationToken.None);
        await AssertBalances(100m, 100m);
    }

    /// <summary>
    /// The pended claim is denied: nothing was applied, so nothing is reversed
    /// — and the claim id is fenced, so the write the pended pricing prepared
    /// can never be committed later.
    /// </summary>
    [SkippableFact]
    public async Task NcciPendedClaim_Denied_LeavesNothing_AndALateCommitIsRefused()
    {
        var pended = await SubmitAndAdjudicateAsync();

        var denied = await ResolveAsync("examiner-1", pended.Id, "Denied");

        Assert.Equal(200, StatusOf(denied));
        Assert.Equal(Claims.ClaimStatus.Denied, (await _claims.GetByIdAsync(pended.Id))!.Status);
        await AssertBalances(0m, 0m);
        var individual = Assert.Single(await AccumulatorDocumentsAsync(), d => d.Scope == "Individual");
        Assert.Empty(individual.Transactions);
        Assert.Contains(pended.Id, individual.ReversedClaimIds);

        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed,
            await _engine.CommitAccumulatorsAsync(_calculated[0].PreparedAccumulatorCommit!));
        await AssertBalances(0m, 0m);
    }

    /// <summary>
    /// A claim pended before this change had written its accumulators at
    /// benefit calculation. Approving it replaces that write (deductible 100,
    /// not 200); denying such a claim backs it out.
    /// </summary>
    [SkippableTheory]
    [InlineData("Approved", 100)]
    [InlineData("Denied", 0)]
    public async Task ClaimPendedBeforeTheChange_WithAccumulatorsWritten_IsReplacedOrReversed(string disposition, int expected)
    {
        var pended = await SubmitAndAdjudicateAsync();
        var legacy = _calculated[0].PreparedAccumulatorCommit!;
        await _accumulators.ApplyUpdatesAsync(legacy.MemberId, legacy.SubscriberId, legacy.BenefitPlanId, legacy.PlanYear,
            pended.Id, legacy.Updates);
        await AssertBalances(100m, 100m);

        var result = await ResolveAsync("examiner-1", pended.Id, disposition);

        Assert.Equal(200, StatusOf(result));
        await AssertBalances(expected, expected);
    }

    // ── part 2: lock expiry mid-run ───────────────────────────────────────

    /// <summary>
    /// Examiner A approves. A's re-run stalls before benefit calculation for
    /// longer than its resolution lock; examiner B takes the lock and denies
    /// the claim; then A's re-run resumes. Before, A's benefit calculation
    /// wrote the accumulators in Production after B's denial had reversed
    /// (nothing): the denied claim kept deductible 100 / OOP 100. Now the
    /// re-run writes nothing, its fenced persistence is refused (409), and the
    /// commit is never made: zero.
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
        var final = (await _claims.GetByIdAsync(pended.Id))!;
        Assert.Equal(Claims.ClaimStatus.Denied, final.Status);
        Assert.Equal("Denied", Assert.Single(final.ExaminerResolutions).Disposition);
        await AssertBalances(0m, 0m);
        Assert.Equal(0m, await Balance(AccumulatorType.FamilyDeductible, AccumulatorScope.Family));

        // A's prepared write (from its re-run) cannot land now either.
        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed,
            await _engine.CommitAccumulatorsAsync(_calculated[^1].PreparedAccumulatorCommit!));
        await AssertBalances(0m, 0m);
    }

    /// <summary>
    /// The store fence on its own: a commit prepared before a denial and
    /// arriving after it (a resolver's commit in flight when its claim was
    /// denied) is refused inside the versioned write; a commit that lands
    /// first is reversed by the denial. Either order: zero.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommitAndDenial_InEitherOrder_LeaveNothing(bool commitFirst)
    {
        var pended = await SubmitAndAdjudicateAsync();
        var commit = _calculated[0].PreparedAccumulatorCommit!;

        if (commitFirst)
            Assert.Equal(AccumulatorCommitOutcome.Committed, await _engine.CommitAccumulatorsAsync(commit));
        await _engine.ReverseClaimAsync(commit.MemberId, commit.SubscriberId, commit.BenefitPlanId,
            DateOnly.FromDateTime(pended.ServiceDateFrom), pended.Id);
        if (!commitFirst)
            Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, await _engine.CommitAccumulatorsAsync(commit));

        await AssertBalances(0m, 0m);
        Assert.Equal(0m, await Balance(AccumulatorType.FamilyDeductible, AccumulatorScope.Family));
    }

    // ── stages standing in for NCCI and for a stall ───────────────────────

    /// <summary>
    /// The NCCI stage's pend (code NCCI, Order 400 — after benefit
    /// calculation), the same finding on every run, so the examiner's
    /// approval overrides it on the re-run.
    /// </summary>
    private sealed class NcciPendStage : IClaimAdjudicationStage
    {
        public string Name => NcciEditsStage.StageName;
        public int Order => 400;
        public bool IsRequired => false;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
        {
            if (context.PendDetails is null)
                context.PendDetails = new Claims.PendDetails { PendCode = "NCCI", PendReason = NcciReason, PendedAt = DateTime.UtcNow };
            else
                context.PendDetails.AdditionalPendReasons.Add($"NCCI: {NcciReason}");
            return Task.FromResult(ClaimAdjudicationStageResult.Pend(Name, NcciReason));
        }
    }

    /// <summary>Order 280: on an approval re-run, runs the test's hook once (a stall during which other things happen).</summary>
    private sealed class MidRerunStage(Func<Func<Task>?> takeHook) : IClaimAdjudicationStage
    {
        public string Name => "MidRerunStall";
        public int Order => 280;
        public bool IsRequired => false;

        public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
        {
            if (context.ExaminerApproval is not null && takeHook() is { } hook)
                await hook();
            return ClaimAdjudicationStageResult.Pass(Name);
        }
    }

    private sealed class FixedTenant(string tenantId) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = tenantId;
    }
}

/// <summary><see cref="AccumulatorIntegrityTests"/> on MongoDB: <see cref="ClaimRepositoryMongo"/> and <see cref="AccumulatorRepositoryMongo"/>.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorIntegrityMongoTests(MongoRunnerFixture mongo) : AccumulatorIntegrityTests
{
    private IMongoDatabase _db = null!;

    protected override Task<(IClaimRepository Claims, IAccumulatorRepository Accumulators)> CreateStoresAsync(
        AdjudicationTenantContext tenantContext)
    {
        _db = mongo.CreateDatabase("accumulator_integrity");
        return Task.FromResult<(IClaimRepository, IAccumulatorRepository)>((
            new ClaimRepositoryMongo(_db, new HttpContextAccessor(), NullLogger<ClaimRepositoryMongo>.Instance, tenantContext),
            new AccumulatorRepositoryMongo(_db, new ConfigurationBuilder().Build(), NullLogger<AccumulatorRepositoryMongo>.Instance)));
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
/// ETag fences) and the engine's <see cref="AccumulatorRepositoryCosmos"/> (ETag replace).
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class AccumulatorIntegrityCosmosTests(CosmosEmulatorFixture cosmos) : AccumulatorIntegrityTests
{
    private const string ClaimsContainer = "ClaimsV2";
    private Container _claimsContainer = null!;
    private Container _accumulatorContainer = null!;

    protected override async Task<(IClaimRepository Claims, IAccumulatorRepository Accumulators)> CreateStoresAsync(
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
        var accessor = new FixedAccessor(Tenant);
        var claims = new ClaimRepository(claimsClient, claimsConfig, accessor, NullLogger<ClaimRepository>.Instance);

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
        return (claims, new AccumulatorRepositoryCosmos(accumulatorClient, accumulatorConfig, NullLogger<AccumulatorRepositoryCosmos>.Instance));
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

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public FixedAccessor(string tenant)
        {
            HttpContext = new DefaultHttpContext();
            HttpContext.Items["TenantId"] = tenant;
        }

        public HttpContext? HttpContext { get; set; }
    }
}
