using ArLegacyPostingReconciliation;
using ArService.Controllers;
using ArService.Ledger;
using ArService.Models;
using ArService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ArLegacyPostingReconciliation.Tests;

/// <summary>
/// The listing and the reconciliation against a real mongod: legacy postings as the old apply
/// left them (status and AppliedAmount set, no PostedEntryId, no balance credited).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class LegacyPostingReconciliationTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly string _dbName;
    private readonly TenantDatabases _databases;
    private readonly string _dir;
    private readonly List<string> _output = new();

    public LegacyPostingReconciliationTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("ar_legacy");
        _dbName = _db.DatabaseNamespace.DatabaseName;
        _databases = new TenantDatabases(mongo.Client, _dbName, useTenantScoping: false);
        _dir = Path.Combine(Path.GetTempPath(), "ar-legacy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    // The guard-capable ar-service has started against the database: the precondition for execute.
    public Task InitializeAsync() => ArServiceCapabilities.WriteAsync(_db, DateTime.UtcNow);

    public async Task DisposeAsync()
    {
        await _mongo.DropDatabaseAsync(_db);
        Directory.Delete(_dir, recursive: true);
    }

    private IMongoCollection<CashPosting> Postings => _db.GetCollection<CashPosting>("cash_postings");
    private IMongoCollection<ArBalance> Balances => _db.GetCollection<ArBalance>("ar_balances");
    private IMongoCollection<LegacyReconciliationAudit> Audits => _db.GetCollection<LegacyReconciliationAudit>(LegacyReconciliationAudit.Collection);
    private IMongoCollection<LegacyReconciliationRun> Runs => _db.GetCollection<LegacyReconciliationRun>(LegacyReconciliationRun.Collection);

    private async Task Balance(string id, decimal opening, string gl = "gl-1200", string tenant = Tenant, IMongoDatabase? db = null) =>
        await (db ?? _db).GetCollection<ArBalance>("ar_balances").InsertOneAsync(new ArBalance
        {
            Id = id, TenantId = tenant, GlAccountId = gl, AccountNumber = "1200", Period = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            OpeningBalance = opening, ClosingBalance = opening, SponsorBalance = opening
        });

    private async Task<CashPosting> Posting(string id, CashPostingStatus status, decimal amount, (string Balance, decimal Amount)[] applications,
        string tenant = Tenant, bool posted = false, IMongoDatabase? db = null)
    {
        var posting = new CashPosting
        {
            Id = id, TenantId = tenant, PostingNumber = $"CP-20260301-{id.ToUpperInvariant()}",
            ReceiptDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), Amount = amount,
            PayerType = PayerType.Sponsor, PayerReferenceId = "GRP001", PayerName = "Acme, Inc.", Status = status,
            AppliedAmount = status == CashPostingStatus.Pending ? 0m : applications.Sum(a => a.Amount),
            UnappliedAmount = amount - (status == CashPostingStatus.Pending ? 0m : applications.Sum(a => a.Amount)),
            CreatedBy = "clerk-1",
            Applications = applications.Select((a, i) => new CashApplication
            {
                ArBalanceId = a.Balance, GlAccountId = "gl-1200", AmountApplied = a.Amount, Memo = "premium",
                Period = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                PostedEntryId = posted ? CashPostingLedger.CreditEntryId(id, i) : null
            }).ToList()
        };
        await (db ?? _db).GetCollection<CashPosting>("cash_postings").InsertOneAsync(posting);
        return posting;
    }

    /// <summary>The standard data: two legacy postings and three that are not legacy.</summary>
    private async Task SeedAsync()
    {
        await Balance("bal-1", 1000m);
        await Balance("bal-2", 500m);
        await Balance("bal-3", 300m);
        await Posting("cp-partial", CashPostingStatus.PartiallyApplied, 1000m, [("bal-1", 600m), ("bal-2", 200m)]);
        await Posting("cp-applied", CashPostingStatus.Applied, 300m, [("bal-3", 300m)]);
        await Posting("cp-pending", CashPostingStatus.Pending, 100m, [("bal-1", 100m)]);
        await Posting("cp-voided", CashPostingStatus.Voided, 100m, [("bal-1", 100m)]);
        await Posting("cp-new", CashPostingStatus.PartiallyApplied, 100m, [("bal-1", 50m)], posted: true);
    }

    private async Task<string> ListToCsvAsync(string name = "listing.csv")
    {
        var path = Path.Combine(_dir, name);
        var rows = await new LegacyPostingLister(_databases).ListAsync();
        await using (var writer = new StreamWriter(path))
            await LegacyPostingLister.WriteCsvAsync(writer, rows);
        return path;
    }

    /// <summary>Fills finance's columns: decisions by (posting, application index).</summary>
    private static async Task<string> DecideAsync(string listing, string name, Func<Dictionary<string, string>, bool> keep,
        params (string Posting, int Index, string Decision)[] decisions)
    {
        var rows = Csv.ParseWithHeader(await File.ReadAllTextAsync(listing));
        var path = Path.Combine(Path.GetDirectoryName(listing)!, name);
        var lines = new List<string> { Csv.Line(LegacyPostingLister.Header) };
        foreach (var (_, fields) in rows.Where(r => keep(r.Fields)))
        {
            var match = decisions.FirstOrDefault(d => d.Posting == fields["posting_id"] && d.Index.ToString() == fields["application_index"]);
            if (match.Decision != null)
            {
                fields["decision"] = match.Decision;
                fields["reviewer"] = "fin-reviewer";
                fields["reviewed_at"] = "2026-10-08";
                fields["ticket"] = "FIN-123";
                fields["note"] = "checked against bank statement";
            }
            lines.Add(Csv.Line(LegacyPostingLister.Header.Select(h => fields[h])));
        }
        await File.WriteAllTextAsync(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private LegacyPostingReconciler Reconciler() => new(_databases, _output.Add);

    private ReconcileOptions Options(string csv, bool execute, string? confirm = null, IReadOnlyCollection<string>? tenants = null) => new()
    {
        CsvPath = csv, Execute = execute, ExpectedSha256 = execute ? LegacyPostingReconciler.Sha256Of(csv) : null, Operator = "ops-1",
        ConfirmDatabase = confirm ?? _dbName, Tenants = tenants
    };

    private Task<ReconcileResult> RunAsync(string csv, bool execute, LegacyPostingReconciler? reconciler = null) =>
        (reconciler ?? Reconciler()).RunAsync(Options(csv, execute));

    private async Task<List<BsonDocument>> SnapshotAsync(string collection) =>
        await _db.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty).Sort("{_id:1}").ToListAsync();

    private async Task<CashPosting> Read(string id) => await Postings.Find(p => p.Id == id).FirstAsync();
    private async Task<ArBalance> ReadBalance(string id) => await Balances.Find(b => b.Id == id).FirstAsync();

    // ── Listing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsOneRowPerLegacyApplication_AndNothingElse()
    {
        await SeedAsync();

        var csv = await ListToCsvAsync();
        var rows = Csv.ParseWithHeader(await File.ReadAllTextAsync(csv), LegacyPostingLister.Header);

        rows.Select(r => (r.Fields["posting_id"], r.Fields["application_index"]))
            .Should().BeEquivalentTo(new[] { ("cp-partial", "0"), ("cp-partial", "1"), ("cp-applied", "0") });
        var first = rows.Single(r => r.Fields["posting_id"] == "cp-partial" && r.Fields["application_index"] == "0").Fields;
        first["tenant_id"].Should().Be(Tenant);
        first["posting_number"].Should().Be("CP-20260301-CP-PARTIAL");
        first["payer_name"].Should().Be("Acme, Inc.");
        first["posting_amount"].Should().Be("1000");
        first["applied_amount"].Should().Be("800");
        first["ar_balance_id"].Should().Be("bal-1");
        first["account_number"].Should().Be("1200");
        first["amount_applied"].Should().Be("600");
        first["balance_closing_balance"].Should().Be("1000");
        first["balance_has_cash_entry"].Should().Be("false");
        first["posting_created_by"].Should().Be("clerk-1");
        first["applied_by"].Should().Be(LegacyPostingLister.AppliedByNotRecorded);
        first["source_database"].Should().Be(_dbName);
        first["source_host"].Should().Be(_databases.Host).And.NotBeEmpty();
        first["decision"].Should().BeEmpty();
    }

    [Fact]
    public async Task List_IsReadOnly()
    {
        await SeedAsync();
        var collections = await (await _db.ListCollectionNamesAsync()).ToListAsync();
        var indexes = await (await Balances.Indexes.ListAsync()).ToListAsync();
        var postings = await SnapshotAsync("cash_postings");
        var balances = await SnapshotAsync("ar_balances");

        await ListToCsvAsync();

        (await (await _db.ListCollectionNamesAsync()).ToListAsync()).Should().BeEquivalentTo(collections);
        (await (await Balances.Indexes.ListAsync()).ToListAsync()).Should().HaveCount(indexes.Count);
        (await SnapshotAsync("cash_postings")).Should().Equal(postings);
        (await SnapshotAsync("ar_balances")).Should().Equal(balances);
    }

    [Fact]
    public async Task List_WithTenantScoping_ReadsEveryTenantDatabase()
    {
        var baseName = $"arscoped{Guid.NewGuid():N}"[..20];
        var scoped = new TenantDatabases(_mongo.Client, baseName, useTenantScoping: true);
        var dbA = scoped.ForTenant("tenant-a");
        var dbB = scoped.ForTenant("tenant.b");
        try
        {
            await Balance("bal-a", 100m, tenant: "tenant-a", db: dbA);
            await Posting("cp-a", CashPostingStatus.PartiallyApplied, 100m, [("bal-a", 40m)], tenant: "tenant-a", db: dbA);
            await Balance("bal-b", 100m, tenant: "tenant.b", db: dbB);
            await Posting("cp-b", CashPostingStatus.Applied, 60m, [("bal-b", 60m)], tenant: "tenant.b", db: dbB);

            var rows = await new LegacyPostingLister(scoped).ListAsync();

            rows.Select(r => (r[0], r[1])).Should().BeEquivalentTo(new[] { ("tenant-a", "cp-a"), ("tenant.b", "cp-b") });
            dbB.DatabaseNamespace.DatabaseName.Should().Be($"{baseName}_tenant_b");
        }
        finally
        {
            await _mongo.Client.DropDatabaseAsync(dbA.DatabaseNamespace.DatabaseName);
            await _mongo.Client.DropDatabaseAsync(dbB.DatabaseNamespace.DatabaseName);
        }
    }

    // ── Environment, pre-flight and the hash ─────────────────────────────────

    [Fact]
    public async Task DryRun_ChangesNothing_AndPrintsTargetAndBeforeAndAfter()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true,
            ("cp-partial", 0, "APPLY_CREDIT"), ("cp-partial", 1, "CORRECTED_MANUALLY"), ("cp-applied", 0, "APPLY_CREDIT"));
        var postings = await SnapshotAsync("cash_postings");
        var balances = await SnapshotAsync("ar_balances");

        var result = await RunAsync(csv, execute: false);

        result.Count(OutcomeKind.Reconciled).Should().Be(2);
        (await SnapshotAsync("cash_postings")).Should().Equal(postings);
        (await SnapshotAsync("ar_balances")).Should().Equal(balances);
        (await (await _db.ListCollectionNamesAsync()).ToListAsync()).Should().NotContain(LegacyReconciliationAudit.Collection)
            .And.NotContain(LegacyReconciliationRun.Collection);
        _output.Should().Contain(l => l.StartsWith("Target: host ") && l.Contains($"database {_dbName}"));
        _output.Should().Contain(l => l.StartsWith("ar-service: build ") && l.Contains(ArServiceCapabilities.LegacyReconciliationGuard));
        _output.Should().Contain(l => l.Contains("app 0 -> balance bal-1") && l.Contains("WOULD credit 600")
                                      && l.Contains("before [closing 1000") && l.Contains("after [closing 400"));
        _output.Should().Contain(l => l.Contains("app 1 -> balance bal-2") && l.Contains("WOULD mark corrected manually")
                                      && l.Contains("before [closing 500") && l.Contains("after [closing 500"));
    }

    [Fact]
    public async Task Execute_WithAnotherFilesHash_ChangesNothing()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));
        var balances = await SnapshotAsync("ar_balances");

        var options = Options(csv, execute: true);
        var act = () => Reconciler().RunAsync(new ReconcileOptions
        {
            CsvPath = csv, Execute = true, ExpectedSha256 = new string('0', 64), Operator = "ops-1", ConfirmDatabase = options.ConfirmDatabase
        });

        await act.Should().ThrowAsync<CsvHashMismatchException>();
        (await SnapshotAsync("ar_balances")).Should().Equal(balances);
        (await Read("cp-applied")).Applications[0].PostedEntryId.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WithoutAGuardCapableService_IsRefused_AndChangesNothing()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));
        await _db.DropCollectionAsync(ArServiceCapabilities.Collection);
        var balances = await SnapshotAsync("ar_balances");
        var postings = await SnapshotAsync("cash_postings");

        var act = () => RunAsync(csv, execute: true);

        (await act.Should().ThrowAsync<EnvironmentMismatchException>()).Which.Message.Should().Contain("Deploy the ar-service build with the guard first");
        (await SnapshotAsync("ar_balances")).Should().Equal(balances);
        (await SnapshotAsync("cash_postings")).Should().Equal(postings);
        (await (await _db.ListCollectionNamesAsync()).ToListAsync()).Should().NotContain(LegacyReconciliationRun.Collection);

        // A dry-run still works, and says why execute would be refused.
        (await RunAsync(csv, execute: false)).Count(OutcomeKind.Reconciled).Should().Be(1);
        _output.Should().Contain(l => l.StartsWith("WARNING: no ar-service capability marker"));
    }

    [Fact]
    public async Task Execute_WhenTheMarkerLacksTheGuard_IsRefused()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));
        await _db.GetCollection<ArServiceCapabilities>(ArServiceCapabilities.Collection).UpdateOneAsync(
            c => c.Id == ArServiceCapabilities.DocumentId,
            Builders<ArServiceCapabilities>.Update.Set(c => c.Capabilities, new List<string> { ArServiceCapabilities.LedgerCredit }));

        var act = () => RunAsync(csv, execute: true);

        (await act.Should().ThrowAsync<EnvironmentMismatchException>()).Which.Message.Should().Contain(ArServiceCapabilities.LegacyReconciliationGuard);
        (await ReadBalance("bal-3")).PostingEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task TheServicesStartupWriter_WritesTheMarker()
    {
        await _db.DropCollectionAsync(ArServiceCapabilities.Collection);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:DatabaseName"] = _dbName }).Build();
        var writer = new ArServiceCapabilitiesWriter(_mongo.Client, config, NullLogger<ArServiceCapabilitiesWriter>.Instance);

        await writer.StartAsync(CancellationToken.None);
        ArServiceCapabilities? marker = null;
        for (var i = 0; i < 100 && marker == null; i++)
        {
            marker = await ArServiceCapabilities.ReadAsync(_db);
            if (marker == null) await Task.Delay(100);
        }
        await writer.StopAsync(CancellationToken.None);

        marker.Should().NotBeNull();
        marker!.Capabilities.Should().BeEquivalentTo(ArServiceCapabilities.Current);
        marker.Build.Should().NotBeNullOrEmpty();
        marker.LastStartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task UnconfirmedOrOtherDatabase_IsRefused_BeforeAnythingIsRead()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));

        await FluentActions.Awaiting(() => Reconciler().RunAsync(Options(csv, execute: true, confirm: "CloudHealthOffice")))
            .Should().ThrowAsync<EnvironmentMismatchException>().WithMessage("*not the configured database*");
        var withoutConfirm = Options(csv, execute: true);
        await FluentActions.Awaiting(() => Reconciler().RunAsync(new ReconcileOptions
            {
                CsvPath = csv, Execute = true, ExpectedSha256 = withoutConfirm.ExpectedSha256, Operator = "ops-1"
            }))
            .Should().ThrowAsync<EnvironmentMismatchException>().WithMessage("*--confirm-database is required*");
        _output.Should().BeEmpty();
        (await ReadBalance("bal-3")).PostingEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task CsvListedFromAnotherEnvironment_IsRefused()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));
        // The same file run against another database of the same server (say staging's CSV against production).
        var other = _mongo.CreateDatabase("ar_legacy_other");
        try
        {
            var elsewhere = new TenantDatabases(_mongo.Client, other.DatabaseNamespace.DatabaseName, useTenantScoping: false);
            await ArServiceCapabilities.WriteAsync(other, DateTime.UtcNow);

            var act = () => new LegacyPostingReconciler(elsewhere, _output.Add).RunAsync(Options(csv, execute: true, confirm: other.DatabaseNamespace.DatabaseName));

            (await act.Should().ThrowAsync<EnvironmentMismatchException>()).Which.Message.Should().Contain($"listed from {_dbName}");
            (await (await other.ListCollectionNamesAsync()).ToListAsync()).Should().NotContain(LegacyReconciliationRun.Collection);
        }
        finally
        {
            await _mongo.DropDatabaseAsync(other);
        }
    }

    [Fact]
    public async Task TenantRestriction_RefusesACsvWithOtherTenants()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));

        await FluentActions.Awaiting(() => Reconciler().RunAsync(Options(csv, execute: true, tenants: ["tenant-2"])))
            .Should().ThrowAsync<EnvironmentMismatchException>().WithMessage("*tenant-1*outside --tenant tenant-2*");

        (await Reconciler().RunAsync(Options(csv, execute: true, tenants: [Tenant]))).Count(OutcomeKind.Reconciled).Should().Be(1);
    }

    // ── Execute ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_CarriesOutBothDecisions_AndAudits()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true,
            ("cp-partial", 0, "APPLY_CREDIT"), ("cp-partial", 1, "CORRECTED_MANUALLY"), ("cp-applied", 0, "CORRECTED_MANUALLY"));

        var result = await RunAsync(csv, execute: true);

        result.Count(OutcomeKind.Reconciled).Should().Be(2);
        result.Count(OutcomeKind.Refused).Should().Be(0);

        // APPLY_CREDIT: the controller's own entry, through the versioned repository.
        var bal1 = await ReadBalance("bal-1");
        bal1.PostingEntries.Should().ContainSingle(e => e.EntryId == "cash-cp-partial-0" && e.CreditAmount == 600m && e.PostedBy == "ops-1");
        bal1.TotalCredits.Should().Be(600m);
        bal1.ClosingBalance.Should().Be(400m);
        bal1.SponsorCredits.Should().Be(600m);
        bal1.Version.Should().Be(1);

        // CORRECTED_MANUALLY: no balance changes.
        (await ReadBalance("bal-2")).PostingEntries.Should().BeEmpty();
        (await ReadBalance("bal-2")).ClosingBalance.Should().Be(500m);
        (await ReadBalance("bal-3")).ClosingBalance.Should().Be(300m);

        var partial = await Read("cp-partial");
        partial.Applications.Select(a => a.PostedEntryId).Should().Equal("cash-cp-partial-0", "manual-cp-partial-1");
        partial.Status.Should().Be(CashPostingStatus.PartiallyApplied);
        partial.LegacyReconciliation!.Status.Should().Be(LegacyReconciliationStatus.Reconciled);
        partial.LegacyReconciliation.ReviewedBy.Should().Be("fin-reviewer");
        partial.LegacyReconciliation.ReviewedAt.Should().Be(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        partial.LegacyReconciliation.Ticket.Should().Be("FIN-123");
        partial.LegacyReconciliation.ReconciledBy.Should().Be("ops-1");
        partial.LegacyReconciliation.CsvSha256.Should().Be(result.CsvSha256);
        partial.LegacyReconciliation.RunId.Should().Be(result.RunId);
        CashPostingLedger.IsLegacy(partial).Should().BeFalse();
        CashPostingLedger.RequiresLegacyReconciliation(partial).Should().BeFalse();

        var audits = await Audits.Find(FilterDefinition<LegacyReconciliationAudit>.Empty).ToListAsync();
        audits.Should().HaveCount(3).And.OnlyContain(a => a.State == AuditState.Completed && a.Operator == "ops-1" && a.RunId == result.RunId
                                                          && a.CsvSha256 == result.CsvSha256 && a.Ticket == "FIN-123"
                                                          && a.Database == _dbName && a.Host == _databases.Host);
        var credit = audits.Single(a => a.Id == $"{result.RunId}:tenant-1:cp-partial:0");
        credit.Decision.Should().Be("APPLY_CREDIT");
        credit.Credited.Should().BeTrue();
        credit.Before!.ClosingBalance.Should().Be(1000m);
        credit.After!.ClosingBalance.Should().Be(400m);
        credit.After.Version.Should().Be(1);
        credit.Events.Select(e => e.Event).Should().Equal(AuditEvent.Intended, AuditEvent.CreditIntended, AuditEvent.Credited, AuditEvent.PostingSaved);
        var manual = audits.Single(a => a.Id == $"{result.RunId}:tenant-1:cp-partial:1");
        manual.Credited.Should().BeFalse();
        manual.Before!.ClosingBalance.Should().Be(500m);
        manual.After!.ClosingBalance.Should().Be(500m);
        manual.Events.Select(e => e.Event).Should().Equal(AuditEvent.Intended, AuditEvent.PostingSaved);

        var run = await Runs.Find(r => r.Id == result.RunId).SingleAsync();
        run.State.Should().Be(RunState.Finished);
        run.Operator.Should().Be("ops-1");
        run.Database.Should().Be(_dbName);
        run.Host.Should().Be(_databases.Host);
        run.ServiceBuild.Should().NotBeNullOrEmpty();
        run.FinishedAt.Should().NotBeNull();
        run.CurrentPosting.Should().BeNull();
        run.Postings.Should().HaveCount(2);
        run.Actions.Should().ContainSingle(a => a.ArBalanceId == "bal-1" && a.EntryIds.SequenceEqual(new[] { "cash-cp-partial-0" }) && a.Amount == 600m);

        // Nothing is legacy any more.
        (await new LegacyPostingLister(_databases).ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RunningTwice_ChangesNothingTheSecondTime_AndKeepsTheFirstRunsAudit()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true,
            ("cp-partial", 0, "APPLY_CREDIT"), ("cp-partial", 1, "CORRECTED_MANUALLY"), ("cp-applied", 0, "APPLY_CREDIT"));
        await RunAsync(csv, execute: true);
        var postings = await SnapshotAsync("cash_postings");
        var balances = await SnapshotAsync("ar_balances");
        var audits = await SnapshotAsync(LegacyReconciliationAudit.Collection);

        var second = await RunAsync(csv, execute: true);

        second.Count(OutcomeKind.AlreadyReconciled).Should().Be(2);
        second.Count(OutcomeKind.Reconciled).Should().Be(0);
        (await SnapshotAsync("cash_postings")).Should().Equal(postings);
        (await SnapshotAsync("ar_balances")).Should().Equal(balances);
        (await SnapshotAsync(LegacyReconciliationAudit.Collection)).Should().Equal(audits);
        (await Runs.CountDocumentsAsync(FilterDefinition<LegacyReconciliationRun>.Empty)).Should().Be(2);
    }

    [Fact]
    public async Task ARunInterruptedAfterTheCredit_DoesNotCreditTwice_AndKeepsTheOriginalCreditRecord()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));

        // The first run credits bal-3 and stops before the posting is saved.
        var crashing = Reconciler();
        crashing.BeforePostingSave = _ => throw new IOException("connection lost");
        var first = await RunAsync(csv, execute: true, crashing);
        first.Postings.Single().Outcome.Should().Be(OutcomeKind.Failed);
        (await ReadBalance("bal-3")).PostingEntries.Should().ContainSingle(e => e.EntryId == "cash-cp-applied-0");
        (await Read("cp-applied")).Applications[0].PostedEntryId.Should().BeNull();

        // Its run record shows what it did, though it never got to the posting.
        var firstRun = await Runs.Find(r => r.Id == first.RunId).SingleAsync();
        firstRun.Actions.Should().ContainSingle(a => a.ArBalanceId == "bal-3" && a.Amount == 300m);

        var second = await RunAsync(csv, execute: true);

        second.Count(OutcomeKind.Reconciled).Should().Be(1);
        var after = await ReadBalance("bal-3");
        after.PostingEntries.Should().ContainSingle();
        after.TotalCredits.Should().Be(300m);
        (await Read("cp-applied")).Applications[0].PostedEntryId.Should().Be("cash-cp-applied-0");

        var audits = await Audits.Find(a => a.PostingId == "cp-applied").ToListAsync();
        audits.Should().HaveCount(2);
        var original = audits.Single(a => a.RunId == first.RunId);
        original.Credited.Should().BeTrue();
        original.State.Should().Be(AuditState.Failed);
        original.After!.TotalCredits.Should().Be(300m);
        original.Events.Select(e => e.Event).Should().Equal(AuditEvent.Intended, AuditEvent.CreditIntended, AuditEvent.Credited, AuditEvent.Failed);
        original.Events.Last().Detail.Should().Contain("connection lost");
        var completing = audits.Single(a => a.RunId == second.RunId);
        completing.Credited.Should().BeFalse();
        completing.State.Should().Be(AuditState.Completed);
        completing.Events.Select(e => e.Event).Should().Equal(AuditEvent.Intended, AuditEvent.AlreadyOnBalance, AuditEvent.PostingSaved);
    }

    [Fact]
    public async Task ABalanceChangedDuringTheSave_IsReReadAndRetried_KeepingBothChanges()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));
        var reconciler = Reconciler();
        var interfered = 0;
        reconciler.BeforeBalanceSave = async balance =>
        {
            if (interfered++ > 0) return;
            // Someone else (say a manual adjustment) saves bal-3 between the tool's read and save.
            await Balances.UpdateOneAsync(b => b.Id == balance.Id,
                Builders<ArBalance>.Update.Inc(b => b.Version, 1L).Inc(b => b.TotalDebits, 25m).Inc(b => b.ClosingBalance, 25m));
        };

        var result = await RunAsync(csv, execute: true, reconciler);

        result.Count(OutcomeKind.Reconciled).Should().Be(1);
        var balance = await ReadBalance("bal-3");
        balance.PostingEntries.Should().ContainSingle(e => e.EntryId == "cash-cp-applied-0");
        balance.TotalDebits.Should().Be(25m, "the concurrent change is kept");
        balance.TotalCredits.Should().Be(300m);
        balance.ClosingBalance.Should().Be(25m);
        balance.Version.Should().Be(2);
        var audit = await Audits.Find(a => a.PostingId == "cp-applied").SingleAsync();
        audit.Events.Select(e => e.Event).Should().Equal(
            AuditEvent.Intended, AuditEvent.CreditIntended, AuditEvent.ConcurrencyRetry, AuditEvent.CreditIntended, AuditEvent.Credited, AuditEvent.PostingSaved);
        _output.Should().Contain(l => l.Contains("changed concurrently"));
    }

    [Fact]
    public async Task APostingChangedDuringTheRun_IsNotSaved_AndARerunCompletesWithoutCreditingTwice()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));
        var reconciler = Reconciler();
        reconciler.BeforePostingSave = _ => Postings.UpdateOneAsync(p => p.Id == "cp-applied",
            Builders<CashPosting>.Update.Set(p => p.LastUpdatedAt, DateTime.UtcNow.AddMinutes(1)));

        var first = await RunAsync(csv, execute: true, reconciler);

        first.Postings.Single().Outcome.Should().Be(OutcomeKind.Failed);
        first.Postings.Single().Reasons.Should().Contain(r => r.Contains("changed while it was being reconciled"));
        (await Read("cp-applied")).LegacyReconciliation.Should().BeNull();
        (await ReadBalance("bal-3")).TotalCredits.Should().Be(300m);
        var audit = await Audits.Find(a => a.RunId == first.RunId).SingleAsync();
        audit.State.Should().Be(AuditState.Failed);
        audit.Credited.Should().BeTrue();
        audit.Events.Last().Event.Should().Be(AuditEvent.PostingSaveConflict);

        var second = await RunAsync(csv, execute: true);

        second.Count(OutcomeKind.Reconciled).Should().Be(1);
        (await ReadBalance("bal-3")).PostingEntries.Should().ContainSingle();
        (await ReadBalance("bal-3")).TotalCredits.Should().Be(300m);
        (await Read("cp-applied")).LegacyReconciliation!.RunId.Should().Be(second.RunId);
    }

    [Fact]
    public async Task CorrectedManually_WhenTheBalanceNoLongerExists_IsReconciledAndRecordedAsMissing()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "CORRECTED_MANUALLY"));
        await Balances.DeleteOneAsync(b => b.Id == "bal-3");

        var dry = await RunAsync(csv, execute: false);
        dry.Count(OutcomeKind.Reconciled).Should().Be(1);
        _output.Should().Contain(l => l.Contains("balance NOT FOUND"));

        var result = await RunAsync(csv, execute: true);

        result.Count(OutcomeKind.Reconciled).Should().Be(1);
        var posting = await Read("cp-applied");
        posting.Applications[0].PostedEntryId.Should().Be("manual-cp-applied-0");
        posting.LegacyReconciliation!.Applications.Single().BalanceMissing.Should().BeTrue();
        var audit = await Audits.Find(a => a.RunId == result.RunId).SingleAsync();
        audit.BalanceMissing.Should().BeTrue();
        audit.Before.Should().BeNull();
        audit.State.Should().Be(AuditState.Completed);
        CashPostingLedger.RequiresLegacyReconciliation(posting).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyCredit_WhenTheBalanceNoLongerExists_IsRefused()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));
        await Balances.DeleteOneAsync(b => b.Id == "bal-3");

        var outcome = await RefusedAsync(csv, "cp-applied");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        outcome.Reasons.Should().Contain(r => r.Contains("not found") && r.Contains("CORRECTED_MANUALLY can be recorded without it"));
    }

    // ── The tool, then the service ─────────────────────────────────────────

    private sealed class Actor(string userId) : ICurrentActor
    {
        public bool IsAuthenticated => true;
        public string UserId => userId;
        public string? DisplayName => null;
        public string? Email => null;
        public string TenantId => Tenant;
        public bool IsService => false;
        public IReadOnlyCollection<string> Roles => [];
        public bool HasPermission(string permission) => true;
    }

    private CashPostingController Controller()
    {
        var accessor = TenantDatabases.AccessorFor(Tenant);
        return new CashPostingController(
            new MongoCashPostingRepository(_db, accessor, NullLogger<MongoCashPostingRepository>.Instance),
            new MongoArBalanceRepository(_db, accessor, NullLogger<MongoArBalanceRepository>.Instance),
            new Actor("clerk-2"), NullLogger<CashPostingController>.Instance);
    }

    [Fact]
    public async Task EndToEnd_TheServiceRefusesALegacyPosting_ThenAppliesAndVoidsItCorrectlyAfterReconciliation()
    {
        await SeedAsync();

        // Before: 409, nothing credited.
        var refused = await Controller().ApplyCashPosting("cp-partial");
        refused.Result.Should().BeOfType<ConflictObjectResult>();
        (await Controller().VoidCashPosting("cp-partial")).Result.Should().BeOfType<ConflictObjectResult>();
        (await ReadBalance("bal-1")).PostingEntries.Should().BeEmpty();

        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-partial",
            ("cp-partial", 0, "APPLY_CREDIT"), ("cp-partial", 1, "CORRECTED_MANUALLY"));
        (await RunAsync(csv, execute: true)).Count(OutcomeKind.Reconciled).Should().Be(1);

        // Apply now credits nothing more: one application credited by the tool, one corrected by hand.
        (await Controller().ApplyCashPosting("cp-partial")).Result.Should().BeOfType<OkObjectResult>();
        (await ReadBalance("bal-1")).PostingEntries.Should().ContainSingle();
        (await ReadBalance("bal-1")).ClosingBalance.Should().Be(400m);
        (await ReadBalance("bal-2")).PostingEntries.Should().BeEmpty();
        (await ReadBalance("bal-2")).ClosingBalance.Should().Be(500m);

        // Void reverses the tool's credit and leaves the manual correction to finance.
        (await Controller().VoidCashPosting("cp-partial")).Result.Should().BeOfType<OkObjectResult>();
        var bal1 = await ReadBalance("bal-1");
        bal1.PostingEntries.Select(e => e.EntryId).Should().Equal("cash-cp-partial-0", "rev-cash-cp-partial-0");
        bal1.ClosingBalance.Should().Be(1000m);
        (await ReadBalance("bal-2")).PostingEntries.Should().BeEmpty();
        (await Read("cp-partial")).Status.Should().Be(CashPostingStatus.Voided);
        (await Read("cp-partial")).LegacyReconciliation!.Status.Should().Be(LegacyReconciliationStatus.Reconciled);
    }

    // ── Refusals ───────────────────────────────────────────────────────────

    private async Task<PostingOutcome> RefusedAsync(string csv, string postingId)
    {
        var balances = await SnapshotAsync("ar_balances");
        var postings = await SnapshotAsync("cash_postings");
        var result = await RunAsync(csv, execute: true);
        (await SnapshotAsync("ar_balances")).Should().Equal(balances);
        (await SnapshotAsync("cash_postings")).Should().Equal(postings);
        return result.Postings.Single(p => p.PostingId == postingId);
    }

    [Fact]
    public async Task AmountChangedSinceTheListing_IsRefused()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));
        await File.WriteAllTextAsync(csv, (await File.ReadAllTextAsync(csv)).Replace(",300,", ",299,"));

        var outcome = await RefusedAsync(csv, "cp-applied");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        outcome.Reasons.Should().Contain(r => r.Contains("amount"));
    }

    [Fact]
    public async Task PostingNotFound_IsRefused()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));
        await Postings.DeleteOneAsync(p => p.Id == "cp-applied");

        var outcome = await RefusedAsync(csv, "cp-applied");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        outcome.Reasons.Should().Contain(r => r.Contains("not found"));
    }

    [Fact]
    public async Task PostingThatIsNotLegacy_IsRefused()
    {
        await SeedAsync();
        var listing = await ListToCsvAsync();
        // A hand-made row for a posting the current code applied.
        var text = await File.ReadAllTextAsync(listing);
        var row = Csv.Line(LegacyPostingLister.Header.Select(h => h switch
        {
            "tenant_id" => Tenant, "posting_id" => "cp-new", "posting_number" => "CP-20260301-CP-NEW", "posting_status" => "PartiallyApplied",
            "posting_amount" => "100", "applied_amount" => "50", "application_index" => "0", "ar_balance_id" => "bal-1",
            "gl_account_id" => "gl-1200", "amount_applied" => "50", "decision" => "APPLY_CREDIT", "reviewer" => "r", "ticket" => "t",
            "source_database" => _dbName, "source_host" => _databases.Host,
            _ => ""
        }));
        var csv = Path.Combine(_dir, "forged.csv");
        await File.WriteAllTextAsync(csv, text + row + "\n");

        var outcome = await RefusedAsync(csv, "cp-new");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        outcome.Reasons.Should().Contain(r => r.Contains("not a legacy posting"));
    }

    [Fact]
    public async Task PartlyDecidedPosting_IsRefused_AndUndecidedOneSkipped()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-partial", 0, "APPLY_CREDIT"));

        var result = await RunAsync(csv, execute: true);

        result.Postings.Single(p => p.PostingId == "cp-partial").Outcome.Should().Be(OutcomeKind.Refused);
        result.Postings.Single(p => p.PostingId == "cp-applied").Outcome.Should().Be(OutcomeKind.Skipped);
        (await ReadBalance("bal-1")).PostingEntries.Should().BeEmpty();
        (await Read("cp-partial")).LegacyReconciliation.Should().BeNull();
    }

    [Fact]
    public async Task UnknownDecisionOrMissingTicket_IsRefused()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "MAYBE"));
        await File.WriteAllTextAsync(csv, (await File.ReadAllTextAsync(csv)).Replace("FIN-123", ""));

        var outcome = await RefusedAsync(csv, "cp-applied");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        outcome.Reasons.Should().Contain(r => r.Contains("MAYBE")).And.Contain(r => r.Contains("ticket"));
    }

    [Theory]
    [InlineData("10/08/2026")]
    [InlineData("2026-10-08T00:00:00Z")]
    [InlineData("8 Oct 2026")]
    [InlineData("2026-13-01")]
    public async Task ReviewedAtNotWrittenYyyyMmDd_IsRefused(string reviewedAt)
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));
        await File.WriteAllTextAsync(csv, (await File.ReadAllTextAsync(csv)).Replace("2026-10-08,FIN-123", Csv.Field(reviewedAt) + ",FIN-123"));

        var outcome = await RefusedAsync(csv, "cp-applied");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        outcome.Reasons.Should().Contain(r => r.Contains("reviewed_at") && r.Contains("yyyy-MM-dd"));
    }

    [Fact]
    public async Task CorrectedManually_WhenTheBalanceAlreadyHoldsTheCredit_IsRefused()
    {
        await SeedAsync();
        var posting = await Read("cp-applied");
        var balance = await ReadBalance("bal-3");
        CashPostingLedger.Post(balance, CashPostingLedger.CreditEntry(posting, 0, "x", DateTime.UtcNow), posting.PayerType);
        await Balances.ReplaceOneAsync(b => b.Id == "bal-3", balance);
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "CORRECTED_MANUALLY"));

        var outcome = await RefusedAsync(csv, "cp-applied");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        outcome.Reasons.Should().Contain(r => r.Contains("already holds credit entry"));
    }

    [Fact]
    public async Task ReconciledPosting_WithADifferentDecision_IsRefused()
    {
        await SeedAsync();
        var listing = await ListToCsvAsync();
        var first = await DecideAsync(listing, "first.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "CORRECTED_MANUALLY"));
        await RunAsync(first, execute: true);
        var second = await DecideAsync(listing, "second.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));

        var outcome = await RefusedAsync(second, "cp-applied");

        outcome.Outcome.Should().Be(OutcomeKind.Refused);
        (await ReadBalance("bal-3")).PostingEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedCsv_IsRefusedWhole_AndChangesNothing()
    {
        await SeedAsync();
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", r => r["posting_id"] == "cp-applied", ("cp-applied", 0, "APPLY_CREDIT"));
        var text = await File.ReadAllTextAsync(csv);
        await File.WriteAllTextAsync(csv, text.Replace("FIN-123", "FIN\"123"));

        await FluentActions.Awaiting(() => RunAsync(csv, execute: true)).Should().ThrowAsync<FormatException>().WithMessage("*quote*");
        (await ReadBalance("bal-3")).PostingEntries.Should().BeEmpty();
        (await (await _db.ListCollectionNamesAsync()).ToListAsync()).Should().NotContain(LegacyReconciliationRun.Collection);
    }
}
