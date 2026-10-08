using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using static CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions.ExclusionTestData;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions;

[Collection(MongoRunnerFixture.CollectionName)]
public class MongoExclusionRecordStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoExclusionRecordStore _store = null!;
    private readonly ExclusionScreeningOptions _options = Options();

    public MongoExclusionRecordStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("exclusions_test");
        _store = new MongoExclusionRecordStore(_database, _options);
        await _store.EnsureIndexesAsync();
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    [Fact]
    public async Task EnsureIndexes_CreatesNpiAndLastNameDobIndexes()
    {
        var names = (await (await _database.GetCollection<BsonDocument>(_options.RecordsCollectionName)
                .Indexes.ListAsync()).ToListAsync())
            .Select(i => i["name"].AsString)
            .ToList();

        Assert.Contains("source_sync_npi", names);
        Assert.Contains("source_sync_lastname_dob", names);
        Assert.Contains("source_sync_business_name", names);
    }

    [Fact]
    public async Task ReplaceDataset_SwapsCopies_AndScreeningUsesMongo()
    {
        await SeedLeieAsync(_store, Now.AddDays(-40), LeieRow(last: "OLD", first: "ROW", npi: "1497758544"));
        await SeedLeieAsync(_store, Now.AddDays(-1),
            LeieRow(last: "DOE", first: "JOHN", npi: "1234567893", dob: "19650412"),
            LeieRow(bus: "ACME CLINIC LLC"));

        var status = await _store.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie);
        Assert.Equal(Now.AddDays(-1), status!.LastSuccessfulSyncAt);
        Assert.Equal(2, status.RecordCount);
        var active = status.ActiveSyncId!;
        Assert.Equal(2, await RawCountAsync()); // old copy deleted
        Assert.Single(await _store.FindByLastNameAsync(ExclusionScreeningSource.OigLeie, active, "DOE"));
        Assert.Single(await _store.FindByBusinessNameAsync(ExclusionScreeningSource.OigLeie, active, "ACME CLINIC"));
        Assert.Empty(await _store.FindByNpiAsync(ExclusionScreeningSource.SamGov, active, "1234567893")); // per-source

        var screener = new LocalExclusionListScreener(ExclusionScreeningSource.OigLeie, _store, Wrap(_options),
            NullLogger<LocalExclusionListScreener>.Instance, new FixedTimeProvider(Now));
        var outcome = await screener.ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);
        Assert.True(outcome.Status.WasScreened);
        Assert.True(outcome.IsExcluded);
    }

    [Fact]
    public async Task FailedLoad_RemovesPartialRows_AndKeepsPreviousDataset()
    {
        await SeedLeieAsync(_store, Now.AddDays(-1), LeieRow(last: "DOE", first: "JOHN", npi: "1234567893"));

        static IEnumerable<ExclusionRecord> Broken()
        {
            yield return new ExclusionRecord { LastName = "PARTIAL", Npi = "1497758544" }.Normalize();
            throw new IOException("connection reset mid-file");
        }

        await Assert.ThrowsAsync<IOException>(() => _store.ReplaceDatasetAsync(ExclusionScreeningSource.OigLeie, Broken(),
            new ExclusionDatasetLoadPolicy { SourceUrl = "x", SyncedAt = Now, MinimumRecordCount = 1 }));

        var active = (await _store.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie))!.ActiveSyncId!;
        Assert.Equal(1, await RawCountAsync()); // partial rows removed
        Assert.Single(await _store.FindByNpiAsync(ExclusionScreeningSource.OigLeie, active, "1234567893"));
        Assert.Equal(Now.AddDays(-1), (await _store.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie))!.LastSuccessfulSyncAt);
    }

    [Fact]
    public async Task Lookups_SeeOnlyTheActiveSnapshot_NotInFlightRows()
    {
        await SeedLeieAsync(_store, Now.AddDays(-1), LeieRow(last: "DOE", first: "JOHN", npi: "1234567893"));
        // Rows of an uncommitted load (in flight, or left by a failed cleanup).
        await _database.GetCollection<ExclusionRecord>(_options.RecordsCollectionName).InsertOneAsync(
            new ExclusionRecord { Source = ExclusionScreeningSource.OigLeie, SyncId = "in-flight", LastName = "ROE", FirstName = "MARY", Npi = "1497758544" }.Normalize());

        var screener = new LocalExclusionListScreener(ExclusionScreeningSource.OigLeie, _store, Wrap(_options),
            NullLogger<LocalExclusionListScreener>.Instance, new FixedTimeProvider(Now));
        var outcome = await screener.ScreenAsync(
            new ProviderScreeningRequest { Npi = "1497758544", FirstName = "Mary", LastName = "Roe" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.Empty(outcome.Matches);
    }

    private async Task<long> RawCountAsync() =>
        await _database.GetCollection<BsonDocument>(_options.RecordsCollectionName).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

    [Fact]
    public async Task SyncLease_IsExclusiveUntilReleasedOrExpired()
    {
        Assert.True(await _store.TryAcquireSyncLeaseAsync(ExclusionScreeningSource.SamGov, "pod-a", Now, TimeSpan.FromMinutes(30)));
        Assert.False(await _store.TryAcquireSyncLeaseAsync(ExclusionScreeningSource.SamGov, "pod-b", Now.AddMinutes(1), TimeSpan.FromMinutes(30)));
        Assert.True(await _store.TryAcquireSyncLeaseAsync(ExclusionScreeningSource.SamGov, "pod-b", Now.AddMinutes(31), TimeSpan.FromMinutes(30)));

        await _store.ReleaseSyncLeaseAsync(ExclusionScreeningSource.SamGov, "pod-b");
        Assert.True(await _store.TryAcquireSyncLeaseAsync(ExclusionScreeningSource.SamGov, "pod-a", Now.AddMinutes(32), TimeSpan.FromMinutes(30)));
    }
}
