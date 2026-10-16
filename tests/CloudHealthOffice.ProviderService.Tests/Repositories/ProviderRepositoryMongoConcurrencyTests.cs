using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB provider version chain
/// (EphemeralMongo). The service checked <see cref="Provider.VersionState"/>
/// on its read, but every write replaced the row on Id + TenantId only, so two
/// activations of drafts amending the same head both succeeded (two Active
/// heads), two draft edits overwrote each other, and a suspension or
/// termination read before a supersede or a projection patch erased it. Every
/// write is now conditional on the state and the <see cref="Provider.Revision"/>
/// it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ProviderRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private ProviderRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("provider_concurrency");
        _repo = ProviderChain.Repo(_db);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    [Fact]
    public async Task ActivatingTheSameDraftTwice_FromTheSameRead_TheSecondConflicts()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var draft = await chain.AmendAsync();
        var (d1, p1) = await chain.ReadForActivateAsync(draft);
        var (d2, p2) = await chain.ReadForActivateAsync(draft);

        await _repo.ActivateAndSupersedeAsync(d1, p1);
        var second = () => _repo.ActivateAndSupersedeAsync(d2, p2);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        (await chain.ActiveVersionIdsAsync()).Should().Equal(draft.VersionId);
    }

    [Fact]
    public async Task TwoDraftsAmendingTheSameHead_OnlyOneActivates()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForActivateAsync(a);
        var (db, pb) = await chain.ReadForActivateAsync(b);

        await _repo.ActivateAndSupersedeAsync(da, pa);
        var second = () => _repo.ActivateAndSupersedeAsync(db, pb);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        (await chain.ActiveVersionIdsAsync()).Should().Equal(new[] { a.VersionId }, "the chain has one Active head");
        (await chain.StoredAsync(b)).VersionState.Should().Be(ProviderVersionState.Draft, "the losing activation wrote nothing");
        (await chain.StoredAsync(chain.Genesis)).SupersededByVersionId.Should().Be(a.VersionId);
    }

    [Fact]
    public async Task TwoConcurrentActivations_ExactlyOneWins()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForActivateAsync(a);
        var (db, pb) = await chain.ReadForActivateAsync(b);

        var outcomes = await Task.WhenAll(
            ProviderChain.Try(() => _repo.ActivateAndSupersedeAsync(da, pa)),
            ProviderChain.Try(() => _repo.ActivateAndSupersedeAsync(db, pb)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one activation wins");
        outcomes.Count(o => o is ProviderConcurrencyException).Should().Be(1, "the other gets a conflict");
        (await chain.ActiveVersionIdsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task TwoDraftUpdatesFromTheSameRead_TheSecondConflicts()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var draft = await chain.AmendAsync();
        var a = (await _repo.GetVersionAsync(ProviderChain.ProviderId, draft.VersionId))!;
        var b = (await _repo.GetVersionAsync(ProviderChain.ProviderId, draft.VersionId))!;
        a.PrimarySpecialty = "edit A";
        b.PrimarySpecialty = "edit B";

        await _repo.UpdateAsync(a);
        var second = () => _repo.UpdateAsync(b);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        b.Revision.Should().Be(0, "the in-memory revision is restored on a conflict");
        (await chain.StoredAsync(draft)).PrimarySpecialty.Should().Be("edit A");
    }

    [Fact]
    public async Task TwoUpdateDraftCallsFromTheSameRead_TheSecondConflicts()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var draft = await chain.AmendAsync();
        var a = (await _repo.GetVersionAsync(ProviderChain.ProviderId, draft.VersionId))!;
        var b = (await _repo.GetVersionAsync(ProviderChain.ProviderId, draft.VersionId))!;
        a.PrimarySpecialty = "edit A";
        b.PrimarySpecialty = "edit B";

        await _repo.UpdateDraftAsync(a);
        var second = () => _repo.UpdateDraftAsync(b);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        var stored = await chain.StoredAsync(draft);
        stored.PrimarySpecialty.Should().Be("edit A");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task ASuspensionReadBeforeASupersede_Conflicts_InsteadOfErasingTheSupersede()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var staleSuspend = (await _repo.GetVersionAsync(ProviderChain.ProviderId, chain.Genesis.VersionId))!;
        var draft = await chain.AmendAsync();
        var (d, p) = await chain.ReadForActivateAsync(draft);
        await _repo.ActivateAndSupersedeAsync(d, p);

        staleSuspend.VersionState = ProviderVersionState.Suspended;
        staleSuspend.SuspendedAt = DateTime.UtcNow;
        staleSuspend.Status = ProviderStatus.Inactive;
        var act = () => _repo.ReplaceVersionRowAsync(staleSuspend);

        await act.Should().ThrowAsync<ProviderConcurrencyException>();
        var genesis = await chain.StoredAsync(chain.Genesis);
        genesis.VersionState.Should().Be(ProviderVersionState.Superseded, "the supersede is not undone");
        genesis.SupersededByVersionId.Should().Be(draft.VersionId);
    }

    [Fact]
    public async Task ATerminationReadBeforeAnIntegrityPatch_Conflicts_InsteadOfErasingTheProjection()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var stale = (await _repo.GetVersionAsync(ProviderChain.ProviderId, chain.Genesis.VersionId))!;
        (await _repo.UpdateIntegrityProjectionAsync(ProviderChain.Tenant, ProviderChain.ProviderId, 87, "A",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30))).Should().BeTrue();

        stale.VersionState = ProviderVersionState.Terminated;
        stale.TerminationDate = DateTime.UtcNow;
        stale.Status = ProviderStatus.Terminated;
        var act = () => _repo.ReplaceVersionRowAsync(stale);

        await act.Should().ThrowAsync<ProviderConcurrencyException>();
        (await chain.StoredAsync(chain.Genesis)).IntegrityScore.Should().Be(87);
    }

    [Fact]
    public async Task AnActivationWhoseDraftWasEditedAfterItsRead_Conflicts_AndLeavesTheHeadActive()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var draft = await chain.AmendAsync();
        var (staleDraft, pred) = await chain.ReadForActivateAsync(draft);
        var edit = (await _repo.GetVersionAsync(ProviderChain.ProviderId, draft.VersionId))!;
        edit.PrimarySpecialty = "edited after the activation read";
        await _repo.UpdateDraftAsync(edit);

        var act = () => _repo.ActivateAndSupersedeAsync(staleDraft, pred);

        await act.Should().ThrowAsync<ProviderConcurrencyException>();
        var genesis = await chain.StoredAsync(chain.Genesis);
        genesis.VersionState.Should().Be(ProviderVersionState.Active, "a failed activation does not leave the chain without a head");
        genesis.SupersededByVersionId.Should().BeNull();
        (await chain.ActiveVersionIdsAsync()).Should().Equal(chain.Genesis.VersionId);
    }

    [Fact]
    public async Task AVersionWrittenBeforeTheRevisionExisted_SuspendsOnce_ThenCarriesARevision()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        await _db.GetCollection<BsonDocument>("Providers").UpdateOneAsync(
            new BsonDocument("_id", chain.Genesis.Id), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = (await _repo.GetVersionAsync(ProviderChain.ProviderId, chain.Genesis.VersionId))!;
        legacy.Revision.Should().Be(0);

        legacy.VersionState = ProviderVersionState.Suspended;
        await _repo.ReplaceVersionRowAsync(legacy);

        (await chain.StoredAsync(chain.Genesis)).Revision.Should().Be(1);
    }

    [Fact]
    public async Task MissingVersions_AreStillNotFound_NotAConflict()
    {
        var ghost = ProviderChain.NewDraft(1, predecessor: null);

        var update = () => _repo.UpdateAsync(ghost);
        var thrown = (await update.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().NotBeOfType<ProviderConcurrencyException>();
        thrown.Message.Should().Contain("not found");

        var draftEdit = () => _repo.UpdateDraftAsync(ghost);
        (await draftEdit.Should().ThrowAsync<ProviderVersionStateException>()).Which.IsNotFound.Should().BeTrue();

        ghost.VersionState = ProviderVersionState.Active;
        var activate = () => _repo.ActivateAndSupersedeAsync(ghost, null);
        (await activate.Should().ThrowAsync<ProviderVersionStateException>()).Which.IsNotFound.Should().BeTrue();

        ghost.VersionState = ProviderVersionState.Suspended;
        await _repo.ReplaceVersionRowAsync(ghost); // a silent no-op, as before
    }
}

