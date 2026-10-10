using CloudHealthOffice.BankReconciliation.Bai2;
using CloudHealthOffice.BankReconciliation.Testing;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using static CloudHealthOffice.BankReconciliation.Testing.Bai2Builder;

namespace CloudHealthOffice.BankReconciliation.Tests;

/// <summary>Parser edge cases from review: '/'-delimited files, a 16 spilling into an 88, custom type codes.</summary>
public class Bai2ParserReviewTests
{
    [Fact]
    public void FileWithNoLineBreaksAndNoRecordLength_IsSplitOnRecordDelimiters()
    {
        var content = Single(
                new Tx("165", 1000, BankReference: "B1", CustomerReference: "C1", Text: "TEXT WITH / SLASH/"),
                new Tx("169", 2000, CustomerReference: "C2"))
            .Build().Replace("\n", string.Empty);

        var file = Bai2Parser.Parse(content);

        file.AllTransactions.Select(t => t.Amount).Should().Equal(1000, 2000);
        file.AllTransactions.First().Text.Should().Be("TEXT WITH / SLASH");
    }

    [Fact]
    public void SixteenThatSpillsIntoAnEightyEightBeforeItsText_KeepsItsCustomerReference()
    {
        const string content =
            "01,B,R,261010,0600,1,,,2/\n" +
            "02,R,B,1,261009,,USD,2/\n" +
            "03,99887766,USD/\n" +
            "16,165,1000,0,BANKREF1\n" +
            "88,CUSTREF1,PAYER TEXT\n" +
            "49,1000,4/\n" +
            "98,1000,1,6/\n" +
            "99,1000,1,8/\n";

        var t = Bai2Parser.Parse(content).AllTransactions.Single();

        t.BankReference.Should().Be("BANKREF1");
        t.CustomerReference.Should().Be("CUSTREF1");
        t.Text.Should().Be("PAYER TEXT");
    }

    [Fact]
    public void CustomTypeCodes_AreConsultedBeforeTheStandardRanges()
    {
        var content = Single(new Tx("901", 100, CustomerReference: "X"), new Tx("950", 200, CustomerReference: "Y")).Build();

        Bai2Parser.Parse(content).AllTransactions.Should().OnlyContain(t => t.Direction == Bai2Direction.Status);
        var custom = Bai2Parser.Parse(content, new Bai2ParseOptions { CreditTypeCodes = ["901"], DebitTypeCodes = ["950"] });
        custom.AllTransactions.Select(t => t.Direction).Should().Equal(Bai2Direction.Credit, Bai2Direction.Debit);
    }
}

