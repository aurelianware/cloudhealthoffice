using CloudHealthOffice.Portal.Infrastructure;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Tests.Infrastructure;

public class TenantSeedServiceTests
{
    private readonly Mock<IMongoClient> _mongoClient = new();
    private readonly Mock<IMongoDatabase> _database = new();
    private readonly Mock<IMongoCollection<TenantSubscription>> _collection = new();
    private readonly Mock<ILogger<TenantSeedService>> _logger = new();
    private readonly IConfiguration _configuration;

    public TenantSeedServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDB:DatabaseName"] = "TestDb",
                ["MongoDB:TenantsCollection"] = "TestTenants",
                ["SeedTenant:AzureTenantId"] = "test-azure-tenant-id",
                ["SeedTenant:TenantId"] = "test-tenant",
                ["SeedTenant:OrganizationName"] = "Test Org",
                ["SeedTenant:AdminEmail"] = "admin@test.org",
                ["SeedTenant:Tier"] = "enterprise"
            })
            .Build();

        _mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null))
            .Returns(_database.Object);
        _database.Setup(d => d.GetCollection<TenantSubscription>(It.IsAny<string>(), null))
            .Returns(_collection.Object);
    }

    private static Mock<IAsyncCursor<T>> CreateCursor<T>(List<T> items)
    {
        var cursor = new Mock<IAsyncCursor<T>>();
        var first = true;
        cursor.Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(() => { if (first) { first = false; return items.Count > 0; } return false; });
        var asyncFirst = true;
        cursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { if (asyncFirst) { asyncFirst = false; return items.Count > 0; } return false; });
        cursor.Setup(c => c.Current).Returns(items);
        cursor.Setup(c => c.Dispose());
        return cursor;
    }

    private TenantSeedService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mongoClient.Object);
        var sp = services.BuildServiceProvider();

        return new TenantSeedService(sp, _configuration, _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCollectionEmpty_SeedsDemoTenant()
    {
        var cursor = CreateCursor(new List<TenantSubscription>());
        _collection.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        _collection.Setup(c => c.InsertOneAsync(
            It.IsAny<TenantSubscription>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateService();
        using var cts = new CancellationTokenSource();

        // Start the service - it has a 5s delay internally, so we need to wait
        var task = sut.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(7));
        await cts.CancelAsync();

        try { await task; } catch (OperationCanceledException) { }

        _collection.Verify(c => c.InsertOneAsync(
            It.Is<TenantSubscription>(t =>
                t.TenantId == "test-tenant" &&
                t.AzureTenantId == "test-azure-tenant-id" &&
                t.OrganizationName == "Test Org" &&
                t.SubscriptionStatus == "Active" &&
                t.Tier == "enterprise" &&
                t.AdminEmails.Contains("admin@test.org")),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTenantExists_SkipsSeeding()
    {
        var existing = new List<TenantSubscription>
        {
            new() { TenantId = "test-tenant", AzureTenantId = "test-azure-tenant-id" }
        };
        var cursor = CreateCursor(existing);
        _collection.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        using var cts = new CancellationTokenSource();

        var task = sut.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(7));
        await cts.CancelAsync();

        try { await task; } catch (OperationCanceledException) { }

        _collection.Verify(c => c.InsertOneAsync(
            It.IsAny<TenantSubscription>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMongoConnectionFails_LogsWarningAndContinues()
    {
        _collection.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoConnectionException(
                new MongoDB.Driver.Core.Connections.ConnectionId(
                    new MongoDB.Driver.Core.Servers.ServerId(
                        new MongoDB.Driver.Core.Clusters.ClusterId(1),
                        new System.Net.DnsEndPoint("localhost", 27017))),
                "Connection refused"));

        var sut = CreateService();
        using var cts = new CancellationTokenSource();

        var task = sut.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(7));
        await cts.CancelAsync();

        // Should not throw — the service handles exceptions gracefully
        try { await task; } catch (OperationCanceledException) { }

        // Verify InsertOne was never called
        _collection.Verify(c => c.InsertOneAsync(
            It.IsAny<TenantSubscription>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithDefaultConfig_UsesDefaultValues()
    {
        // Use empty config to exercise defaults
        var defaultConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(_mongoClient.Object);
        var sp = services.BuildServiceProvider();

        var cursor = CreateCursor(new List<TenantSubscription>());
        _collection.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        _collection.Setup(c => c.InsertOneAsync(
            It.IsAny<TenantSubscription>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new TenantSeedService(sp, defaultConfig, _logger.Object);
        using var cts = new CancellationTokenSource();

        var task = sut.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(7));
        await cts.CancelAsync();

        try { await task; } catch (OperationCanceledException) { }

        // Verify default values were used
        _mongoClient.Verify(c => c.GetDatabase("CloudHealthOffice", null), Times.Once);
        _database.Verify(d => d.GetCollection<TenantSubscription>("Tenants", null), Times.Once);

        _collection.Verify(c => c.InsertOneAsync(
            It.Is<TenantSubscription>(t =>
                t.TenantId == "aurelianware" &&
                t.OrganizationName == "Cloud Health Office" &&
                t.Tier == "professional" &&
                t.AdminEmails.Count == 0),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
