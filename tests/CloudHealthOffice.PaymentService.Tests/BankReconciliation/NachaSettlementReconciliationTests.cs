using CloudHealthOffice.BankReconciliation;
using CloudHealthOffice.BankReconciliation.Testing;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PaymentService.BankReconciliation;
using PaymentService.Models;
using PaymentService.Repositories;
using Xunit;
using static CloudHealthOffice.BankReconciliation.Testing.Bai2Builder;

namespace CloudHealthOffice.PaymentService.Tests.BankReconciliation;

/// <summary>
/// BAI2 against transmitted NACHA files: settlement by amount and effective
/// date (file reference to break ties), one bank line per file, and ACH
/// returns flagged, never settled.
/// </summary>
public class NachaSettlementReconciliationTests
{
    private const string Tenant = "tenant-nacha";
    private static readonly DateTime Effective = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryNachaSettlementStore _nacha = new();
    private readonly InMemoryBankReconciliationStore _store = new();
    private readonly NachaSettlementMatcher _matcher;
    private readonly BankStatementIngestion _ingestion;
    private readonly BankReconciliationRunner _runner;

    public NachaSettlementReconciliationTests()
    {
        var options = Options.Create(new BankReconciliationOptions { ServiceName = "payment-service" });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Era:OriginatingCompanyId"] = "1999999999" }).Build();
        _matcher = new NachaSettlementMatcher(_nacha, _nacha, Options.Create(new NachaSettlementOptions()), options, config, new BankStatementFreeze(_store));
        _ingestion = new BankStatementIngestion(_store, new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "t"),
            options, NullLogger<BankStatementIngestion>.Instance);
        _runner = new BankReconciliationRunner(_store, new LoggingBankReconciliationEventSink(NullLogger<LoggingBankReconciliationEventSink>.Instance),
            options, NullLogger<BankReconciliationRunner>.Instance);
    }

    private PaymentFileTransmission File(string reference, decimal credits, decimal debits = 0, DateTime? effective = null,
        PaymentFileTransmissionStatus status = PaymentFileTransmissionStatus.Transmitted)
    {
        var file = new PaymentFileTransmission
        {
            Id = PaymentFileTransmission.KeyFor(Tenant, reference), TenantId = Tenant, FileReference = reference,
            PaymentRunId = "run-" + reference, PaymentRunNumber = "PR-" + reference, TotalCreditAmount = credits, TotalDebitAmount = debits,
            EffectiveEntryDate = effective ?? Effective, Status = status,
        };
        _nacha.Transmissions.Add(file);
        return file;
    }

    private async Task<BankReconciliationSummary> Reconcile(Bai2Builder bai2)
    {
        var ingest = await _ingestion.IngestAsync(Tenant, bai2.Bytes(), null, "treasury-1");
        Assert.True(ingest.IsAccepted, string.Join("; ", ingest.Errors));
        return await _runner.RunAsync(ingest.Transactions, _matcher);
    }

    [Fact]
    public async Task SettlementDebit_EqualToOneTransmittedFile_SettlesIt()
    {
        File("FFS-PR-1", 15234.56m);
        File("FFS-PR-2", 999.00m);

        var item = (await Reconcile(Single(new Tx("466", 1523456, Text: "ACH SETTLEMENT CO ID 1999999999")))).Items.Single();

        Assert.Equal(ReconciliationStatus.Matched, item.Status);
        Assert.Equal("NachaSettlement", item.Kind);
        Assert.Equal(new[] { "FFS-PR-1" }, item.MatchedEntityIds);
        Assert.Contains("Company id confirmed", item.Reason);
        var settlement = await _nacha.GetAsync(Tenant, "FFS-PR-1");
        Assert.NotNull(settlement);
        Assert.Equal(15234.56m, settlement!.Amount);
        Assert.Equal(item.Id, settlement.ReconciliationItemId);
        var message = Assert.Single(settlement.GlOutbox);
        Assert.Equal(CloudHealthOffice.Finance.Contracts.GlEventTypes.PaymentFileSettled, message.Type);
        var payload = System.Text.Json.JsonSerializer.Deserialize<CloudHealthOffice.Finance.Contracts.PaymentFileSettledEvent>(
            message.PayloadJson, CloudHealthOffice.Finance.Contracts.GlEventTypes.Json)!;
        Assert.Equal(settlement.Amount, payload.Amount);
        Assert.Equal(settlement.BankTransactionId, payload.BankTransactionId);
        Assert.Equal(settlement.BankAsOfDate.Date, payload.BankAsOfDate);
        Assert.Equal(settlement.PaymentRunId, payload.PaymentRunId);
        Assert.False(string.IsNullOrWhiteSpace(payload.TransmissionEventId));
        Assert.Null(settlement.GlAccountingLimitation);

    }

    [Fact]
    public async Task TwoFilesWithTheSameTotal_AreAmbiguous_UnlessTheLineNamesOne()
    {
        File("FFS-PR-10", 500m);
        File("FFS-PR-11", 500m);

        var ambiguous = (await Reconcile(Single(new Tx("466", 50000)))).Items.Single();
        Assert.Equal(ReconciliationStatus.Suspense, ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates.Count);

        var named = (await Reconcile(new Bai2Builder { FileId = "2", Groups = [new Group { Accounts = [new Account { Transactions = [new Tx("466", 50000, CustomerReference: "FFS-PR-11")] }] }] })).Items.Single();
        Assert.Equal(ReconciliationStatus.Matched, named.Status);
        Assert.Equal(new[] { "FFS-PR-11" }, named.MatchedEntityIds);
    }

    [Fact]
    public async Task AFileIsSettledByOneLineOnly()
    {
        File("FFS-PR-20", 250m);

        var summary = await Reconcile(Single(new Tx("466", 25000), new Tx("466", 25000)));

        Assert.Equal(ReconciliationStatus.Matched, summary.Items[0].Status);
        Assert.Equal(ReconciliationStatus.Suspense, summary.Items[1].Status);
    }

    [Fact]
    public async Task NoFile_OutsideTheWindow_OrNotTransmitted_GoesToSuspense()
    {
        File("FFS-PR-30", 100m, effective: Effective.AddDays(-10));
        File("FFS-PR-31", 100m, status: PaymentFileTransmissionStatus.NeedsReview);

        var item = (await Reconcile(Single(new Tx("466", 10000)))).Items.Single();

        Assert.Equal(ReconciliationStatus.Suspense, item.Status);
        Assert.Empty(item.Candidates);
        Assert.Null(await _nacha.GetAsync(Tenant, "FFS-PR-30"));
    }

    [Fact]
    public async Task DebitFile_IsSettledByASettlementCredit()
    {
        File("PREM-DRAFT-1", credits: 0, debits: 870.10m);
        var item = (await Reconcile(Single(new Tx("166", 87010)))).Items.Single();
        Assert.Equal(ReconciliationStatus.Matched, item.Status);
        var settlement = await _nacha.GetAsync(Tenant, "PREM-DRAFT-1");
        Assert.NotNull(settlement);
        Assert.Empty(settlement.GlOutbox);
        Assert.NotNull(settlement.GlAccountingLimitation);
    }

    [Fact]
    public async Task AchReturn_IsFlaggedWithRecentFiles_AndSettlesNothing()
    {
        File("FFS-PR-40", 300m);

        var item = (await Reconcile(Single(new Tx("266", 30000, Text: "RETURN R03 NO ACCOUNT")))).Items.Single();

        Assert.Equal(ReconciliationStatus.Return, item.Status);
        Assert.Equal("AchReturn", item.Kind);
        Assert.Contains(item.Candidates, c => c.Contains("FFS-PR-40"));
        Assert.Null(await _nacha.GetAsync(Tenant, "FFS-PR-40"));
    }

    [Fact]
    public async Task CheckAndOtherLines_AreIgnored()
    {
        Assert.Equal(ReconciliationStatus.Ignored, (await Reconcile(Single(new Tx("475", 100, CustomerReference: "1001")))).Items.Single().Status);
    }

    [Fact]
    public async Task ReRunningTheStatement_DoesNotSettleAgain()
    {
        File("FFS-PR-50", 42m);
        var bai2 = Single(new Tx("466", 4200));

        await Reconcile(bai2);
        var again = await Reconcile(bai2);

        Assert.Equal(1, again.AlreadyDecided);
    }

    [Fact]
    public async Task FrozenStatement_SettlesNothing_AtTheCommitPoint()
    {
        var file = File("FFS-PR-70", 20m);
        var ingest = await _ingestion.IngestAsync(Tenant, Single(new Tx("466", 2000)).Bytes(), null, "treasury-1");
        await new BankStatementSupersessionService(_store, Options.Create(new BankReconciliationOptions { ServiceName = "payment-service" }),
            NullLogger<BankStatementSupersessionService>.Instance).RequestAsync(Tenant, ingest.Statement.Id, "corrected file arriving", "alice");

        await Assert.ThrowsAsync<StatementFrozenException>(() => _matcher.SettleAsync(ingest.Transactions.Single(), file, "treasury-2"));
        Assert.Null(await _nacha.GetAsync(Tenant, "FFS-PR-70"));
        Assert.Equal(1, (await _runner.RunAsync(ingest.Transactions, _matcher)).Frozen);
    }

    [Fact]
    public async Task SuspenseLine_SettledByAPerson()
    {
        var file = File("FFS-PR-60", 10m);
        var item = (await Reconcile(Single(new Tx("466", 1100)))).Items.Single();
        Assert.Equal(ReconciliationStatus.Suspense, item.Status);
        var tx = (await _store.GetTransactionsAsync(Tenant, item.StatementId)).Single();

        var decision = await _matcher.SettleAsync(tx, file, "treasury-2");
        var resolved = await _runner.ResolveAsync(Tenant, item.Id, false, "Bank fee netted into settlement", "treasury-2",
            decision.MatchedEntityType, decision.MatchedEntityIds);

        Assert.Equal(ReconciliationStatus.Resolved, resolved.Status);
        Assert.Equal("treasury-2", (await _nacha.GetAsync(Tenant, "FFS-PR-60"))!.SettledBy);
    }
}

