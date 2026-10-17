using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests;

/// <summary>
/// Covers database registration in AddChoInfrastructure. MongoDB is the only store (ADR 017);
/// leftover Cosmos DB SDK settings or another provider name are refused at start-up.
/// </summary>
public class RegisterDatabaseTests
{
    private const string MongoConnectionString = "mongodb://localhost:27017";
    private const string CosmosEndpoint = "https://example.documents.azure.com:443/";
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
    public void Default_WithCosmosCredentialsButNoMongo_DoesNotSilentlyFallBackToCosmos()
    {
        // Leftover Cosmos DB SDK settings: the operator believes a database is configured, so the
        // service must not start without one.
        var act = () => BuildServices(
            ("CosmosDb:Endpoint", CosmosEndpoint),
            ("CosmosDb:Key", CosmosKey));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CosmosDb settings are present*");
    }

    [Fact]
    public void Default_WithNoDatabaseConfiguration_RegistersNothing()
    {
        // Nothing is configured, so there is no wrong database to reach. Services that fall back
        // to in-memory storage, and smoke tests that substitute the repositories, rely on the host
        // still booting here.
        var services = BuildServices();

        services.Should().NotContain(d => d.ServiceType == typeof(IMongoClient));
        services.Should().NotContain(d => d.ServiceType == typeof(MongoDbConnectionFactory));
    }

    [Fact]
    public void Default_WithNoDatabaseConfiguration_ReturnsNoProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddChoDatabase(configuration).Should().BeNull();
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
            ("Database:Provider", "mongodb"),
            ("MongoDb:ConnectionString", MongoConnectionString));

        services.Should().Contain(d => d.ServiceType == typeof(IMongoClient));
    }

    [Theory]
    [InlineData("CosmosDb")]
    [InlineData("cosmosdb")]
    public void CosmosProvider_IsRefused_EvenWithCredentials(string provider)
    {
        var act = () => BuildServices(
            ("Database:Provider", provider),
            ("CosmosDb:Endpoint", CosmosEndpoint),
            ("CosmosDb:Key", CosmosKey),
            ("MongoDb:ConnectionString", MongoConnectionString));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not supported*ADR 017*");
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
