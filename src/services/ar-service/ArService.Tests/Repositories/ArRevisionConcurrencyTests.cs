using ArService.Models;
using ArService.Repositories;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ArService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on cash postings and AR adjustments (EphemeralMongo).
/// <c>UpdateAsync</c> replaced the document filtered on Id + TenantId only, so of
/// two apply/void or approve/reject requests that read the same document the
/// later silently overwrote the earlier. A save is now conditional on the
/// <c>Revision</c> it read, and a stale save fails with
/// <see cref="ArConcurrencyException"/> (409).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ArRevisionConcurrencyTests : IAsyncLifetime
{
    private const string Tenant = "tenant-ar-concurrency";
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public ArRevisionConcurrencyTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("ar_revision");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static HttpContextAccessor Accessor()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private MongoCashPostingRepository Postings() =>
        new(_db, Accessor(), NullLogger<MongoCashPostingRepository>.Instance);

    private MongoArAdjustmentRepository Adjustments() =>
        new(_db, Accessor(), NullLogger<MongoArAdjustmentRepository>.Instance);

    private Task<CashPosting> SeedPostingAsync(string id) => Postings().CreateAsync(new CashPosting
    {
        Id = id, PostingNumber = "CP-" + id, Amount = 100m, UnappliedAmount = 100m,
        PayerType = PayerType.Sponsor, PayerReferenceId = "S1", Status = CashPostingStatus.Pending,
        ReceiptDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
    });

    private Task<ArAdjustment> SeedAdjustmentAsync(string id) => Adjustments().CreateAsync(new ArAdjustment
    {
        Id = id, AdjustmentNumber = "ADJ-" + id, Amount = 25m, Direction = ArAdjustmentDirection.Credit,
        GlAccountId = "gl-1200", ArBalanceId = "B1", ReasonCode = "WO", Status = ArAdjustmentStatus.Pending,
        Period = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
    });

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
    public void The_conflict_is_the_shared_type_every_service_answers_with_409()
    {
        new ArConcurrencyException("x").Should().BeAssignableTo<ConcurrencyConflictException>();
    }

    [Fact]
    public async Task Two_concurrent_cash_posting_updates_from_the_same_read_one_wins_the_other_conflicts()
    {
        await SeedPostingAsync("P1");
        var apply = (await Postings().GetByIdAsync("P1"))!;
        var @void = (await Postings().GetByIdAsync("P1"))!;
        apply.Status = CashPostingStatus.Applied;
        apply.AppliedAmount = 100m;
        apply.UnappliedAmount = 0m;
        @void.Status = CashPostingStatus.Voided;

        var outcomes = await Task.WhenAll(
            Try(() => Postings().UpdateAsync(apply)),
            Try(() => Postings().UpdateAsync(@void)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is ArConcurrencyException).Should().Be(1, "the other is a conflict, not a silent overwrite");
        var stored = (await Postings().GetByIdAsync("P1"))!;
        stored.Status.Should().BeOneOf(CashPostingStatus.Applied, CashPostingStatus.Voided);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task A_cash_posting_save_from_a_read_before_an_apply_conflicts_instead_of_undoing_it()
    {
        await SeedPostingAsync("P1");
        var stale = (await Postings().GetByIdAsync("P1"))!;
        var fresh = (await Postings().GetByIdAsync("P1"))!;
        fresh.Status = CashPostingStatus.Applied;
        fresh.AppliedAmount = 100m;
        await Postings().UpdateAsync(fresh);

        stale.Status = CashPostingStatus.Voided;
        var act = () => Postings().UpdateAsync(stale);

        await act.Should().ThrowAsync<ArConcurrencyException>();
        stale.Revision.Should().Be(0, "the in-memory revision is restored when nothing was written");
        var stored = (await Postings().GetByIdAsync("P1"))!;
        stored.Status.Should().Be(CashPostingStatus.Applied);
        stored.AppliedAmount.Should().Be(100m);
    }

    [Fact]
    public async Task A_cash_posting_written_before_the_revision_existed_updates_once_then_carries_one()
    {
        await SeedPostingAsync("P1");
        await _db.GetCollection<BsonDocument>("cash_postings").UpdateOneAsync(
            new BsonDocument("_id", "P1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = (await Postings().GetByIdAsync("P1"))!;
        var legacyToo = (await Postings().GetByIdAsync("P1"))!;
        legacy.Revision.Should().Be(0);
        legacy.Status = CashPostingStatus.PartiallyApplied;

        await Postings().UpdateAsync(legacy);

        (await Postings().GetByIdAsync("P1"))!.Revision.Should().Be(1);
        legacyToo.Status = CashPostingStatus.Voided;
        await ((Func<Task>)(() => Postings().UpdateAsync(legacyToo))).Should().ThrowAsync<ArConcurrencyException>();
    }

    [Fact]
    public async Task Updating_a_missing_cash_posting_is_still_a_silent_no_op_not_a_conflict()
    {
        var ghost = new CashPosting { Id = "NOPE", TenantId = Tenant, PostingNumber = "CP-NOPE" };

        var result = await Postings().UpdateAsync(ghost);

        result.Should().BeSameAs(ghost);
        ghost.Revision.Should().Be(0);
        (await Postings().GetByIdAsync("NOPE")).Should().BeNull();
    }

    [Fact]
    public async Task A_new_cash_posting_starts_at_revision_zero_whatever_the_client_sent()
    {
        var created = await Postings().CreateAsync(new CashPosting { Id = "P9", PostingNumber = "CP-9", Revision = 42 });

        created.Revision.Should().Be(0);
        (await Postings().GetByIdAsync("P9"))!.Revision.Should().Be(0);
    }

    [Fact]
    public async Task Two_concurrent_adjustment_decisions_from_the_same_read_one_wins_the_other_conflicts()
    {
        await SeedAdjustmentAsync("A1");
        var approve = (await Adjustments().GetByIdAsync("A1"))!;
        var reject = (await Adjustments().GetByIdAsync("A1"))!;
        approve.Status = ArAdjustmentStatus.Approved;
        approve.AuthorizedBy = "approver";
        reject.Status = ArAdjustmentStatus.Rejected;

        var outcomes = await Task.WhenAll(
            Try(() => Adjustments().UpdateAsync(approve)),
            Try(() => Adjustments().UpdateAsync(reject)));

        outcomes.Count(o => o is null).Should().Be(1);
        outcomes.Count(o => o is ArConcurrencyException).Should().Be(1);
        var stored = (await Adjustments().GetByIdAsync("A1"))!;
        stored.Status.Should().BeOneOf(ArAdjustmentStatus.Approved, ArAdjustmentStatus.Rejected);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task An_adjustment_written_before_the_revision_existed_updates_once_then_carries_one()
    {
        await SeedAdjustmentAsync("A1");
        await _db.GetCollection<BsonDocument>("ar_adjustments").UpdateOneAsync(
            new BsonDocument("_id", "A1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = (await Adjustments().GetByIdAsync("A1"))!;
        legacy.Status = ArAdjustmentStatus.Approved;

        await Adjustments().UpdateAsync(legacy);

        (await Adjustments().GetByIdAsync("A1"))!.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Updating_a_missing_adjustment_is_still_a_silent_no_op_not_a_conflict()
    {
        var ghost = new ArAdjustment { Id = "NOPE", TenantId = Tenant, AdjustmentNumber = "ADJ-NOPE" };

        var result = await Adjustments().UpdateAsync(ghost);

        result.Should().BeSameAs(ghost);
        (await Adjustments().GetByIdAsync("NOPE")).Should().BeNull();
    }
}