/// <summary>Supersession of a corrected file, and interim/final report de-duplication.</summary>
public class SupersessionAndInterimTests
{
    private const string Tenant = "tenant-review";
    private readonly InMemoryBankReconciliationStore _store = new();
    private readonly IFieldProtector _protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "review");

    private IOptions<BankReconciliationOptions> Options(Action<BankReconciliationOptions>? edit = null)
    {
        var o = new BankReconciliationOptions { ServiceName = "svc" };
        edit?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private BankStatementIngestion Ingestion(Action<BankReconciliationOptions>? edit = null) =>
        new(_store, _protector, Options(edit), NullLogger<BankStatementIngestion>.Instance);

    private BankReconciliationRunner Runner(Action<BankReconciliationOptions>? edit = null) =>
        new(_store, new RecordingSink(), Options(edit), NullLogger<BankReconciliationRunner>.Instance);

    private BankStatementSupersessionService Supersessions() => new(_store, Options(), NullLogger<BankStatementSupersessionService>.Instance);

    private static Bai2Builder Statement(long amount, string modifier = "2", string asOf = "261009", string fileId = "7") => new()
    {
        FileId = fileId,
        Groups = [new Group { Modifier = modifier, AsOfDate = asOf, Accounts = [new Account { Transactions = [new Tx("165", amount, BankReference: "BNKREF-55", CustomerReference: "C")] }] }],
    };

    [Fact]
    public async Task CorrectedFile_WithTheSameBankId_IsAcceptedOnlyAfterTwoPeopleSupersede()
    {
        var original = await Ingestion().IngestAsync(Tenant, Statement(1000).Bytes(), null, "u1");
        await Runner().RunAsync(original.Transactions, new CountingMatcher { Default = ReconciliationStatus.Suspense });

        var corrected = Statement(1100);
        (await Ingestion().IngestAsync(Tenant, corrected.Bytes(), null, "u1")).Status.Should().Be(BankStatementIngestStatus.Rejected);

        await Supersessions().RequestAsync(Tenant, original.Statement.Id, "Bank re-sent with corrected amount", "alice");
        await FluentActions.Invoking(() => Supersessions().ApproveAsync(Tenant, original.Statement.Id, "alice"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*second person*");
        await Supersessions().ApproveAsync(Tenant, original.Statement.Id, "bob");

        // The open line of the old statement is dismissed.
        (await _store.ListItemsByStatementAsync(Tenant, original.Statement.Id)).Should().OnlyContain(i => i.Status == ReconciliationStatus.Dismissed);

        // The same corrected bytes that were refused before are now accepted.
        var accepted = await Ingestion().IngestAsync(Tenant, corrected.Bytes(), null, "u1");
        accepted.Status.Should().Be(BankStatementIngestStatus.Ingested);
        (await _store.GetFileAsync(Tenant, accepted.Statement.Id))!.BankFileKey.Should().EndWith("#2");
        (await Supersessions().GetAsync(Tenant, original.Statement.Id))!.ReplacedByStatementId.Should().Be(accepted.Statement.Id);

        // Re-sending the superseded file reconciles nothing.
        var again = await Ingestion().IngestAsync(Tenant, Statement(1000).Bytes(), null, "u1");
        again.IsSuperseded.Should().BeTrue();
        again.Transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Supersession_IsRefused_OnceALineWasActedOn()
    {
        var original = await Ingestion().IngestAsync(Tenant, Statement(1000).Bytes(), null, "u1");
        await Runner().RunAsync(original.Transactions, new CountingMatcher { Default = ReconciliationStatus.Applied });

        await FluentActions.Invoking(() => Supersessions().RequestAsync(Tenant, original.Statement.Id, "corrected", "alice"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*already applied, matched, settled or resolved*");
    }

    [Fact]
    public async Task FinalReport_DoesNotDecideALineTheInterimReportAlreadyDecided()
    {
        var matcher = new CountingMatcher { Default = ReconciliationStatus.Matched };
        var interim = await Ingestion().IngestAsync(Tenant, Statement(1000, modifier: "3", fileId: "1").Bytes(), null, null);
        var final = await Ingestion().IngestAsync(Tenant, Statement(1000, modifier: "4", fileId: "2").Bytes(), null, null);

        (await Runner().RunAsync(interim.Transactions, matcher)).Matched.Should().Be(1);
        var second = await Runner().RunAsync(final.Transactions, matcher);

        second.Ignored.Should().Be(1);
        second.Items.Single().Reason.Should().Contain("already reconciled");
        matcher.Calls.Should().Be(1);
    }

    [Fact]
    public async Task InterimReports_CanBeSkipped()
    {
        var matcher = new CountingMatcher { Default = ReconciliationStatus.Matched };
        var interim = await Ingestion().IngestAsync(Tenant, Statement(1000, modifier: "3", fileId: "1").Bytes(), null, null);
        var final = await Ingestion().IngestAsync(Tenant, Statement(1000, modifier: "4", fileId: "2").Bytes(), null, null);

        (await Runner(o => o.InterimReports = InterimReportHandling.Skip).RunAsync(interim.Transactions, matcher)).Ignored.Should().Be(1);
        (await Runner(o => o.InterimReports = InterimReportHandling.Skip).RunAsync(final.Transactions, matcher)).Matched.Should().Be(1);
        matcher.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CustomTypeCodes_FromOptions_ReachTheTransactions()
    {
        var bai2 = Single(new Tx("901", 100, CustomerReference: "X"));
        var result = await Ingestion(o => o.CreditTypeCodes = ["901"]).IngestAsync(Tenant, bai2.Bytes(), null, null);
        result.Transactions.Single().Direction.Should().Be(Bai2Direction.Credit);
    }
}

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoReviewStoreTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly MongoBankReconciliationStore _store;

    public MongoReviewStoreTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("bank_review");
        _store = new MongoBankReconciliationStore(_db);
    }

    public Task InitializeAsync() => _store.EnsureIndexesAsync();
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    [Fact]
    public async Task DedupeLookup_AndSupersessions_Work()
    {
        await _store.TryCreateItemAsync(new ReconciliationItem { Id = "svc:a", TenantId = "t1", Service = "svc", TransactionId = "a", DedupeKey = "K", Status = ReconciliationStatus.Matched, CreatedAt = DateTime.UtcNow });
        await _store.TryCreateItemAsync(new ReconciliationItem { Id = "svc:b", TenantId = "t1", Service = "svc", TransactionId = "b", DedupeKey = "K2", Status = ReconciliationStatus.Dismissed, CreatedAt = DateTime.UtcNow });

        (await _store.FindDecidedByDedupeKeyAsync("t1", "svc", "K", "z"))!.Id.Should().Be("svc:a");
        (await _store.FindDecidedByDedupeKeyAsync("t1", "svc", "K", "a")).Should().BeNull();
        (await _store.FindDecidedByDedupeKeyAsync("t1", "svc", "K2", "z")).Should().BeNull("dismissed lines are not duplicates");

        var s = new BankStatementSupersession { Id = BankStatementSupersession.IdFor("t1", "st1"), TenantId = "t1", StatementId = "st1", Status = SupersessionStatus.Requested };
        (await _store.TryCreateSupersessionAsync(s)).Should().BeTrue();
        (await _store.TryCreateSupersessionAsync(s)).Should().BeFalse();
        s.Status = SupersessionStatus.Approved;
        (await _store.TryUpdateSupersessionAsync(s, SupersessionStatus.Requested)).Should().BeTrue();
        (await _store.TryUpdateSupersessionAsync(s, SupersessionStatus.Requested)).Should().BeFalse();
        (await _store.GetSupersessionAsync("t1", "st1"))!.Status.Should().Be(SupersessionStatus.Approved);
        (await _store.GetSupersessionAsync("t2", "st1")).Should().BeNull();
    }

    [Fact]
    public async Task CorrectedFiles_GetANumberedBankKey()
    {
        BankStatementFile File(string sha, string key) => new()
        {
            Id = BankStatementFile.IdFor("t1", sha), TenantId = "t1", Sha256 = sha, Status = BankStatementStatus.Accepted,
            BankFileKeyBase = "B|R|20261010|1", BankFileKey = key, ReceivedAt = DateTime.UtcNow,
        };
        (await _store.TryInsertFileAsync(File("a", "B|R|20261010|1"))).Should().BeTrue();
        (await _store.TryInsertFileAsync(File("b", "B|R|20261010|1"))).Should().BeFalse();
        (await _store.TryInsertFileAsync(File("b", "B|R|20261010|1#2"))).Should().BeTrue();
        (await _store.ListAcceptedByBankFileKeyAsync("t1", "B|R|20261010|1")).Select(f => f.Sha256).Should().Equal("a", "b");
    }
}
