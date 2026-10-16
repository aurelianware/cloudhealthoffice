using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB organization (network) version chain
/// (EphemeralMongo). Every write replaced the row on Id + TenantId only, so two
/// updates of the same organization (each a new draft superseding the same
/// head) both succeeded (two Active heads), two draft edits overwrote each
/// other, and a termination read before an update erased the supersede. Every
/// write is now conditional on the state and the
/// <see cref="Organization.Revision"/> it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class OrganizationRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private OrganizationRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("organization_concurrency");
        _repo = OrganizationChain.Repo(_db);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    [Fact]
    public async Task TwoUpdatesSupersedingTheSameHead_OnlyOneActivates()
    {
        var chain = await OrganizationChain.SeedAsync(_repo);
        var (da, pa) = await chain.PrepareUpdateAsync("update A");
        var (db, pb) = await chain.PrepareUpdateAsync("update B");

        await _repo.ActivateAndSupersedeAsync(da, pa);
        var second = () => _repo.ActivateAndSupersedeAsync(db, pb);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        (await chain.ActiveVersionIdsAsync()).Should().Equal(new[] { da.VersionId }, "the chain has one Active head");
        (await chain.StoredAsync(db)).VersionState.Should().Be(OrganizationVersionState.Draft, "the losing update activated nothing");
        (await chain.StoredAsync(chain.Genesis)).SupersededByVersionId.Should().Be(da.VersionId);
    }

    [Fact]
    public async Task TwoConcurrentUpdates_ExactlyOneWins()
    {
        var chain = await OrganizationChain.SeedAsync(_repo);
        var (da, pa) = await chain.PrepareUpdateAsync("update A");
        var (db, pb) = await chain.PrepareUpdateAsync("update B");

        var outcomes = await Task.WhenAll(
            OrganizationChain.Try(() => _repo.ActivateAndSupersedeAsync(da, pa)),
            OrganizationChain.Try(() => _repo.ActivateAndSupersedeAsync(db, pb)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one update wins");
        outcomes.Count(o => o is ProviderConcurrencyException).Should().Be(1, "the other gets a conflict");
        (await chain.ActiveVersionIdsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task ATerminationReadBeforeAnUpdate_Conflicts_InsteadOfErasingTheSupersede()
    {
        var chain = await OrganizationChain.SeedAsync(_repo);
        var staleTerminate = (await _repo.GetByIdAsync(OrganizationChain.OrganizationId))!;
        var (d, p) = await chain.PrepareUpdateAsync("update");
        await _repo.ActivateAndSupersedeAsync(d, p);

        staleTerminate.VersionState = OrganizationVersionState.Terminated;
        staleTerminate.TerminationDate = DateTime.UtcNow;
        staleTerminate.Status = OrganizationStatus.Terminated;
        var act = () => _repo.ReplaceVersionRowAsync(staleTerminate);

        await act.Should().ThrowAsync<ProviderConcurrencyException>();
        var genesis = await chain.StoredAsync(chain.Genesis);
        genesis.VersionState.Should().Be(OrganizationVersionState.Superseded, "the supersede is not undone");
        genesis.SupersededByVersionId.Should().Be(d.VersionId);
    }

    [Fact]
    public async Task TwoDraftEditsFromTheSameRead_TheSecondConflicts()
    {
        var chain = await OrganizationChain.SeedAsync(_repo);
        var draft = await _repo.CreateDraftAsync(OrganizationChain.NewDraft(2, chain.Genesis.VersionId, "draft"));
        var a = (await _repo.GetVersionAsync(OrganizationChain.OrganizationId, draft.VersionId))!;
        var b = (await _repo.GetVersionAsync(OrganizationChain.OrganizationId, draft.VersionId))!;
        a.Name = "edit A";
        b.Name = "edit B";

        await _repo.UpdateDraftAsync(a);
        var second = () => _repo.UpdateDraftAsync(b);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        b.Revision.Should().Be(0, "the in-memory revision is restored on a conflict");
        var stored = await chain.StoredAsync(draft);
        stored.Name.Should().Be("edit A");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task AVersionWrittenBeforeTheRevisionExisted_TerminatesOnce_ThenCarriesARevision()
    {
        var chain = await OrganizationChain.SeedAsync(_repo);
        await _db.GetCollection<BsonDocument>("Organizations").UpdateOneAsync(
            new BsonDocument("_id", chain.Genesis.Id), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var service = new OrganizationService(_repo, NullLogger<OrganizationService>.Instance);

        var terminated = await service.TerminateAsync(OrganizationChain.OrganizationId, "closed", "tester");

        terminated.Revision.Should().Be(1);
        (await chain.StoredAsync(chain.Genesis)).VersionState.Should().Be(OrganizationVersionState.Terminated);
    }

    [Fact]
    public async Task MissingVersions_AreStillNotFound_NotAConflict()
    {
        var ghost = OrganizationChain.NewDraft(1, predecessor: null, "ghost");

        var draftEdit = () => _repo.UpdateDraftAsync(ghost);
        (await draftEdit.Should().ThrowAsync<OrganizationVersionStateException>()).Which.IsNotFound.Should().BeTrue();

        ghost.VersionState = OrganizationVersionState.Active;
        var activate = () => _repo.ActivateAndSupersedeAsync(ghost, null);
        (await activate.Should().ThrowAsync<OrganizationVersionStateException>()).Which.IsNotFound.Should().BeTrue();

        ghost.VersionState = OrganizationVersionState.Terminated;
        await _repo.ReplaceVersionRowAsync(ghost); // a silent no-op, as before
    }
}

/// <summary>
/// The update race on a replica-set mongod, where activate/supersede runs
/// inside a multi-document transaction.
/// </summary>
public sealed class OrganizationRepositoryMongoTransactionConcurrencyTests(ProviderMongoReplicaSetFixture mongo)
    : IClassFixture<ProviderMongoReplicaSetFixture>, IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private OrganizationRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"org_concurrency_rs_{Guid.NewGuid():N}");
        _repo = OrganizationChain.Repo(_db);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    [Fact]
    public async Task TwoUpdatesSupersedingTheSameHead_OnlyOneActivates()
    {
        var chain = await OrganizationChain.SeedAsync(_repo);
        var (da, pa) = await chain.PrepareUpdateAsync("update A");
        var (db, pb) = await chain.PrepareUpdateAsync("update B");

        await _repo.ActivateAndSupersedeAsync(da, pa);
        var second = () => _repo.ActivateAndSupersedeAsync(db, pb);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        (await chain.ActiveVersionIdsAsync()).Should().Equal(da.VersionId);
        (await chain.StoredAsync(db)).VersionState.Should().Be(OrganizationVersionState.Draft, "the transaction rolled the draft back");
    }

    [Fact]
    public async Task TwoConcurrentUpdates_ExactlyOneWins()
    {
        var chain = await OrganizationChain.SeedAsync(_repo);
        var (da, pa) = await chain.PrepareUpdateAsync("update A");
        var (db, pb) = await chain.PrepareUpdateAsync("update B");

        var outcomes = await Task.WhenAll(
            OrganizationChain.Try(() => _repo.ActivateAndSupersedeAsync(da, pa)),
            OrganizationChain.Try(() => _repo.ActivateAndSupersedeAsync(db, pb)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one update wins");
        outcomes.Count(o => o is ProviderConcurrencyException).Should().Be(1, "the other gets a conflict, not a write-conflict 500");
        (await chain.ActiveVersionIdsAsync()).Should().HaveCount(1);
    }
}

/// <summary>An organization with an Active genesis version, and OrganizationService's read-then-mutate steps.</summary>
internal sealed class OrganizationChain
{
    public const string Tenant = "tenant-concurrency";
    public const string OrganizationId = "ORG-1";

    private readonly OrganizationRepositoryMongo _repo;

    private OrganizationChain(OrganizationRepositoryMongo repo, Organization genesis)
    {
        _repo = repo;
        Genesis = genesis;
    }

    public Organization Genesis { get; }

    public static OrganizationRepositoryMongo Repo(IMongoDatabase db)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new OrganizationRepositoryMongo(db, new HttpContextAccessor { HttpContext = ctx }, NullLogger<OrganizationRepositoryMongo>.Instance);
    }

    public static async Task<OrganizationChain> SeedAsync(OrganizationRepositoryMongo repo)
    {
        var service = new OrganizationService(repo, NullLogger<OrganizationService>.Instance);
        var genesis = await service.CreateAndActivateAsync(NewDraft(1, predecessor: null, "Network"), "tester");
        return new OrganizationChain(repo, genesis);
    }

    /// <summary>What OrganizationService.UpdateAsync reads, stores and mutates before calling ActivateAndSupersedeAsync.</summary>
    public async Task<(Organization Draft, Organization Predecessor)> PrepareUpdateAsync(string name)
    {
        var current = (await _repo.GetByIdAsync(OrganizationId))!;
        var stored = await _repo.CreateDraftAsync(NewDraft(current.VersionNumber + 1, current.VersionId, name));
        var now = DateTime.UtcNow;
        stored.VersionState = OrganizationVersionState.Active;
        stored.ActivatedAt = now;
        stored.Status = OrganizationStatus.Active;
        current.VersionState = OrganizationVersionState.Superseded;
        current.SupersededAt = now;
        current.SupersededByVersionId = stored.VersionId;
        current.Status = OrganizationStatus.Inactive;
        return (stored, current);
    }

    public async Task<Organization> StoredAsync(Organization v) => (await _repo.GetVersionAsync(OrganizationId, v.VersionId))!;

    public async Task<IReadOnlyList<string>> ActiveVersionIdsAsync()
    {
        var (items, _) = await _repo.ListVersionsAsync(OrganizationId, 50, null);
        return items.Where(v => v.VersionState == OrganizationVersionState.Active).Select(v => v.VersionId).ToList();
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

    public static Organization NewDraft(int number, string? predecessor, string name) => new()
    {
        Id = number == 1 && predecessor == null ? OrganizationId : Guid.NewGuid().ToString(),
        OrganizationId = OrganizationId,
        TenantId = Tenant,
        Name = name,
        NetworkType = NetworkType.Unknown,
        EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        VersionId = OrganizationVersionId.NewId(),
        VersionNumber = number,
        VersionState = OrganizationVersionState.Draft,
        PredecessorVersionId = predecessor,
        Status = OrganizationStatus.Pending,
    };
}
