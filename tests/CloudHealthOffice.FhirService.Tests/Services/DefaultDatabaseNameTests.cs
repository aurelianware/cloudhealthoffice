using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using EphemeralMongo;
using FhirService.Services;
using FhirService.Services.PayerToPayer.Ingestion;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// With no <c>MongoDb:DatabaseName</c> (the docker-compose setups), the host's
/// <see cref="MongoDbConnectionFactory"/> uses <c>CloudHealthOffice</c>, while the
/// DTR and payer-to-payer stores defaulted to <c>cloudhealthoffice</c>. MongoDB
/// refuses two databases whose names differ only in case on one server
/// (DatabaseDifferCase), so whichever store wrote second failed. They now default
/// to the host's database. Runs on its own mongod: the default database name is
/// shared server state.
/// </summary>
public sealed class DefaultDatabaseNameTests : IDisposable
{
    private readonly IMongoRunner _runner = MongoRunner.Run(TestMongo.Options());
    private readonly MongoClient _client;
    private readonly IConfiguration _noDatabaseName = new ConfigurationBuilder().Build();

    public DefaultDatabaseNameTests() => _client = new MongoClient(_runner.ConnectionString);

    public void Dispose()
    {
        _client.Dispose();
        _runner.Dispose();
    }

    private IMongoDatabase HostDatabaseWithData()
    {
        var host = new MongoDbConnectionFactory(_client, new HttpContextAccessor(), _noDatabaseName).GetDatabase();
        host.GetCollection<BsonDocument>("Claims").InsertOne(new BsonDocument("_id", "c1"));
        return host;
    }

    private static async Task<List<string>> CollectionsAsync(IMongoDatabase database) =>
        await (await database.ListCollectionNamesAsync()).ToListAsync();

    [Fact]
    public async Task DtrStore_WithNoDatabaseName_UsesTheHostDatabase()
    {
        var host = HostDatabaseWithData();

        var act = () => new MongoDtrResourceStore(_client, _noDatabaseName, NullLogger<MongoDtrResourceStore>.Instance);

        act.Should().NotThrow("the store must not open a database that differs from the host's only by case");
        (await CollectionsAsync(host)).Should().Contain(MongoDtrResourceStore.QuestionnaireCollectionName);
    }

    [Fact]
    public async Task PayerToPayerStore_WithNoDatabaseName_UsesTheHostDatabase()
    {
        var host = HostDatabaseWithData();

        var act = () => new MongoPayerToPayerImportRepository(
            _client, _noDatabaseName, NullLogger<MongoPayerToPayerImportRepository>.Instance);

        act.Should().NotThrow("the store must not open a database that differs from the host's only by case");
        (await CollectionsAsync(host)).Should().Contain(MongoPayerToPayerImportRepository.LedgerCollectionName);
    }
}
