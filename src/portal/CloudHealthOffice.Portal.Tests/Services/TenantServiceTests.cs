using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class TenantServiceTests
{
    private readonly Mock<IMongoClient> _mongoClient = new();
    private readonly Mock<IMongoDatabase> _database = new();
    private readonly Mock<IMongoCollection<TenantSubscription>> _tenantsCol = new();
    private readonly Mock<IMongoCollection<BsonDocument>> _tenantUsersCol = new();
    private readonly Mock<IMongoCollection<BsonDocument>> _membersCol = new();
    private readonly Mock<ILogger<TenantService>> _logger = new();
    private readonly IConfiguration _configuration;

    public TenantServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDB:DatabaseName"] = "TestDB",
                ["MongoDB:TenantsCollection"] = "Tenants",
                ["MongoDB:MembersCollection"] = "Members"
            })
            .Build();

        _mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(_database.Object);
        _database.Setup(d => d.GetCollection<TenantSubscription>("Tenants", null)).Returns(_tenantsCol.Object);
        _database.Setup(d => d.GetCollection<BsonDocument>("TenantUsers", null)).Returns(_tenantUsersCol.Object);
        _database.Setup(d => d.GetCollection<BsonDocument>("Members", null)).Returns(_membersCol.Object);
    }

    private TenantService CreateService()
        => new(_mongoClient.Object, _configuration, _logger.Object);

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

    // ── GetSubscriptionByAzureTenantIdAsync ──

    [Fact]
    public async Task GetSubscriptionByAzureTenantIdAsync_WhenTenantExists_ReturnsTenant()
    {
        var tenant = new TenantSubscription
        {
            TenantId = "tenant-1", AzureTenantId = "azure-123",
            OrganizationName = "Acme Health", SubscriptionStatus = "Active",
            Tier = "enterprise"
        };

        var cursor = CreateCursor(new List<TenantSubscription> { tenant });
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        var result = await sut.GetSubscriptionByAzureTenantIdAsync("azure-123");

        result.Should().NotBeNull();
        result!.OrganizationName.Should().Be("Acme Health");
        result.Tier.Should().Be("enterprise");
    }

    [Fact]
    public async Task GetSubscriptionByAzureTenantIdAsync_WhenEmpty_ReturnsNull()
    {
        var cursor = CreateCursor(new List<TenantSubscription>());
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        var result = await sut.GetSubscriptionByAzureTenantIdAsync("azure-nonexistent");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("common")]
    public async Task GetSubscriptionByAzureTenantIdAsync_WithInvalidId_ReturnsNull(string? tenantId)
    {
        var sut = CreateService();

        var result = await sut.GetSubscriptionByAzureTenantIdAsync(tenantId!);

        result.Should().BeNull();
    }

    // ── GetDemoTenantAsync ──

    [Fact]
    public async Task GetDemoTenantAsync_WhenDemoTenantExists_ReturnsDemoTenant()
    {
        var demoTenant = new TenantSubscription
        {
            TenantId = "demo-tenant", IsDemo = true, OrganizationName = "Demo Plan"
        };

        var cursor = CreateCursor(new List<TenantSubscription> { demoTenant });
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        var result = await sut.GetDemoTenantAsync();

        result.Should().NotBeNull();
        result!.OrganizationName.Should().Be("Demo Plan");
        result.IsDemo.Should().BeTrue();
    }

    [Fact]
    public async Task GetDemoTenantAsync_WhenNoDemoTenant_ReturnsDefault()
    {
        var cursor = CreateCursor(new List<TenantSubscription>());
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        var result = await sut.GetDemoTenantAsync();

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("demo-tenant");
        result.OrganizationName.Should().Be("Demo Health Plan");
        result.IsDemo.Should().BeTrue();
    }

    // ── CreateTenantAsync ──

    [Fact]
    public async Task CreateTenantAsync_InsertsAndReturnsTenantId()
    {
        _tenantsCol.Setup(c => c.InsertOneAsync(
            It.IsAny<TenantSubscription>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateService();

        var result = await sut.CreateTenantAsync(new CreateTenantRequest
        {
            OrganizationName = "New Corp",
            AzureTenantId = "azure-new",
            Tier = "professional",
            AdminEmail = "admin@new.com"
        });

        result.Should().StartWith("tenant-");
        _tenantsCol.Verify(c => c.InsertOneAsync(
            It.Is<TenantSubscription>(t => t.OrganizationName == "New Corp"),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── DeleteTenantAsync ──

    [Fact]
    public async Task DeleteTenantAsync_WhenTenantExists_DeletesSuccessfully()
    {
        var deleteResult = new Mock<DeleteResult>();
        deleteResult.Setup(r => r.DeletedCount).Returns(1);
        _tenantsCol.Setup(c => c.DeleteOneAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResult.Object);

        var sut = CreateService();

        await sut.DeleteTenantAsync("azure-123");

        _tenantsCol.Verify(c => c.DeleteOneAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTenantAsync_WhenTenantNotFound_ThrowsKeyNotFoundException()
    {
        var deleteResult = new Mock<DeleteResult>();
        deleteResult.Setup(r => r.DeletedCount).Returns(0);
        _tenantsCol.Setup(c => c.DeleteOneAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResult.Object);

        var sut = CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.DeleteTenantAsync("azure-nope"));
    }

    // ── GetAllSubscriptionsAsync ──
    // Skipped: GetAllSubscriptionsAsync uses .Find().SortByDescending().ToListAsync()
    // which relies on extension methods that cannot be mocked with Moq.
    // This is better suited for integration testing.

    // ── IsMemberOfTenantAsync ──

    [Theory]
    [InlineData(null, "test@test.com")]
    [InlineData("azure-1", null)]
    [InlineData("", "test@test.com")]
    [InlineData("azure-1", "")]
    public async Task IsMemberOfTenantAsync_WithNullOrEmptyParams_ReturnsFalse(string? tenantId, string? email)
    {
        var sut = CreateService();

        var result = await sut.IsMemberOfTenantAsync(tenantId!, email!);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsMemberOfTenantAsync_WhenUserIsAdmin_ReturnsTrue()
    {
        var tenant = new TenantSubscription
        {
            TenantId = "tenant-1", AzureTenantId = "azure-123",
            AdminEmails = new List<string> { "admin@acme.com" }
        };

        var cursor = CreateCursor(new List<TenantSubscription> { tenant });
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        var result = await sut.IsMemberOfTenantAsync("azure-123", "admin@acme.com");

        result.Should().BeTrue();
    }

    // ── GetTenantsForUserAsync ──

    [Fact]
    public async Task GetTenantsForUserAsync_WhenEmailEmpty_ReturnsEmptyList()
    {
        var sut = CreateService();

        var result = await sut.GetTenantsForUserAsync("");

        result.Should().BeEmpty();
    }

    // ── UpdateSubscriptionStatusAsync ──

    [Fact]
    public async Task UpdateSubscriptionStatusAsync_CallsUpdateOne()
    {
        var updateResult = new Mock<UpdateResult>();
        updateResult.Setup(r => r.MatchedCount).Returns(1);
        _tenantsCol.Setup(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<UpdateDefinition<TenantSubscription>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult.Object);

        var sut = CreateService();

        await sut.UpdateSubscriptionStatusAsync("azure-123", "Active");

        _tenantsCol.Verify(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<UpdateDefinition<TenantSubscription>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── UpdateTenantAsync ──

    [Fact]
    public async Task UpdateTenantAsync_WhenTenantNotFound_ThrowsKeyNotFoundException()
    {
        var updateResult = new Mock<UpdateResult>();
        updateResult.Setup(r => r.MatchedCount).Returns(0);
        _tenantsCol.Setup(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<UpdateDefinition<TenantSubscription>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult.Object);

        var sut = CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.UpdateTenantAsync("azure-nope", new UpdateTenantRequest { OrganizationName = "X" }));
    }

    // ── GetTenantsForUserAsync – admin-email path ─────────────────────────────

    [Fact]
    public async Task GetTenantsForUserAsync_WhenUserIsAdminOnTenants_ReturnsTenantList()
    {
        var tenant = new TenantSubscription
        {
            TenantId = "tenant-1", AzureTenantId = "azure-123",
            OrganizationName = "Acme Health",
            AdminEmails = new List<string> { "admin@acme.com" }
        };

        // tenantsCol.Find(adminFilter).ToListAsync()
        var tenantCursor = CreateCursor(new List<TenantSubscription> { tenant });
        _tenantsCol
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TenantSubscription>>(),
                It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantCursor.Object);

        // tenantUsersCol.Find(tenantUserFilter).ToListAsync() → empty
        var tenantUsersCursor = new Mock<IAsyncCursor<BsonDocument>>();
        var firstCallDone = false;
        tenantUsersCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (firstCallDone) return false;
                firstCallDone = true;
                return true;
            });
        tenantUsersCursor.Setup(c => c.Current).Returns(new List<BsonDocument>());
        tenantUsersCursor.Setup(c => c.Dispose());
        _tenantUsersCol
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantUsersCursor.Object);

        var sut = CreateService();

        var result = await sut.GetTenantsForUserAsync("admin@acme.com");

        result.Should().ContainSingle()
            .Which.OrganizationName.Should().Be("Acme Health");
    }

    [Fact]
    public async Task GetTenantsForUserAsync_WhenExceptionThrown_ReturnsEmptyList()
    {
        _tenantsCol
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TenantSubscription>>(),
                It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("MongoDB connection lost"));

        var sut = CreateService();

        var result = await sut.GetTenantsForUserAsync("user@example.com");

        result.Should().BeEmpty();
    }

    // ── GetDemoTenantAsync – exception path ───────────────────────────────────

    [Fact]
    public async Task GetDemoTenantAsync_WhenMongoThrows_ReturnsDefaultDemoTenant()
    {
        _tenantsCol
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TenantSubscription>>(),
                It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("MongoDB connection error"));

        var sut = CreateService();

        var result = await sut.GetDemoTenantAsync();

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("demo-tenant");
        result.IsDemo.Should().BeTrue();
    }

    // ── IsMemberOfTenantAsync – TenantUsers collection path ──────────────────

    [Fact]
    public async Task IsMemberOfTenantAsync_WhenUserFoundInTenantUsers_ReturnsTrue()
    {
        var tenant = new TenantSubscription
        {
            TenantId = "tenant-1",
            AzureTenantId = "azure-123",
            AdminEmails = new List<string>()
        };

        var tenantCursor = CreateCursor(new List<TenantSubscription> { tenant });
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantCursor.Object);

        _tenantUsersCol.Setup(c => c.CountDocumentsAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<CountOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var sut = CreateService();

        var result = await sut.IsMemberOfTenantAsync("azure-123", "member@acme.com");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsMemberOfTenantAsync_WhenUserNotInAdminOrTenantUsers_ChecksMembersCollection()
    {
        var tenant = new TenantSubscription
        {
            TenantId = "tenant-1",
            AzureTenantId = "azure-123",
            AdminEmails = new List<string>()
        };

        var tenantCursor = CreateCursor(new List<TenantSubscription> { tenant });
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantCursor.Object);

        _tenantUsersCol.Setup(c => c.CountDocumentsAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<CountOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);

        _membersCol.Setup(c => c.CountDocumentsAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<CountOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);

        var sut = CreateService();

        var result = await sut.IsMemberOfTenantAsync("azure-123", "stranger@other.com");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsMemberOfTenantAsync_WhenUserFoundInMembersCollection_ReturnsTrue()
    {
        var tenant = new TenantSubscription
        {
            TenantId = "tenant-1",
            AzureTenantId = "azure-123",
            AdminEmails = new List<string>()
        };

        var tenantCursor = CreateCursor(new List<TenantSubscription> { tenant });
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantCursor.Object);

        _tenantUsersCol.Setup(c => c.CountDocumentsAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<CountOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);

        _membersCol.Setup(c => c.CountDocumentsAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<CountOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var sut = CreateService();

        var result = await sut.IsMemberOfTenantAsync("azure-123", "member@acme.com");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsMemberOfTenantAsync_WhenExceptionThrown_ReturnsFalse()
    {
        _tenantsCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<TenantSubscription>>(),
            It.IsAny<FindOptions<TenantSubscription, TenantSubscription>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection timeout"));

        var sut = CreateService();

        var result = await sut.IsMemberOfTenantAsync("azure-123", "user@example.com");

        result.Should().BeFalse();
    }
}
