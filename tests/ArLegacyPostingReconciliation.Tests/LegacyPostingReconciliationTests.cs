using ArLegacyPostingReconciliation;
using ArService.Ledger;
using ArService.Models;
using CloudHealthOffice.Testing.Mongo;
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
    private readonly TenantDatabases _databases;
    private readonly string _dir;
    private readonly List<string> _output = new();

    public LegacyPostingReconciliationTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("ar_legacy");
        _databases = new TenantDatabases(mongo.Client, _db.DatabaseNamespace.DatabaseName, useTenantScoping: false);
        _dir = Path.Combine(Path.GetTempPath(), "ar-legacy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _mongo.DropDatabaseAsync(_db);
        Directory.Delete(_dir, recursive: true);
    }

    private IMongoCollection<CashPosting> Postings => _db.GetCollection<CashPosting>("cash_postings");
    private IMongoCollection<ArBalance> Balances => _db.GetCollection<ArBalance>("ar_balances");
    private IMongoCollection<LegacyReconciliationAudit> Audits => _db.GetCollection<LegacyReconciliationAudit>(LegacyReconciliationAudit.Collection);

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

    private Task<ReconcileResult> RunAsync(string csv, bool execute) =>
        new LegacyPostingReconciler(_databases, _output.Add).RunAsync(new ReconcileOptions
        {
            CsvPath = csv, Execute = execute, ExpectedSha256 = execute ? LegacyPostingReconciler.Sha256Of(csv) : null, Operator = "ops-1"
        });

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
        var rows = Csv.ParseWithHeader(await File.ReadAllTextAsync(csv));

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

    // ── Dry-run and the hash ───────────────────────────────────────────────

    [Fact]
    public async Task DryRun_ChangesNothing_AndPrintsBeforeAndAfter()
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
        (await (await _db.ListCollectionNamesAsync()).ToListAsync()).Should().NotContain(LegacyReconciliationAudit.Collection);
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

        var act = () => new LegacyPostingReconciler(_databases, _output.Add).RunAsync(new ReconcileOptions
        {
            CsvPath = csv, Execute = true, ExpectedSha256 = new string('0', 64), Operator = "ops-1"
        });

        await act.Should().ThrowAsync<CsvHashMismatchException>();
        (await SnapshotAsync("ar_balances")).Should().Equal(balances);
        (await Read("cp-applied")).Applications[0].PostedEntryId.Should().BeNull();
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
        partial.LegacyReconciliation.Ticket.Should().Be("FIN-123");
        partial.LegacyReconciliation.ReconciledBy.Should().Be("ops-1");
        partial.LegacyReconciliation.CsvSha256.Should().Be(result.CsvSha256);
        CashPostingLedger.IsLegacy(partial).Should().BeFalse();
        CashPostingLedger.RequiresLegacyReconciliation(partial).Should().BeFalse();

        var audits = await Audits.Find(FilterDefinition<LegacyReconciliationAudit>.Empty).ToListAsync();
        audits.Should().HaveCount(3).And.OnlyContain(a => a.State == AuditState.Completed && a.Operator == "ops-1"
                                                          && a.CsvSha256 == result.CsvSha256 && a.Ticket == "FIN-123");
        var credit = audits.Single(a => a.Id == "tenant-1:cp-partial:0");
        credit.Decision.Should().Be("APPLY_CREDIT");
        credit.Credited.Should().BeTrue();
        credit.Before!.ClosingBalance.Should().Be(1000m);
        credit.After!.ClosingBalance.Should().Be(400m);
        var manual = audits.Single(a => a.Id == "tenant-1:cp-partial:1");
        manual.Credited.Should().BeFalse();
        manual.Before!.ClosingBalance.Should().Be(500m);
        manual.After!.ClosingBalance.Should().Be(500m);

        var run = await _db.GetCollection<LegacyReconciliationRun>(LegacyReconciliationRun.Collection).Find(r => r.Id == result.RunId).SingleAsync();
        run.Operator.Should().Be("ops-1");
        run.FinishedAt.Should().NotBeNull();
        run.Postings.Should().HaveCount(2);

        // Nothing is legacy any more.
        (await new LegacyPostingLister(_databases).ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RunningTwice_ChangesNothingTheSecondTime()
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
    }

    [Fact]
    public async Task ARunInterruptedAfterTheCredit_DoesNotCreditTwice()
    {
        await SeedAsync();
        // The earlier run credited bal-3 and stopped before saving the posting.
        var posting = await Read("cp-applied");
        var balance = await ReadBalance("bal-3");
        CashPostingLedger.Post(balance, CashPostingLedger.CreditEntry(posting, 0, "ops-1", DateTime.UtcNow), posting.PayerType);
        await Balances.ReplaceOneAsync(b => b.Id == "bal-3", balance);
        var csv = await DecideAsync(await ListToCsvAsync(), "reviewed.csv", _ => true, ("cp-applied", 0, "APPLY_CREDIT"));

        var result = await RunAsync(csv, execute: true);

        result.Count(OutcomeKind.Reconciled).Should().Be(1);
        var after = await ReadBalance("bal-3");
        after.PostingEntries.Should().ContainSingle();
        after.TotalCredits.Should().Be(300m);
        (await Read("cp-applied")).Applications[0].PostedEntryId.Should().Be("cash-cp-applied-0");
        (await Audits.Find(a => a.PostingId == "cp-applied").SingleAsync()).Credited.Should().BeFalse();
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
    public void Csv_RoundTripsQuotesCommasAndFormulaGuard()
    {
        var line = Csv.Line(["a,b", "say \"hi\"", "=SUM(A1)", "-5"]);
        var parsed = Csv.Parse("﻿" + line + "\r\n").Single();
        parsed[0].Should().Be("a,b");
        parsed[1].Should().Be("say \"hi\"");
        Csv.Unguard(parsed[2]).Should().Be("=SUM(A1)");
        parsed[3].Should().Be("-5");
    }
}
