using CloudHealthOffice.Testing.Mongo;
using EligibilityService.HostedServices;
using EligibilityService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// The one-time rewrite of eligibility-service's old 0-5 line-of-business numbers on stored
/// inquiries to the shared names, against a real mongod.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EligibilityLineOfBusinessMigrationMongoTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public EligibilityLineOfBusinessMigrationMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("elig_lob_migration");
        // As Program.cs registers it.
        MongoDB.Bson.Serialization.BsonSerializer.TryRegisterSerializer(EligibilityLineOfBusinessMigrationHostedService.Serializer);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private IMongoCollection<BsonDocument> Raw =>
        _db.GetCollection<BsonDocument>(EligibilityLineOfBusinessMigrationHostedService.Collection);

    private IMongoCollection<EligibilityInquiry> Typed =>
        _db.GetCollection<EligibilityInquiry>(EligibilityLineOfBusinessMigrationHostedService.Collection);

    private static BsonDocument Inquiry(string id, BsonValue lob) => new()
    {
        { "_id", id }, { "TenantId", "t1" }, { "LineOfBusiness", lob },
    };

    [Fact]
    public async Task Every_old_number_becomes_its_shared_name_once()
    {
        await Raw.InsertManyAsync(Enumerable.Range(0, 6).Select(n => Inquiry("n" + n, n)));

        var changed = await EligibilityLineOfBusinessMigrationHostedService.RunAsync(Raw, CancellationToken.None);
        var again = await EligibilityLineOfBusinessMigrationHostedService.RunAsync(Raw, CancellationToken.None);

        Assert.Equal(6, changed);
        Assert.Equal(0, again);
        var stored = (await Raw.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync())
            .ToDictionary(d => d["_id"].AsString, d => d["LineOfBusiness"].AsString);
        Assert.Equal(new[] { "Commercial", "Medicare", "Medicaid", "Exchange", "TRICARE", "VA" },
            Enumerable.Range(0, 6).Select(n => stored["n" + n]));
    }

    [Fact]
    public async Task An_inquiry_not_yet_rewritten_reads_as_the_old_number_meant()
    {
        // Old 3 is Exchange; the shared enum's 3 is Medicaid.
        await Raw.InsertManyAsync(new[] { Inquiry("exchange", 3), Inquiry("commercial", 0) });

        var inquiries = (await Typed.Find(FilterDefinition<EligibilityInquiry>.Empty).ToListAsync()).ToDictionary(i => i.Id);

        Assert.Equal(LineOfBusiness.Exchange, inquiries["exchange"].LineOfBusiness);
        Assert.Equal(LineOfBusiness.Commercial, inquiries["commercial"].LineOfBusiness);
    }

    [Fact]
    public async Task A_new_inquiry_is_stored_by_name()
    {
        await Typed.InsertOneAsync(new EligibilityInquiry { Id = "new", TenantId = "t1", LineOfBusiness = LineOfBusiness.Medicaid });

        var stored = await Raw.Find(new BsonDocument("_id", "new")).SingleAsync();
        Assert.Equal("Medicaid", stored["LineOfBusiness"].AsString);
    }
}
