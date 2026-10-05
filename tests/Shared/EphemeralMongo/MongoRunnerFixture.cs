// Shared by every test project that needs a real mongod. Linked into each
// project with <Compile Include="..\Shared\EphemeralMongo\*.cs" />.
using EphemeralMongo;
using MongoDB.Driver;
using Xunit;

namespace CloudHealthOffice.Testing.Mongo;

/// <summary>Runner options every test project uses.</summary>
public static class TestMongo
{
    /// <summary>
    /// MongoDB 7 Community, the version the tests were written against.
    /// EphemeralMongo 3.x downloads the binaries on first use and caches them
    /// under ~/.local/share/ephemeral-mongo; data directories go under
    /// /tmp/ephemeral-mongo/data and are removed when the runner is disposed.
    /// </summary>
    public static MongoRunnerOptions Options() => new()
    {
        Version = MongoVersion.V7,
        Edition = MongoEdition.Community,
        ConnectionTimeout = TimeSpan.FromSeconds(30),
    };
}

/// <summary>
/// One mongod shared by every test class in the <see cref="MongoCollection"/>
/// collection. Tests isolate themselves by creating a uniquely named database
/// (<see cref="CreateDatabase"/>) and dropping it when they finish.
/// </summary>
public sealed class MongoRunnerFixture : IDisposable
{
    public const string CollectionName = "EphemeralMongo";

    private readonly IMongoRunner _runner;
    private readonly MongoClient _client;

    public MongoRunnerFixture()
    {
        _runner = MongoRunner.Run(TestMongo.Options());
        _client = new MongoClient(_runner.ConnectionString);
    }

    public string ConnectionString => _runner.ConnectionString;

    public IMongoClient Client => _client;

    /// <summary>
    /// A database no other test uses: <c>{prefix}_{guid}</c>. The prefix is
    /// cut to 30 characters because MongoDB rejects names longer than 63.
    /// </summary>
    public IMongoDatabase CreateDatabase(string prefix) =>
        _client.GetDatabase($"{(prefix.Length > 30 ? prefix[..30] : prefix)}_{Guid.NewGuid():N}");

    public Task DropDatabaseAsync(IMongoDatabase database) =>
        _client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);

    // No catch: if mongod cannot be stopped the run must fail, not leak it.
    public void Dispose()
    {
        _client.Dispose();
        _runner.Dispose();
    }
}

[CollectionDefinition(MongoRunnerFixture.CollectionName)]
public sealed class MongoCollection : ICollectionFixture<MongoRunnerFixture>
{
}
