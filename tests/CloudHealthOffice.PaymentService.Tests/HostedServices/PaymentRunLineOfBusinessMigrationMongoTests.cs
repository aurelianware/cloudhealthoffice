using CloudHealthOffice.Testing.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using PaymentService.HostedServices;
using PaymentService.Models;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.HostedServices;

/// <summary>
/// The one-time rewrite of payment-service's old 0-3 line-of-business numbers to the shared
/// names, against a real mongod, and the runs read back through the shared serializer.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PaymentRunLineOfBusinessMigrationMongoTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public PaymentRunLineOfBusinessMigrationMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("payment_lob_migration");
        // As Program.cs registers it.
        MongoDB.Bson.Serialization.BsonSerializer.TryRegisterSerializer(PaymentRunLineOfBusinessMigrationHostedService.Serializer);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private IMongoCollection<BsonDocument> Raw =>
        _db.GetCollection<BsonDocument>(PaymentRunLineOfBusinessMigrationHostedService.Collection);

    private static BsonDocument Run(string id, BsonValue lob) => new()
    {
        { "_id", id },
        { "TenantId", "t1" },
        { "PaymentRunNumber", "PR-" + id },
        { "Criteria", new BsonDocument { { "LineOfBusiness", lob } } },
    };

    [Fact]
    public async Task Old_numbers_become_the_shared_names_and_read_back_as_the_shared_enum()
    {
        await Raw.InsertManyAsync(new[]
        {
            Run("commercial", 0), Run("medicare", 1), Run("medicaid", new BsonInt64(2)), Run("marketplace", 3),
            Run("named", "Marketplace"), Run("none", BsonNull.Value),
        });

        var changed = await PaymentRunLineOfBusinessMigrationHostedService.RunAsync(Raw, CancellationToken.None);

        Assert.Equal(5, changed);
        var stored = (await Raw.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync())
            .ToDictionary(d => d["_id"].AsString, d => d["Criteria"]["LineOfBusiness"]);
        Assert.Equal("Commercial", stored["commercial"]);
        Assert.Equal("Medicare", stored["medicare"]);
        Assert.Equal("Medicaid", stored["medicaid"]);
        Assert.Equal("Exchange", stored["marketplace"]);
        Assert.Equal("Exchange", stored["named"]);
        Assert.Equal(BsonNull.Value, stored["none"]);

        var typed = _db.GetCollection<PaymentRun>(PaymentRunLineOfBusinessMigrationHostedService.Collection);
        var medicare = await typed.Find(r => r.Id == "medicare").SingleAsync();
        Assert.Equal(LineOfBusiness.Medicare, medicare.Criteria.LineOfBusiness);
        var none = await typed.Find(r => r.Id == "none").SingleAsync();
        Assert.Null(none.Criteria.LineOfBusiness);
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        // Run twice: 1 (old Medicare) must not drift on to Medicaid.
        await Raw.InsertManyAsync(new[] { Run("medicare", 1), Run("commercial", 0) });

        await PaymentRunLineOfBusinessMigrationHostedService.RunAsync(Raw, CancellationToken.None);
        var second = await PaymentRunLineOfBusinessMigrationHostedService.RunAsync(Raw, CancellationToken.None);

        Assert.Equal(0, second);
        var medicare = await Raw.Find(new BsonDocument("_id", "medicare")).SingleAsync();
        Assert.Equal("Medicare", medicare["Criteria"]["LineOfBusiness"]);
    }

    [Fact]
    public async Task A_run_not_yet_rewritten_reads_as_the_old_number_meant()
    {
        // Old 1 is Medicare; the shared enum's 1 is Commercial. Reads must not wait for the rewrite.
        await Raw.InsertManyAsync(new[] { Run("medicare", 1), Run("marketplace", new BsonInt64(3)), Run("junk", 9) });

        var typed = _db.GetCollection<PaymentRun>(PaymentRunLineOfBusinessMigrationHostedService.Collection);
        var runs = (await typed.Find(FilterDefinition<PaymentRun>.Empty).ToListAsync()).ToDictionary(r => r.Id);

        Assert.Equal(LineOfBusiness.Medicare, runs["medicare"].Criteria.LineOfBusiness);
        Assert.Equal(LineOfBusiness.Exchange, runs["marketplace"].Criteria.LineOfBusiness);
        Assert.Equal(LineOfBusiness.Unknown, runs["junk"].Criteria.LineOfBusiness);
    }

    [Fact]
    public async Task A_new_run_is_stored_by_name()
    {
        var typed = _db.GetCollection<PaymentRun>(PaymentRunLineOfBusinessMigrationHostedService.Collection);
        await typed.InsertOneAsync(new PaymentRun
        {
            Id = "new", TenantId = "t1", PaymentRunNumber = "PR-new",
            Criteria = new PaymentRunCriteria { LineOfBusiness = LineOfBusiness.Exchange },
        });

        var stored = await Raw.Find(new BsonDocument("_id", "new")).SingleAsync();
        Assert.Equal("Exchange", stored["Criteria"]["LineOfBusiness"]);
    }
}
