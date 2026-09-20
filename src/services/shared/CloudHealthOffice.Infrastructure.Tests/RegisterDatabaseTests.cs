using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests;

/// <summary>
/// Covers provider selection in AddChoInfrastructure. MongoDB is the default so deployments
/// stay cloud-agnostic; the Cosmos DB native SDK is opt-in via Database:Provider=CosmosDb.
/// </summary>
public class RegisterDatabaseTests
{
    private const string MongoConnectionString = "mongodb://localhost:27017";
    private const string CosmosEndpoint = "https://example.documents.azure.com:443/";

    // A syntactically valid base-64 key; CosmosClient parses the key at construction time.
    private const string CosmosKey = "dGVzdC1jb3Ntb3Mta2V5LXZhbHVlLWZvci11bml0LXRlc3Rz";

    private static IServiceCollection BuildServices(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();

        // The real host registers IConfiguration; MongoDbConnectionFactory depends on it.
        services.AddSingleton<IConfiguration>(configuration);

        services.AddChoInfrastructure(configuration);
        return services;
    }

    [Fact]
    public void Default_WithMongoConnectionString_RegistersMongoClient()
    {
        var services = BuildServices(("MongoDb:ConnectionString", MongoConnectionString));

        services.Should().Contain(d => d.ServiceType == typeof(IMongoClient));
        services.Should().Contain(d => d.ServiceType == typeof(IMongoDatabase));
        services.Should().Contain(d => d.ServiceType == typeof(MongoDbConnectionFactory));
    }

    [Fact]
    public void WithoutTenantScoping_MongoDatabaseIsSingleton()
    {
        // Services register singleton repositories and hosted index initialisers on top of
        // IMongoDatabase; a scoped registration would make those captive dependencies.
        var services = BuildServices(("MongoDb:ConnectionString", MongoConnectionString));

        var descriptor = services.Single(d => d.ServiceType == typeof(IMongoDatabase));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void WithTenantScoping_MongoDatabaseIsScoped()
    {
        // The database name varies per request once tenant scoping is on, so it must not be cached.
        var services = BuildServices(
            ("MongoDb:ConnectionString", MongoConnectionString),
            ("MongoDb:UseTenantScoping", "true"));

        var descriptor = services.Single(d => d.ServiceType == typeof(IMongoDatabase));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void WithoutTenantScoping_SingletonConsumerOfMongoDatabaseResolves()
    {
        var services = BuildServices(("MongoDb:ConnectionString", MongoConnectionString));
        services.AddSingleton<SingletonMongoConsumer>();

        // Mirrors the host's Development-mode scope validation.
        var provider = services.BuildServiceProvider(validateScopes: true);

        var act = () => provider.GetRequiredService<SingletonMongoConsumer>();
        act.Should().NotThrow();
    }

    private sealed class SingletonMongoConsumer
    {
        public SingletonMongoConsumer(IMongoDatabase database) => Database = database;

        public IMongoDatabase Database { get; }
    }

    [Fact]
    public void Default_WithMongoConnectionString_DoesNotRegisterCosmosClient()
    {
        var services = BuildServices(("MongoDb:ConnectionString", MongoConnectionString));

        services.Should().NotContain(d => d.ServiceType == typeof(CosmosClient));
    }

    [Fact]
    public void Default_WithCosmosCredentialsButNoMongo_DoesNotSilentlyFallBackToCosmos()
    {
        // Regression guard: the previous behaviour started a service against Cosmos DB whenever
        // MongoDb:ConnectionString happened to be missing, with no signal that it had done so.
        var act = () => BuildServices(
            ("CosmosDb:Endpoint", CosmosEndpoint),
            ("CosmosDb:Key", CosmosKey));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MongoDb:ConnectionString*");
    }

    [Fact]
    public void Default_WithNoDatabaseConfiguration_Throws()
    {
        var act = () => BuildServices();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MongoDb:ConnectionString*");
    }

    [Fact]
    public void ExplicitMongoProvider_RegistersMongoClient()
    {
        var services = BuildServices(
            ("Database:Provider", "MongoDb"),
            ("MongoDb:ConnectionString", MongoConnectionString));

        services.Should().Contain(d => d.ServiceType == typeof(IMongoClient));
    }

    [Fact]
    public void ProviderName_IsCaseInsensitive()
    {
        var services = BuildServices(
            ("Database:Provider", "cosmosdb"),
            ("CosmosDb:Endpoint", CosmosEndpoint),
            ("CosmosDb:Key", CosmosKey));

        services.Should().Contain(d => d.ServiceType == typeof(CosmosClient));
    }

    [Fact]
    public void CosmosProvider_WithEndpointAndKey_RegistersCosmosClient()
    {
        var services = BuildServices(
            ("Database:Provider", "CosmosDb"),
            ("CosmosDb:Endpoint", CosmosEndpoint),
            ("CosmosDb:Key", CosmosKey));

        services.Should().Contain(d => d.ServiceType == typeof(CosmosClient));
        services.Should().NotContain(d => d.ServiceType == typeof(IMongoClient));
    }

    [Fact]
    public void CosmosProvider_WithoutCredentials_Throws()
    {
        var act = () => BuildServices(("Database:Provider", "CosmosDb"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CosmosDb:ConnectionString*");
    }

    [Fact]
    public void UnknownProvider_Throws()
    {
        var act = () => BuildServices(
            ("Database:Provider", "PostgreSql"),
            ("MongoDb:ConnectionString", MongoConnectionString));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PostgreSql*");
    }
}
