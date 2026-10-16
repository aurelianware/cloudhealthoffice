using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;
using ArService.Tests.Support;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace ArService.Tests.Controllers;

/// <summary>
/// Posting and reversing an AR adjustment saves the balance, then the
/// adjustment. When the second save failed (crash, 409, timeout) the
/// adjustment stayed Approved (or Posted) while the balance already held its
/// entry, and a retry added a second entry with a fresh GUID, so the balance
/// counted the adjustment twice. Each adjustment now has one fixed entry id
/// per direction, and a retry that finds it only saves the adjustment.
/// Real repositories on EphemeralMongo.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ArAdjustmentPostingIdempotencyTests : IAsyncLifetime
{
    private const string Tenant = "tenant-ar-idempotency";
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public ArAdjustmentPostingIdempotencyTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("ar_adjustment_idempotency");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static HttpContextAccessor Accessor()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private MongoArBalanceRepository Balances() =>
        new(_db, Accessor(), NullLogger<MongoArBalanceRepository>.Instance);

    private MongoArAdjustmentRepository Adjustments() =>
        new(_db, Accessor(), NullLogger<MongoArAdjustmentRepository>.Instance);

    private ArAdjustmentsController Controller(IArAdjustmentRepository adjustments) =>
        new(adjustments, Balances(), new TestActor("poster"), NullLogger<ArAdjustmentsController>.Instance);

    private async Task SeedAsync(ArAdjustmentStatus status)
    {
        await Balances().CreateAsync(new ArBalance
        {
            Id = "B1", TenantId = Tenant, GlAccountId = "gl-1200", AccountNumber = "1200",
            Period = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), OpeningBalance = 1000m,
            ClosingBalance = 1000m,
        });
        await Adjustments().CreateAsync(new ArAdjustment
        {
            Id = "A1", AdjustmentNumber = "ADJ-A1", Amount = 25m, Direction = ArAdjustmentDirection.Credit,
            GlAccountId = "gl-1200", ArBalanceId = "B1", ReasonCode = "WO", Status = status,
            Period = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    [Fact]
    public async Task RetryingAPost_AfterTheAdjustmentSaveFailed_LeavesOneBalanceEntry()
    {
        await SeedAsync(ArAdjustmentStatus.Approved);

        var first = () => Controller(new FailFirstUpdate(Adjustments())).PostAdjustment("A1");
        await first.Should().ThrowAsync<TimeoutException>();
        (await Adjustments().GetByIdAsync("A1"))!.Status.Should().Be(ArAdjustmentStatus.Approved,
            "the balance was saved but the adjustment was not");

        var retry = await Controller(Adjustments()).PostAdjustment("A1");

        retry.Result.Should().BeOfType<OkObjectResult>();
        var balance = (await Balances().GetByIdAsync("B1"))!;
        balance.PostingEntries.Should().ContainSingle()
            .Which.EntryId.Should().Be("adj-post:A1");
        balance.TotalCredits.Should().Be(25m, "the adjustment is counted once");
        balance.ClosingBalance.Should().Be(975m);
        (await Adjustments().GetByIdAsync("A1"))!.Status.Should().Be(ArAdjustmentStatus.Posted);
    }

    [Fact]
    public async Task RetryingAReversal_AfterTheAdjustmentSaveFailed_LeavesOneReversalEntry()
    {
        await SeedAsync(ArAdjustmentStatus.Approved);
        await Controller(Adjustments()).PostAdjustment("A1");

        var first = () => Controller(new FailFirstUpdate(Adjustments())).ReverseAdjustment("A1");
        await first.Should().ThrowAsync<TimeoutException>();

        var retry = await Controller(Adjustments()).ReverseAdjustment("A1");

        retry.Result.Should().BeOfType<OkObjectResult>();
        var balance = (await Balances().GetByIdAsync("B1"))!;
        balance.PostingEntries.Select(e => e.EntryId).Should().BeEquivalentTo(new[]
        {
            "adj-post:A1", "adj-reverse:A1",
        });
        balance.TotalDebits.Should().Be(25m);
        balance.TotalCredits.Should().Be(25m);
        balance.ClosingBalance.Should().Be(1000m, "post and reversal cancel out exactly once each");
        (await Adjustments().GetByIdAsync("A1"))!.Status.Should().Be(ArAdjustmentStatus.Reversed);
    }

    /// <summary>Lets the balance save through, then fails the adjustment save once.</summary>
    private sealed class FailFirstUpdate(IArAdjustmentRepository inner) : IArAdjustmentRepository
    {
        private bool _failed;

        public Task<ArAdjustment?> GetByIdAsync(string id) => inner.GetByIdAsync(id);

        public Task<IEnumerable<ArAdjustment>> SearchAsync(ArAdjustmentType? type = null, ArAdjustmentStatus? status = null,
            DateTime? period = null, string? glAccountId = null, int page = 1, int pageSize = 50) =>
            inner.SearchAsync(type, status, period, glAccountId, page, pageSize);

        public Task<ArAdjustment> CreateAsync(ArAdjustment adjustment) => inner.CreateAsync(adjustment);

        public Task<ArAdjustment> UpdateAsync(ArAdjustment adjustment)
        {
            if (_failed)
                return inner.UpdateAsync(adjustment);
            _failed = true;
            throw new TimeoutException("adjustment save lost");
        }
    }
}
