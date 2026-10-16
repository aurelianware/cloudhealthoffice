using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB remittance batch repository
/// (EphemeralMongo). <c>UpdateAsync</c> replaced the batch unconditionally, so
/// when two uploads of the same payment ran at once (the second resumes a batch
/// it finds Processing), the one that saved last erased the other's recorded
/// applications, or wrote a Processing progress save over the Completed batch.
/// Every write now bumps <see cref="RemittanceBatch.Revision"/> and a replace is
/// conditional on it.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class RemittanceBatchRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-remittance-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("pb_remittance_batch_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    /// <summary>One request's context (not <see cref="HttpContextAccessor"/>, whose AsyncLocal is shared across concurrent flows).</summary>
    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IHttpContextAccessor Request()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new RequestContext { HttpContext = http };
    }

    private RemittanceBatchRepositoryMongo Repo() => new(_db, Request());

    private async Task SeedAsync(string id = "rmt-1")
    {
        (await Repo().TryCreateAsync(new RemittanceBatch
        {
            Id = id,
            Source = RemittanceSource.X12820,
            TraceNumber = "TRN-1",
            PayerId = "ACME",
            PaymentAmount = 300m,
            PaymentDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = RemittanceBatchStatus.Processing,
        })).Should().BeTrue();
    }

    private async Task<RemittanceBatch> ReadAsync(string id = "rmt-1") => (await Repo().GetByIdAsync(id))!;

    private static RemittanceApplication Line(int n) => new() { LineNumber = n, PaidAmount = 100m, AppliedAmount = 100m };

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
    public async Task A_resumed_upload_from_a_stale_read_cannot_turn_a_completed_batch_back_to_processing()
    {
        await SeedAsync();
        var resumer = await ReadAsync();
        var first = await ReadAsync();
        first.Applications.AddRange(new[] { Line(1), Line(2), Line(3) });
        first.Status = RemittanceBatchStatus.Completed;
        first.CompletedAt = DateTime.UtcNow;
        await Repo().UpdateAsync(first);

        resumer.Applications.Add(Line(1));
        var act = () => Repo().UpdateAsync(resumer);

        (await act.Should().ThrowAsync<RemittanceBatchConcurrencyException>())
            .Which.Should().BeAssignableTo<CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException>();
        resumer.Revision.Should().Be(0, "a failed save leaves the read revision");
        var stored = await ReadAsync();
        stored.Status.Should().Be(RemittanceBatchStatus.Completed);
        stored.Applications.Select(a => a.LineNumber).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Two_concurrent_saves_from_the_same_read_one_wins_the_other_conflicts()
    {
        await SeedAsync();
        var a = await ReadAsync();
        var b = await ReadAsync();
        a.Applications.Add(Line(1));
        b.Applications.Add(Line(2));

        var outcomes = await Task.WhenAll(Try(() => Repo().UpdateAsync(a)), Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.OfType<RemittanceBatchConcurrencyException>().Should().ContainSingle("the other gets a conflict, not a silent overwrite");
        (await ReadAsync()).Revision.Should().Be(1);
    }

    [Fact]
    public async Task One_upload_saving_progress_item_by_item_tracks_its_own_revision()
    {
        await SeedAsync();
        var batch = await ReadAsync();
        var repo = Repo();
        for (var n = 1; n <= 3; n++)
        {
            batch.Applications.Add(Line(n));
            await repo.UpdateAsync(batch);
        }
        batch.Status = RemittanceBatchStatus.Completed;
        await repo.UpdateAsync(batch);

        var stored = await ReadAsync();
        stored.Revision.Should().Be(4);
        stored.Applications.Should().HaveCount(3);
        stored.Status.Should().Be(RemittanceBatchStatus.Completed);
    }

    [Fact]
    public async Task A_batch_written_before_the_revision_existed_updates_once_then_carries_one()
    {
        await SeedAsync();
        await _db.GetCollection<BsonDocument>("RemittanceBatches").UpdateOneAsync(
            new BsonDocument("_id", "rmt-1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync();
        var legacyToo = await ReadAsync();
        legacy.Revision.Should().Be(0);
        legacy.Applications.Add(Line(1));

        await Repo().UpdateAsync(legacy);

        (await ReadAsync()).Revision.Should().Be(1);
        await ((Func<Task>)(() => Repo().UpdateAsync(legacyToo))).Should().ThrowAsync<RemittanceBatchConcurrencyException>();
    }

    [Fact]
    public async Task Updating_a_missing_batch_is_still_not_written_and_does_not_conflict()
    {
        var ghost = new RemittanceBatch { Id = "rmt-none", TenantId = Tenant };

        var act = () => Repo().UpdateAsync(ghost);

        await act.Should().NotThrowAsync();
        (await Repo().GetByIdAsync("rmt-none")).Should().BeNull();
    }
}