/// <summary>
/// The activation races on a replica-set mongod, where activate/supersede
/// runs inside a multi-document transaction (the standalone runs above use the
/// sequential fallback).
/// </summary>
public sealed class ProviderRepositoryMongoTransactionConcurrencyTests(ProviderMongoReplicaSetFixture mongo)
    : IClassFixture<ProviderMongoReplicaSetFixture>, IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private ProviderRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"provider_concurrency_rs_{Guid.NewGuid():N}");
        _repo = ProviderChain.Repo(_db);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    [Fact]
    public async Task TwoDraftsAmendingTheSameHead_OnlyOneActivates()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForActivateAsync(a);
        var (db, pb) = await chain.ReadForActivateAsync(b);

        await _repo.ActivateAndSupersedeAsync(da, pa);
        var second = () => _repo.ActivateAndSupersedeAsync(db, pb);

        await second.Should().ThrowAsync<ProviderConcurrencyException>();
        (await chain.ActiveVersionIdsAsync()).Should().Equal(a.VersionId);
        (await chain.StoredAsync(b)).VersionState.Should().Be(ProviderVersionState.Draft, "the transaction rolled the draft back");
    }

    [Fact]
    public async Task TwoConcurrentActivations_ExactlyOneWins()
    {
        var chain = await ProviderChain.SeedAsync(_repo);
        var a = await chain.AmendAsync();
        var b = await chain.AmendAsync();
        var (da, pa) = await chain.ReadForActivateAsync(a);
        var (db, pb) = await chain.ReadForActivateAsync(b);

        var outcomes = await Task.WhenAll(
            ProviderChain.Try(() => _repo.ActivateAndSupersedeAsync(da, pa)),
            ProviderChain.Try(() => _repo.ActivateAndSupersedeAsync(db, pb)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one activation wins");
        outcomes.Count(o => o is ProviderConcurrencyException).Should().Be(1, "the other gets a conflict, not a write-conflict 500");
        (await chain.ActiveVersionIdsAsync()).Should().HaveCount(1);
    }
}

/// <summary>A mongod started as a single-node replica set, so multi-document transactions work.</summary>
public sealed class ProviderMongoReplicaSetFixture : IDisposable
{
    private readonly EphemeralMongo.IMongoRunner _runner;

    public ProviderMongoReplicaSetFixture()
    {
        var options = TestMongo.Options();
        options.UseSingleNodeReplicaSet = true;
        _runner = EphemeralMongo.MongoRunner.Run(options);
        Client = new MongoClient(_runner.ConnectionString);
    }

    public IMongoClient Client { get; }

    public void Dispose() => _runner.Dispose();
}

/// <summary>A provider with an Active genesis version, and the service's read-then-mutate steps.</summary>
internal sealed class ProviderChain
{
    public const string Tenant = "tenant-concurrency";
    public const string ProviderId = "PRV-1";

    private readonly ProviderRepositoryMongo _repo;

    private ProviderChain(ProviderRepositoryMongo repo, Provider genesis)
    {
        _repo = repo;
        Genesis = genesis;
    }

    public Provider Genesis { get; }

    public static ProviderRepositoryMongo Repo(IMongoDatabase db)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new ProviderRepositoryMongo(db, new HttpContextAccessor { HttpContext = ctx }, NullLogger<ProviderRepositoryMongo>.Instance);
    }

    public static async Task<ProviderChain> SeedAsync(ProviderRepositoryMongo repo)
    {
        var genesis = await repo.CreateDraftAsync(NewDraft(1, predecessor: null));
        var stored = (await repo.GetVersionAsync(ProviderId, genesis.VersionId))!;
        MarkActive(stored);
        await repo.ActivateAndSupersedeAsync(stored, null);
        return new ProviderChain(repo, stored);
    }

    /// <summary>A new Draft amending the genesis version (what AmendActiveProviderAsync stores).</summary>
    public Task<Provider> AmendAsync() => _repo.CreateDraftAsync(NewDraft(2, Genesis.VersionId));

    /// <summary>What ActivateVersionAsync reads and mutates before calling ActivateAndSupersedeAsync.</summary>
    public async Task<(Provider Draft, Provider Predecessor)> ReadForActivateAsync(Provider draft)
    {
        var d = (await _repo.GetVersionAsync(ProviderId, draft.VersionId))!;
        var p = (await _repo.GetVersionAsync(ProviderId, Genesis.VersionId))!;
        MarkActive(d);
        p.VersionState = ProviderVersionState.Superseded;
        p.SupersededAt = DateTime.UtcNow;
        p.SupersededByVersionId = d.VersionId;
        p.Status = ProviderStatus.Inactive;
        return (d, p);
    }

    public async Task<Provider> StoredAsync(Provider v) => (await _repo.GetVersionAsync(ProviderId, v.VersionId))!;

    public async Task<IReadOnlyList<string>> ActiveVersionIdsAsync()
    {
        var (items, _) = await _repo.ListVersionsAsync(ProviderId, 50, null);
        return items.Where(v => v.VersionState == ProviderVersionState.Active).Select(v => v.VersionId).ToList();
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

    private static void MarkActive(Provider d)
    {
        d.VersionState = ProviderVersionState.Active;
        d.ActivatedAt = DateTime.UtcNow;
        d.ActivatedBy = "tester";
        d.Status = ProviderStatus.Active;
    }

    public static Provider NewDraft(int number, string? predecessor) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ProviderId = ProviderId,
        TenantId = Tenant,
        NPI = "1234567890",
        ProviderType = ProviderType.Individual,
        FirstName = "Jane",
        LastName = "Doe",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X",
        VersionId = ProviderVersionId.NewId(),
        VersionNumber = number,
        VersionState = ProviderVersionState.Draft,
        PredecessorVersionId = predecessor,
        Status = ProviderStatus.Pending,
    };
}
