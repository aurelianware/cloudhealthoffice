using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BenefitPlanService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB benefit-plan version chain
/// (EphemeralMongo). The service checked <see cref="BenefitPlan.VersionState"/>
/// on its read, but every write replaced the row on Id + TenantId only, so two
/// publishes of drafts amending the same Published version both succeeded (two
/// Published heads), an edit read before a publish turned the Published
/// version back into a Draft, and a termination read before a supersede erased
/// it. Every write is now conditional on the state and the
/// <see cref="BenefitPlan.Revision"/> it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class BenefitPlanRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("benefit_plan_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private BenefitPlanRepositoryMongo Repo() => BenefitPlanChain.Repo(_db);

    [Fact]
    public async Task PublishingTheSameDraftTwice_FromTheSameRead_TheSecondConflicts()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var draft = await chain.AmendAsync();
        var (d1, p1) = await chain.ReadForPublishAsync(draft);
        var (d2, p2) = await chain.ReadForPublishAsync(draft);

        await Repo().PublishAndSupersedeAsync(d1, p1);
        var second = () => Repo().PublishAndSupersedeAsync(d2, p2);

        await second.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        (await chain.PublishedVersionIdsAsync()).Should().Equal(draft.VersionId);
    }

    [Fact]
    public async Task TwoDraftsAmendingTheSamePublishedVersion_OnlyOnePublishes()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForPublishAsync(a);
        var (db, pb) = await chain.ReadForPublishAsync(b);

        await Repo().PublishAndSupersedeAsync(da, pa);
        var second = () => Repo().PublishAndSupersedeAsync(db, pb);

        await second.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        (await chain.PublishedVersionIdsAsync()).Should().Equal(new[] { a.VersionId }, "the chain has one Published head");
        (await chain.StoredAsync(b)).VersionState.Should().Be(PlanVersionState.Draft, "the losing publish wrote nothing");
        (await chain.StoredAsync(chain.Genesis)).SupersededByVersionId.Should().Be(a.VersionId);
    }

    [Fact]
    public async Task TwoConcurrentPublishes_ExactlyOneWins()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForPublishAsync(a);
        var (db, pb) = await chain.ReadForPublishAsync(b);

        var outcomes = await Task.WhenAll(
            BenefitPlanChain.Try(() => Repo().PublishAndSupersedeAsync(da, pa)),
            BenefitPlanChain.Try(() => Repo().PublishAndSupersedeAsync(db, pb)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one publish wins");
        outcomes.Count(o => o is BenefitPlanConcurrencyException).Should().Be(1, "the other gets a conflict");
        (await chain.PublishedVersionIdsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task TwoGeneralUpdatesFromTheSameRead_TheSecondConflicts_InsteadOfErasingTheFirst()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var draft = await chain.AmendAsync();
        var a = (await Repo().GetByIdAsync(draft.Id, BenefitPlanChain.Tenant))!;
        var b = (await Repo().GetByIdAsync(draft.Id, BenefitPlanChain.Tenant))!;
        a.PlanName = "update A";
        b.PlanName = "update B";

        await Repo().UpdateAsync(a);
        var second = () => Repo().UpdateAsync(b);

        await second.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        (await chain.StoredAsync(draft)).PlanName.Should().Be("update A");
    }

    [Fact]
    public async Task TwoDraftEditsFromTheSameRead_TheSecondConflicts()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var draft = await chain.AmendAsync();
        var a = (await Repo().GetByIdAsync(draft.Id, BenefitPlanChain.Tenant))!;
        var b = (await Repo().GetByIdAsync(draft.Id, BenefitPlanChain.Tenant))!;
        a.PlanName = "edit A";
        b.PlanName = "edit B";

        await Repo().UpdateDraftAsync(a);
        var second = () => Repo().UpdateDraftAsync(b);

        await second.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        b.Revision.Should().Be(0, "the in-memory revision is restored on a conflict");
        var stored = await chain.StoredAsync(draft);
        stored.PlanName.Should().Be("edit A");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task ATerminationReadBeforeASupersede_Conflicts_InsteadOfErasingTheSupersede()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var staleTerminate = (await Repo().GetVersionAsync(BenefitPlanChain.PlanId, chain.Genesis.VersionId, BenefitPlanChain.Tenant))!;
        var draft = await chain.AmendAsync();
        var (d, p) = await chain.ReadForPublishAsync(draft);
        await Repo().PublishAndSupersedeAsync(d, p);

        BenefitPlanChain.Terminate(staleTerminate);
        var act = () => Repo().TerminateVersionAsync(staleTerminate);

        await act.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        (await chain.StoredAsync(chain.Genesis)).SupersededByVersionId.Should().Be(draft.VersionId, "the supersede is not undone");
    }

    [Fact]
    public async Task ATerminationReadBeforeTheNetworkTierBackfill_Conflicts_InsteadOfErasingTheTiers()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var stale = (await Repo().GetVersionAsync(BenefitPlanChain.PlanId, chain.Genesis.VersionId, BenefitPlanChain.Tenant))!;
        (await Repo().UpdateNetworkTiersAsync(BenefitPlanChain.Tenant, BenefitPlanChain.PlanId,
            new[] { new NetworkTier { TierLevel = 1, TierName = "In", NetworkId = "net-1" } })).Should().BeTrue();

        BenefitPlanChain.Terminate(stale);
        var act = () => Repo().TerminateVersionAsync(stale);

        await act.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        (await chain.StoredAsync(chain.Genesis)).NetworkTiers.Should().ContainSingle(t => t.NetworkId == "net-1");
    }

    [Fact]
    public async Task APublishWhoseDraftWasEditedAfterItsRead_Conflicts_AndLeavesThePredecessorPublished()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var draft = await chain.AmendAsync();
        var (staleDraft, pred) = await chain.ReadForPublishAsync(draft);
        var edit = (await Repo().GetByIdAsync(draft.Id, BenefitPlanChain.Tenant))!;
        edit.PlanName = "edited after the publish read";
        await Repo().UpdateDraftAsync(edit);

        var act = () => Repo().PublishAndSupersedeAsync(staleDraft, pred);

        await act.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        var genesis = await chain.StoredAsync(chain.Genesis);
        genesis.VersionState.Should().Be(PlanVersionState.Published, "a failed publish does not leave the plan without a Published version");
        genesis.SupersededByVersionId.Should().BeNull();
        (await chain.StoredAsync(draft)).VersionState.Should().Be(PlanVersionState.Draft);
        (await chain.PublishedVersionIdsAsync()).Should().Equal(chain.Genesis.VersionId);
    }

    [Fact]
    public async Task AVersionWrittenBeforeTheRevisionExisted_TerminatesOnce_ThenCarriesARevision()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        await _db.GetCollection<BsonDocument>("BenefitPlans").UpdateOneAsync(
            new BsonDocument("_id", chain.Genesis.Id), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = (await Repo().GetVersionAsync(BenefitPlanChain.PlanId, chain.Genesis.VersionId, BenefitPlanChain.Tenant))!;
        legacy.Revision.Should().Be(0);

        BenefitPlanChain.Terminate(legacy);
        (await Repo().TerminateVersionAsync(legacy)).Should().BeTrue();

        (await chain.StoredAsync(chain.Genesis)).Revision.Should().Be(1);
    }

    [Fact]
    public async Task MissingVersions_AreStillNotFound_NotAConflict()
    {
        var ghost = new BenefitPlan
        {
            Id = "NOPE", TenantId = BenefitPlanChain.Tenant, PlanId = BenefitPlanChain.PlanId,
            VersionId = "v-nope", VersionState = PlanVersionState.Draft,
        };

        var draftEdit = () => Repo().UpdateDraftAsync(ghost);
        (await draftEdit.Should().ThrowAsync<PlanVersionStateException>()).Which.IsNotFound.Should().BeTrue();

        ghost.VersionState = PlanVersionState.Published;
        var publish = () => Repo().PublishAndSupersedeAsync(ghost, null);
        (await publish.Should().ThrowAsync<PlanVersionStateException>()).Which.IsNotFound.Should().BeTrue();

        ghost.VersionState = PlanVersionState.Superseded;
        (await Repo().TerminateVersionAsync(ghost)).Should().BeFalse();

        ghost.VersionState = PlanVersionState.Draft;
        await Repo().UpdateAsync(ghost); // a silent no-op, as before
    }
}

