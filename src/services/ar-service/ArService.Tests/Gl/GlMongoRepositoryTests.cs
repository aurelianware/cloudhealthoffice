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
    public async Task A_period_closes_once_and_only_while_no_post_holds_a_live_lease()
    {
        var periods = new MongoGlPeriodRepository(_db);
        var now = new DateTime(2026, 5, 20, 15, 0, 0, DateTimeKind.Utc);

        // Never used: the close creates it closed.
        (await periods.TryCloseAsync(GlFixtures.Tenant, "2026-03", now, "c", "march")).Should().Be(GlPeriodCloseOutcome.Closed);
        (await periods.TryBeginPostAsync(GlFixtures.Tenant, "2026-03", "late", now.AddMinutes(5))).Should().BeFalse();

        // A post in flight blocks the close; once released, the close goes through and nothing posts after it.
        (await periods.TryBeginPostAsync(GlFixtures.Tenant, "2026-04", "p1", now.AddMinutes(5))).Should().BeTrue();
        (await periods.TryBeginPostAsync(GlFixtures.Tenant, "2026-04", "p2", now.AddMinutes(5))).Should().BeTrue();
        (await periods.TryCloseAsync(GlFixtures.Tenant, "2026-04", now, "c", "april")).Should().Be(GlPeriodCloseOutcome.Busy);
        await periods.EndPostAsync(GlFixtures.Tenant, "2026-04", "p1");
        (await periods.TryCloseAsync(GlFixtures.Tenant, "2026-04", now, "c", "april")).Should().Be(GlPeriodCloseOutcome.Busy);
        await periods.EndPostAsync(GlFixtures.Tenant, "2026-04", "p2");
        (await periods.TryCloseAsync(GlFixtures.Tenant, "2026-04", now, "c", "april")).Should().Be(GlPeriodCloseOutcome.Closed);
        (await periods.TryCloseAsync(GlFixtures.Tenant, "2026-04", now, "d", "again")).Should().Be(GlPeriodCloseOutcome.AlreadyClosed);
        (await periods.TryBeginPostAsync(GlFixtures.Tenant, "2026-04", "p3", now.AddMinutes(5))).Should().BeFalse();

        // An expired lease (its process died) does not hold the period open for ever.
        (await periods.TryBeginPostAsync(GlFixtures.Tenant, "2026-02", "dead", now.AddMinutes(-1))).Should().BeTrue();
        (await periods.TryCloseAsync(GlFixtures.Tenant, "2026-02", now, "c", "feb")).Should().Be(GlPeriodCloseOutcome.Closed);

        // The extract control is recorded once, only on a closed period; tenants are separate.
        var control = new GlExtractControl { Sha256 = new string('a', 64), EntryCount = 2, LineCount = 4, TotalDebit = 10m, TotalCredit = 10m };
        (await periods.TrySetExtractControlAsync(GlFixtures.Tenant, "2026-04", control)).Should().BeTrue();
        (await periods.TrySetExtractControlAsync(GlFixtures.Tenant, "2026-04", new GlExtractControl { Sha256 = new string('b', 64) })).Should().BeFalse();
        (await periods.GetAsync(GlFixtures.Tenant, "2026-04"))!.ExtractSha256.Should().Be(new string('a', 64));
        (await periods.GetAsync("tenant-2", "2026-04")).Should().BeNull();
        (await periods.TryBeginPostAsync("tenant-2", "2026-04", "other", now.AddMinutes(5))).Should().BeTrue();
        (await periods.ListAsync(GlFixtures.Tenant)).Select(p => (p.Period, p.Status)).Should().Equal(
            ("2026-02", GlPeriodStatus.Closed), ("2026-03", GlPeriodStatus.Closed), ("2026-04", GlPeriodStatus.Closed));
    }

    [Fact]
    public async Task Concurrent_first_posts_into_a_new_period_all_get_a_lease()
    {
        var periods = new MongoGlPeriodRepository(_db);
        var expires = DateTime.UtcNow.AddMinutes(5);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => periods.TryBeginPostAsync(GlFixtures.Tenant, "2026-06", $"t{i}", expires)));

        results.Should().AllBeEquivalentTo(true);
        (await periods.GetAsync(GlFixtures.Tenant, "2026-06"))!.Leases.Should().HaveCount(16);
    }

    [Fact]
    public async Task Journal_and_register_are_read_in_pages_by_id()
    {
        var journal = new MongoGlJournalRepository(_db);
        for (var i = 0; i < 5; i++)
            (await journal.TryInsertAsync(PageEntry($"{GlFixtures.Tenant}:accrual:run-{i}", "2026-05"))).Should().BeTrue();
        (await journal.TryInsertAsync(PageEntry("tenant-2:accrual:run-x", "2026-05", "tenant-2"))).Should().BeTrue();

        var first = await journal.ListPageAsync(GlFixtures.Tenant, "2026-05", null, 2);
        var second = await journal.ListPageAsync(GlFixtures.Tenant, "2026-05", first[^1].Id, 2);
        var third = await journal.ListPageAsync(GlFixtures.Tenant, "2026-05", second[^1].Id, 2);

        first.Concat(second).Concat(third).Select(e => e.Id).Should().Equal(Enumerable.Range(0, 5).Select(i => $"{GlFixtures.Tenant}:accrual:run-{i}"));
        third.Should().ContainSingle();
        (await journal.ListAsync(GlFixtures.Tenant, null)).Should().HaveCount(5);
    }

    private static GlJournalEntry PageEntry(string id, string period, string tenant = GlFixtures.Tenant) => new()
    {
        Id = id, TenantId = tenant, Kind = GlEntryKind.ClaimsAccrual, Period = period, EntryDate = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc),
        Lines =
        [
            new GlJournalLine { LineNumber = 1, Role = GlPostingRole.ClaimsExpense, AccountId = "a", AccountNumber = "5100", Debit = 1m },
            new GlJournalLine { LineNumber = 2, Role = GlPostingRole.ClaimsPayable, AccountId = "b", AccountNumber = "2100", Credit = 1m },
        ],
        TotalDebit = 1m, TotalCredit = 1m,
    };

    [Fact]
    public async Task Posts_racing_a_period_close_on_mongo_either_land_before_it_or_park_and_the_closed_extract_never_changes()
    {
        await SeedChartAsync();
        var periods = new MongoGlPeriodRepository(_db);
        var journal = new MongoGlJournalRepository(_db);
        var service = new GlPostingService(journal, new MongoGlSourceEventRepository(_db), periods, new MongoGlChartLookup(_db),
            Microsoft.Extensions.Options.Options.Create(GlFixtures.Options()), NullLogger<GlPostingService>.Instance, new GlClock());

        var posts = Enumerable.Range(0, 24).Select(i => Task.Run(() => service.IngestAsync(GlFixtures.RunExecuted($"race-{i}"), GlFixtures.Tenant))).ToList();
        GlPeriod? closed = null;
        var close = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 500 && closed == null; attempt++)
            {
                try { closed = await service.ClosePeriodAsync(GlFixtures.Tenant, "2026-05", "May", "controller-1"); }
                catch (GlStateException) { await Task.Delay(2); }
            }
        });
        var results = await Task.WhenAll(posts);
        await close;

        closed.Should().NotBeNull();
        results.Should().OnlyContain(r => r.Status == GlSourceEventStatus.Posted
                                          || (r.Status == GlSourceEventStatus.Parked && r.ParkReason == GlParkReason.ClosedPeriod));
        var entries = await journal.ListAsync(GlFixtures.Tenant, "2026-05");
        entries.Should().HaveCount(results.Count(r => r.Status == GlSourceEventStatus.Posted));
        // The control recorded at close is the extract of everything that posted: nothing landed after it.
        var extract = GlExtractBuilder.Build(GlFixtures.Tenant, "2026-05", entries);
        closed!.ExtractSha256.Should().Be(extract.Control.Sha256);
        closed.ExtractEntryCount.Should().Be(entries.Count);
        (await service.GetExtractAsync(GlFixtures.Tenant, "2026-05")).Control.Sha256.Should().Be(closed.ExtractSha256);
        (await periods.GetAsync(GlFixtures.Tenant, "2026-05"))!.Leases.Should().BeEmpty();
    }

    private async Task SeedChartAsync()
    {
        var accounts = _db.GetCollection<GlAccount>("gl_accounts");
        foreach (var (role, number) in GlFixtures.Numbers)
            await accounts.InsertOneAsync(new GlAccount
            {
                TenantId = GlFixtures.Tenant, AccountNumber = number, AccountName = role.ToString(),
                Status = GlAccountStatus.Active, EffectiveDate = new DateTime(2020, 1, 1),
            });
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