/// <summary>The Mongo settlement store and candidate query against a real mongod.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class NachaSettlementMongoTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public NachaSettlementMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("payment_settle");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    [Fact]
    public async Task Candidates_AreTransmittedFilesInTheWindow_AndSettlementIsInsertOnce()
    {
        var transmissions = new PaymentFileTransmissionRepositoryMongo(_db);
        var day = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (reference, status, effective) in new[]
                 {
                     ("F1", PaymentFileTransmissionStatus.Transmitted, day),
                     ("F2", PaymentFileTransmissionStatus.Pending, day),
                     ("F3", PaymentFileTransmissionStatus.Transmitted, day.AddDays(-20)),
                 })
        {
            Assert.True(await transmissions.TryInsertAsync(new PaymentFileTransmission
            {
                TenantId = "t1", FileReference = reference, PaymentRunId = "r", PaymentRunNumber = "PR", FileName = reference,
                ApprovedSha256 = new string('a', 64), ApprovedBy = "u", Status = status, EffectiveEntryDate = effective, TotalCreditAmount = 5m,
            }));
        }

        var source = new NachaSettlementCandidateSourceMongo(_db);
        var found = await source.TransmittedAsync("t1", day.AddDays(-3), day.AddDays(1));
        Assert.Equal(new[] { "F1" }, found.Select(f => f.FileReference));
        Assert.Empty(await source.TransmittedAsync("t2", day.AddDays(-3), day.AddDays(1)));

        var store = new NachaSettlementStoreMongo(_db);
        var settlement = new NachaFileSettlement { Id = NachaFileSettlement.KeyFor("t1", "F1"), TenantId = "t1", FileReference = "F1", BankTransactionId = "tx1" };
        Assert.True(await store.TryInsertAsync(settlement));
        Assert.False(await store.TryInsertAsync(new NachaFileSettlement { Id = settlement.Id, TenantId = "t1", FileReference = "F1", BankTransactionId = "tx2" }));
        Assert.Equal("tx1", (await store.GetAsync("t1", "F1"))!.BankTransactionId);
        Assert.Null(await store.GetAsync("t2", "F1"));
    }
}