/// <summary>
/// The same races on a replica-set mongod, where publish/supersede runs inside
/// a multi-document transaction (the standalone runs above use the
/// sequential fallback).
/// </summary>
public sealed class BenefitPlanRepositoryMongoTransactionConcurrencyTests(BenefitPlanMongoReplicaSetFixture mongo)
    : IClassFixture<BenefitPlanMongoReplicaSetFixture>, IAsyncLifetime
{
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"bp_concurrency_rs_{Guid.NewGuid():N}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    private BenefitPlanRepositoryMongo Repo() => BenefitPlanChain.Repo(_db);

    [Fact]
    public async Task TwoDraftsAmendingTheSamePublishedVersion_OnlyOnePublishes()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForPublishAsync(a);
        var (db, pb) = await chain.ReadForPublishAsync(b);

        await Repo().PublishAndSupersedeAsync(da, pa);
        var second = () => Repo().PublishAndSupersedeAsync(db, pb);

        await second.Should().ThrowAsync<BenefitPlanConcurrencyException>();
        (await chain.PublishedVersionIdsAsync()).Should().Equal(a.VersionId);
        (await chain.StoredAsync(b)).VersionState.Should().Be(PlanVersionState.Draft, "the transaction rolled the draft back");
    }

    [Fact]
    public async Task TwoConcurrentPublishes_ExactlyOneWins()
    {
        var chain = await BenefitPlanChain.SeedAsync(Repo());
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForPublishAsync(a);
        var (db, pb) = await chain.ReadForPublishAsync(b);

        var outcomes = await Task.WhenAll(
            BenefitPlanChain.Try(() => Repo().PublishAndSupersedeAsync(da, pa)),
            BenefitPlanChain.Try(() => Repo().PublishAndSupersedeAsync(db, pb)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one publish wins");
        outcomes.Count(o => o is BenefitPlanConcurrencyException).Should().Be(1, "the other gets a conflict, not a write-conflict 500");
        (await chain.PublishedVersionIdsAsync()).Should().HaveCount(1);
    }
}

/// <summary>A mongod started as a single-node replica set, so multi-document transactions work.</summary>
public sealed class BenefitPlanMongoReplicaSetFixture : IDisposable
{
    private readonly EphemeralMongo.IMongoRunner _runner;

    public BenefitPlanMongoReplicaSetFixture()
    {
        var options = TestMongo.Options();
        options.UseSingleNodeReplicaSet = true;
        _runner = EphemeralMongo.MongoRunner.Run(options);
        Client = new MongoClient(_runner.ConnectionString);
    }

    public IMongoClient Client { get; }

    public void Dispose() => _runner.Dispose();
}

/// <summary>A plan with a Published genesis version, and the service's read-then-mutate steps.</summary>
internal sealed class BenefitPlanChain
{
    public const string Tenant = "tenant-concurrency";
    public const string PlanId = "PLAN-1";

    private readonly BenefitPlanRepositoryMongo _repo;

    private BenefitPlanChain(BenefitPlanRepositoryMongo repo, BenefitPlan genesis)
    {
        _repo = repo;
        Genesis = genesis;
    }

    public BenefitPlan Genesis { get; }

    public static BenefitPlanRepositoryMongo Repo(IMongoDatabase db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<BenefitPlanRepositoryMongo>.Instance);

    public static async Task<BenefitPlanChain> SeedAsync(BenefitPlanRepositoryMongo repo)
    {
        var genesis = await repo.CreateDraftAsync(NewDraft(1, predecessor: null));
        var stored = (await repo.GetVersionAsync(PlanId, genesis.VersionId, Tenant))!;
        MarkPublished(stored);
        await repo.PublishAndSupersedeAsync(stored, null);
        return new BenefitPlanChain(repo, stored);
    }

    /// <summary>A new Draft amending the genesis version (what AmendPublishedPlanAsync stores).</summary>
    public Task<BenefitPlan> AmendAsync() => _repo.CreateDraftAsync(NewDraft(2, Genesis.VersionId));

    /// <summary>What PublishVersionAsync reads and mutates before calling PublishAndSupersedeAsync.</summary>
    public async Task<(BenefitPlan Draft, BenefitPlan Predecessor)> ReadForPublishAsync(BenefitPlan draft)
    {
        var d = (await _repo.GetVersionAsync(PlanId, draft.VersionId, Tenant))!;
        var p = (await _repo.GetLatestPublishedAsync(PlanId, Tenant, DateTime.UtcNow))!;
        MarkPublished(d);
        p.VersionState = PlanVersionState.Superseded;
        p.SupersededAt = DateTime.UtcNow;
        p.SupersededByVersionId = d.VersionId;
        p.IsActive = false;
        return (d, p);
    }

    /// <summary>What SupersedeVersionAsync applies before calling TerminateVersionAsync.</summary>
    public static void Terminate(BenefitPlan v)
    {
        v.VersionState = PlanVersionState.Superseded;
        v.SupersededAt = DateTime.UtcNow;
        v.SupersededByVersionId = null;
        v.IsActive = false;
        v.TerminationDate = DateTime.UtcNow;
    }

    public async Task<BenefitPlan> StoredAsync(BenefitPlan v) => (await _repo.GetByIdAsync(v.Id, Tenant))!;

    public async Task<IReadOnlyList<string>> PublishedVersionIdsAsync()
    {
        var (items, _) = await _repo.ListVersionsAsync(PlanId, Tenant, 50, null);
        return items.Where(v => v.VersionState == PlanVersionState.Published).Select(v => v.VersionId).ToList();
    }

    public static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void MarkPublished(BenefitPlan d)
    {
        d.VersionState = PlanVersionState.Published;
        d.PublishedAt = DateTime.UtcNow;
        d.PublishedBy = "tester";
        d.IsActive = true;
    }

    private static BenefitPlan NewDraft(int number, string? predecessor) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        PlanId = PlanId,
        PlanName = "Plan",
        Payer = "Acme",
        EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        PlanType = PlanType.HMO,
        LineOfBusiness = LineOfBusiness.Commercial,
        VersionId = PlanVersionId.NewId(),
        VersionNumber = number,
        VersionState = PlanVersionState.Draft,
        PredecessorVersionId = predecessor,
    };
}
