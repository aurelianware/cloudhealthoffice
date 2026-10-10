using CloudHealthOffice.Testing.Mongo;
using MongoDB.Driver;

namespace CloudHealthOffice.BankReconciliation.Tests;

/// <summary>The Mongo store against a real mongod: insert-only files, the unique bank file key, conditional item updates.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoBankReconciliationStoreTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly MongoBankReconciliationStore _store;

    public MongoBankReconciliationStoreTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("bank_recon");
        _store = new MongoBankReconciliationStore(_db);
    }

    public Task InitializeAsync() => _store.EnsureIndexesAsync();
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static BankStatementFile File(string tenant, string sha, string? key, BankStatementStatus status = BankStatementStatus.Accepted) => new()
    {
        Id = BankStatementFile.IdFor(tenant, sha), TenantId = tenant, Sha256 = sha, BankFileKey = key, BankFileKeyBase = key, Status = status,
        ProtectedContent = "enc:v2:x", ReceivedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Files_AreInsertOnly_AndTheBankFileKeyIsUniquePerTenant()
    {
        (await _store.TryInsertFileAsync(File("t1", "aa", "B|R|20261010|1"))).Should().BeTrue();
        (await _store.TryInsertFileAsync(File("t1", "aa", "B|R|20261010|1"))).Should().BeFalse();
        (await _store.TryInsertFileAsync(File("t1", "bb", "B|R|20261010|1"))).Should().BeFalse();
        (await _store.TryInsertFileAsync(File("t2", "bb", "B|R|20261010|1"))).Should().BeTrue();

        // Rejected files carry no key, so any number of them can be stored.
        (await _store.TryInsertFileAsync(File("t1", "cc", null, BankStatementStatus.Rejected))).Should().BeTrue();
        (await _store.TryInsertFileAsync(File("t1", "dd", null, BankStatementStatus.Rejected))).Should().BeTrue();

        (await _store.ListAcceptedByBankFileKeyAsync("t1", "B|R|20261010|1")).Single().Sha256.Should().Be("aa");
        (await _store.GetFileAsync("t2", BankStatementFile.IdFor("t1", "aa"))).Should().BeNull();
        (await _store.ListFilesAsync("t1", 10)).Should().HaveCount(3).And.OnlyContain(f => f.ProtectedContent == string.Empty);
    }

    [Fact]
    public async Task Transactions_InsertIsIdempotent()
    {
        var txs = Enumerable.Range(0, 3).Select(i => new BankTransaction { Id = $"s1-0-0-{i}", TenantId = "t1", StatementId = "s1", LineNumber = i + 4 }).ToList();
        await _store.InsertTransactionsAsync(txs);
        await _store.InsertTransactionsAsync(txs);
        (await _store.GetTransactionsAsync("t1", "s1")).Select(t => t.LineNumber).Should().Equal(4, 5, 6);
    }

    [Fact]
    public async Task Items_AreCreatedOnce_AndUpdatedConditionally()
    {
        var item = new ReconciliationItem { Id = "svc:tx1", TenantId = "t1", Service = "svc", Status = ReconciliationStatus.Pending, CreatedAt = DateTime.UtcNow };
        (await _store.TryCreateItemAsync(item)).Should().BeTrue();
        (await _store.TryCreateItemAsync(item.Copy())).Should().BeFalse();

        var a = (await _store.GetItemAsync("t1", "svc:tx1"))!;
        var b = (await _store.GetItemAsync("t1", "svc:tx1"))!;
        a.Status = ReconciliationStatus.Suspense;
        (await _store.TryUpdateItemAsync(a, a.Version)).Should().BeTrue();
        b.Status = ReconciliationStatus.Matched;
        (await _store.TryUpdateItemAsync(b, b.Version)).Should().BeFalse("b read a version that is gone");

        (await _store.ListItemsAsync("t1", "svc", ReconciliationStatus.Suspense, null, 10)).Should().ContainSingle();
        (await _store.ListItemsAsync("t1", "other", null, null, 10)).Should().BeEmpty();
    }
}
