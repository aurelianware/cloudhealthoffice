using CloudHealthOffice.Testing.Mongo;
using FhirService.Services.ApiUsage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.ApiUsage;

/// <summary>The durable API usage ledger on a real mongod (Q-20).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoApiDisclosureLedgerTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoApiDisclosureLedger _ledger = null!;
    private static readonly DateOnly Day = new(2026, 3, 1);
    private static readonly DateOnly Year = new(2026, 1, 1);

    public MongoApiDisclosureLedgerTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("api_disclosures");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName })
            .Build();
        _ledger = new MongoApiDisclosureLedger(_mongo.Client, configuration);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static ApiDisclosure D(string patient, string client = "app", DateOnly? day = null, string tenant = "t",
        InteroperabilityApi api = InteroperabilityApi.PatientAccess) => new(tenant, api, patient, client, day ?? Day);

    [Fact]
    public async Task Repeats_on_one_day_keep_one_row_and_count_the_requests()
    {
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _ledger.RecordAsync(D("p1"))));

        var rows = await _database.GetCollection<BsonDocument>(MongoApiDisclosureLedger.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        rows.Should().ContainSingle();
        rows[0]["requests"].ToInt32().Should().Be(10);
        rows[0].Contains("firstAt").Should().BeTrue();
    }

    [Fact]
    public async Task Counts_unique_patients_and_those_sent_more_than_once()
    {
        await _ledger.RecordAsync(D("p1"));
        await _ledger.RecordAsync(D("p1"));
        await _ledger.RecordAsync(D("p2"));
        await _ledger.RecordAsync(D("p2", day: Day.AddDays(3)));
        await _ledger.RecordAsync(D("p3"));
        await _ledger.RecordAsync(D("p3", client: "other-app"));

        (await _ledger.CountAsync("t", InteroperabilityApi.PatientAccess, Year, Year.AddYears(1)))
            .Should().Be(new ApiUsageCounts(3, 2));
    }

    [Fact]
    public async Task Counts_keep_to_the_tenant_api_and_period()
    {
        await _ledger.RecordAsync(D("p1", day: new DateOnly(2026, 12, 31)));
        await _ledger.RecordAsync(D("p2", day: new DateOnly(2027, 1, 1)));
        await _ledger.RecordAsync(D("p3", tenant: "other"));
        await _ledger.RecordAsync(D("p4", api: InteroperabilityApi.ProviderAccess));

        (await _ledger.CountAsync("t", InteroperabilityApi.PatientAccess, Year, Year.AddYears(1)))
            .Should().Be(new ApiUsageCounts(1, 0));
        (await _ledger.CountAsync("t", InteroperabilityApi.PatientAccess, new DateOnly(2030, 1, 1), new DateOnly(2031, 1, 1)))
            .Should().Be(new ApiUsageCounts(0, 0));
        (await _ledger.EarliestDayAsync("t", InteroperabilityApi.PatientAccess)).Should().Be(new DateOnly(2026, 12, 31));
        (await _ledger.EarliestDayAsync("nobody", InteroperabilityApi.PatientAccess)).Should().BeNull();
    }
}
