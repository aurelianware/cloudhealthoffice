using ArService.Gl;
using ArService.Models;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace ArService.Tests.Gl;

/// <summary>The GL's Mongo stores against a real mongod, and the posting service on them end to end.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class GlMongoRepositoryTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public GlMongoRepositoryTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("ar_gl");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static GlJournalEntry Entry(string id, string period = "2026-05", int day = 1, decimal amount = 10m) => new()
    {
        Id = id,
        TenantId = GlFixtures.Tenant,
        Kind = GlEntryKind.ClaimsAccrual,
        Period = period,
        EntryDate = DateTime.SpecifyKind(DateTime.Parse(period + "-01").AddDays(day - 1), DateTimeKind.Utc),
        Lines =
        [
            new GlJournalLine { LineNumber = 1, Role = GlPostingRole.ClaimsExpense, AccountId = "a", AccountNumber = "5100", Debit = amount },
            new GlJournalLine { LineNumber = 2, Role = GlPostingRole.ClaimsPayable, AccountId = "b", AccountNumber = "2100", Credit = amount },
        ],
        TotalDebit = amount,
        TotalCredit = amount,
    };

    [Fact]
    public async Task The_journal_inserts_once_keeps_money_exact_and_lists_by_period_in_order()
    {
        var journal = new MongoGlJournalRepository(_db);

        (await journal.TryInsertAsync(Entry("t:b", day: 20, amount: 0.1m))).Should().BeTrue();
        (await journal.TryInsertAsync(Entry("t:a", day: 3, amount: 0.2m))).Should().BeTrue();
        (await journal.TryInsertAsync(Entry("t:a", day: 3, amount: 999m))).Should().BeFalse("the id is the business key");
        await journal.TryInsertAsync(Entry("t:c", period: "2026-04"));

        var may = await journal.ListAsync(GlFixtures.Tenant, "2026-05");
        may.Select(e => e.Id).Should().Equal("t:a", "t:b");
        may[0].TotalDebit.Should().Be(0.2m);
        may.Sum(e => e.TotalDebit).Should().Be(0.3m); // decimal128, never binary floating point
        (await journal.ListAsync(GlFixtures.Tenant, null)).Should().HaveCount(3);
        (await journal.GetAsync("tenant-2", "t:a")).Should().BeNull();
    }

    [Fact]
    public async Task The_journal_refuses_an_unbalanced_entry_before_writing()
    {
        var journal = new MongoGlJournalRepository(_db);
        var bad = Entry("t:bad");
        bad.Lines[1].Credit = 9m;
        bad.TotalCredit = 9m;

        await FluentActions.Awaiting(() => journal.TryInsertAsync(bad)).Should().ThrowAsync<InvalidOperationException>();
        (await journal.ListAsync(GlFixtures.Tenant, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Source_events_insert_once_and_change_only_from_the_version_read()
    {
        var events = new MongoGlSourceEventRepository(_db);
        var record = new GlSourceEvent { TenantId = GlFixtures.Tenant, EventId = "ev-1", Type = "PaymentRunExecuted", Status = GlSourceEventStatus.Parked };

        (await events.TryInsertAsync(record)).Should().BeTrue();
        (await events.TryInsertAsync(new GlSourceEvent { TenantId = GlFixtures.Tenant, EventId = "ev-1" })).Should().BeFalse();
        var a = (await events.GetAsync(GlFixtures.Tenant, "ev-1"))!;
        var b = (await events.GetAsync(GlFixtures.Tenant, "ev-1"))!;
        a.Status = GlSourceEventStatus.Posted;
        (await events.TryReplaceAsync(a, a.Version)).Should().BeTrue();
        var bVersion = b.Version;
        b.Status = GlSourceEventStatus.Dismissed;
        (await events.TryReplaceAsync(b, bVersion)).Should().BeFalse();
        b.Version.Should().Be(bVersion);
        (await events.ListAsync(GlFixtures.Tenant, GlSourceEventStatus.Posted)).Should().ContainSingle();
        (await events.GetAsync("tenant-2", "ev-1")).Should().BeNull();
    }

    [Fact]
    public async Task Periods_close_once()
    {
        var periods = new MongoGlPeriodRepository(_db);

        (await periods.TryCloseAsync(new GlClosedPeriod { TenantId = GlFixtures.Tenant, Period = "2026-04", ClosedBy = "c" })).Should().BeTrue();
        (await periods.TryCloseAsync(new GlClosedPeriod { TenantId = GlFixtures.Tenant, Period = "2026-04", ClosedBy = "d" })).Should().BeFalse();
        (await periods.IsClosedAsync(GlFixtures.Tenant, "2026-04")).Should().BeTrue();
        (await periods.IsClosedAsync("tenant-2", "2026-04")).Should().BeFalse();
    }

    [Fact]
    public async Task The_chart_lookup_is_per_tenant_and_never_picks_between_duplicates()
    {
        var accounts = _db.GetCollection<GlAccount>("gl_accounts");
        await accounts.InsertManyAsync(new[]
        {
            new GlAccount { TenantId = GlFixtures.Tenant, AccountNumber = "5100", Status = GlAccountStatus.Active },
            new GlAccount { TenantId = "tenant-2", AccountNumber = "2100", Status = GlAccountStatus.Active },
            new GlAccount { TenantId = GlFixtures.Tenant, AccountNumber = "1250", Status = GlAccountStatus.Active },
            new GlAccount { TenantId = GlFixtures.Tenant, AccountNumber = "1250", Status = GlAccountStatus.Active },
        });
        var chart = new MongoGlChartLookup(_db);

        (await chart.FindAsync(GlFixtures.Tenant, "5100")).Should().NotBeNull();
        (await chart.FindAsync(GlFixtures.Tenant, "2100")).Should().BeNull();
        (await chart.FindAsync(GlFixtures.Tenant, "1250")).Should().BeNull();
    }

    [Fact]
    public async Task Posting_on_mongo_is_idempotent_on_redelivery_and_on_the_business_key()
    {
        var accounts = _db.GetCollection<GlAccount>("gl_accounts");
        foreach (var (role, number) in GlFixtures.Numbers)
            await accounts.InsertOneAsync(new GlAccount
            {
                TenantId = GlFixtures.Tenant, AccountNumber = number, AccountName = role.ToString(),
                Status = GlAccountStatus.Active, EffectiveDate = new DateTime(2020, 1, 1),
            });
        var journal = new MongoGlJournalRepository(_db);
        var events = new MongoGlSourceEventRepository(_db);
        var service = new GlPostingService(journal, events, new MongoGlPeriodRepository(_db), new MongoGlChartLookup(_db),
            Microsoft.Extensions.Options.Options.Create(GlFixtures.Options()), NullLogger<GlPostingService>.Instance, new GlClock());
        var envelope = GlFixtures.RunExecuted();

        var first = await service.IngestAsync(envelope, GlFixtures.Tenant);
        var again = await service.IngestAsync(envelope, GlFixtures.Tenant);
        var otherId = await service.IngestAsync(envelope with { EventId = Guid.NewGuid().ToString() }, GlFixtures.Tenant);

        first.Status.Should().Be(GlSourceEventStatus.Posted);
        again.Duplicate.Should().BeTrue();
        otherId.ParkReason.Should().Be(GlParkReason.DuplicateBusinessKey);
        var entries = await journal.ListAsync(GlFixtures.Tenant, null);
        entries.Should().ContainSingle();
        entries[0].TotalDebit.Should().Be(170m);
        (await events.ListAsync(GlFixtures.Tenant, null)).Should().HaveCount(2);
    }
}
