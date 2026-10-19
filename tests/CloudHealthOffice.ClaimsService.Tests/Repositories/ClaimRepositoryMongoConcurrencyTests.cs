using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB claim repository (EphemeralMongo).
/// <c>UpdateAsync</c> read the claim, checked it was not final, then replaced
/// it unconditionally, so of two concurrent updates the later silently
/// erased the earlier, and a stale replace could undo a status patch. Every
/// write now bumps <see cref="Claim.Revision"/>, and a replace is conditional
/// on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("claim_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private ClaimRepositoryMongo Repo()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new ClaimRepositoryMongo(_db, new HttpContextAccessor { HttpContext = ctx }, NullLogger<ClaimRepositoryMongo>.Instance);
    }

    private async Task<Claim> SeedAsync(string id) => await Repo().CreateAsync(new Claim
    {
        Id = id, ClaimVersionId = id, TenantId = Tenant, ClaimNumber = "CN-" + id,
        MemberId = "M1", BillingProviderNPI = "1234567890", Status = ClaimStatus.Submitted,
        VersionState = ClaimRepository.MapStatusToVersionState(ClaimStatus.Submitted),
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
    });

    private async Task<Claim> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

    [Fact]
    public async Task TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("C1");
        var a = await ReadAsync("C1");
        var b = await ReadAsync("C1");
        a.ClaimNotes = "examiner A";
        b.ClaimNotes = "examiner B";

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is ClaimConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        var stored = await ReadAsync("C1");
        stored.ClaimNotes.Should().BeOneOf("examiner A", "examiner B");
        stored.Revision.Should().Be(1);
    }

    private static async Task<Exception?> Try(Func<Task> write)
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

    [Fact]
    public async Task AReplaceBasedOnAReadBeforeAStatusPatch_Conflicts_InsteadOfUndoingIt()
    {
        await SeedAsync("C1");
        var stale = await ReadAsync("C1");

        await Repo().TryTransitionStatusAsync(Tenant, "C1", ClaimStatus.Received, CancellationToken.None);
        stale.ClaimNotes = "edited from a stale read";

        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<ClaimConcurrencyException>();
        var stored = await ReadAsync("C1");
        stored.Status.Should().Be(ClaimStatus.Received, "the status patch is not undone");
        stored.ClaimNotes.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ARereadAfterTheConflict_Succeeds()
    {
        await SeedAsync("C1");
        var stale = await ReadAsync("C1");
        var fresh = await ReadAsync("C1");
        fresh.ClaimNotes = "first";
        await Repo().UpdateAsync(fresh);
        stale.ClaimNotes = "second";
        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<ClaimConcurrencyException>();

        var reread = await ReadAsync("C1");
        reread.ClaimNotes = "second";
        await Repo().UpdateAsync(reread);

        (await ReadAsync("C1")).ClaimNotes.Should().Be("second");
    }

    [Fact]
    public async Task AClaimWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("C1");
        await _db.GetCollection<BsonDocument>("Claims").UpdateOneAsync(
            new BsonDocument("_id", "C1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("C1");
        legacy.Revision.Should().Be(0);
        legacy.ClaimNotes = "first write after the upgrade";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync("C1")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task EveryTargetedWrite_BumpsTheRevision()
    {
        await SeedAsync("C1");
        var before = (await ReadAsync("C1")).Revision;

        await Repo().TryTransitionStatusAsync(Tenant, "C1", ClaimStatus.Received, CancellationToken.None);

        (await ReadAsync("C1")).Revision.Should().Be(before + 1);
    }

    [Fact]
    public async Task UpdatingAMissingClaim_IsStillNotFound_NotAConflict()
    {
        var ghost = new Claim { Id = "NOPE", ClaimVersionId = "NOPE", TenantId = Tenant, Status = ClaimStatus.Submitted };

        var act = () => Repo().UpdateAsync(ghost);

        (await act.Should().ThrowAsync<ClaimVersionStateException>()).Which.IsNotFound.Should().BeTrue();
    }
}
