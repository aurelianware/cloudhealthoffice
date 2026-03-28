using CloudHealthOffice.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests;

public class MongoDbConnectionFactoryTests
{
    private static MongoDbConnectionFactory CreateFactory(
        IMongoClient? client = null,
        IHttpContextAccessor? httpContextAccessor = null,
        Dictionary<string, string?>? configValues = null)
    {
        client ??= new Mock<IMongoClient>().Object;
        httpContextAccessor ??= new Mock<IHttpContextAccessor>().Object;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        return new MongoDbConnectionFactory(client, httpContextAccessor, config);
    }

    [Fact]
    public void GetDatabase_WithoutTenantScoping_ReturnsBaseDatabase()
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        mockClient.Setup(c => c.GetDatabase("TestDb", null)).Returns(mockDb.Object);

        var factory = CreateFactory(
            client: mockClient.Object,
            configValues: new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = "TestDb",
                ["MongoDb:UseTenantScoping"] = "false"
            });

        var db = factory.GetDatabase();

        db.Should().BeSameAs(mockDb.Object);
        mockClient.Verify(c => c.GetDatabase("TestDb", null), Times.Once);
    }

    [Fact]
    public void GetDatabase_DefaultDatabaseName_UsesCloudHealthOffice()
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        mockClient.Setup(c => c.GetDatabase("CloudHealthOffice", null)).Returns(mockDb.Object);

        var factory = CreateFactory(client: mockClient.Object);

        var db = factory.GetDatabase();

        db.Should().BeSameAs(mockDb.Object);
        mockClient.Verify(c => c.GetDatabase("CloudHealthOffice", null), Times.Once);
    }

    [Fact]
    public void GetDatabase_WithTenantScoping_AndTenantInContext_ReturnsScopedDatabase()
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        mockClient.Setup(c => c.GetDatabase("MyDb_tenant1", null)).Returns(mockDb.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "tenant1";
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var factory = CreateFactory(
            client: mockClient.Object,
            httpContextAccessor: accessor.Object,
            configValues: new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = "MyDb",
                ["MongoDb:UseTenantScoping"] = "true"
            });

        var db = factory.GetDatabase();

        db.Should().BeSameAs(mockDb.Object);
        mockClient.Verify(c => c.GetDatabase("MyDb_tenant1", null), Times.Once);
    }

    [Fact]
    public void GetDatabase_WithTenantScoping_NoTenantInContext_ReturnsBaseDatabase()
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        mockClient.Setup(c => c.GetDatabase("MyDb", null)).Returns(mockDb.Object);

        var httpContext = new DefaultHttpContext();
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var factory = CreateFactory(
            client: mockClient.Object,
            httpContextAccessor: accessor.Object,
            configValues: new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = "MyDb",
                ["MongoDb:UseTenantScoping"] = "true"
            });

        var db = factory.GetDatabase();

        db.Should().BeSameAs(mockDb.Object);
        mockClient.Verify(c => c.GetDatabase("MyDb", null), Times.Once);
    }

    [Fact]
    public void GetDatabase_WithTenantScoping_NullHttpContext_ReturnsBaseDatabase()
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        mockClient.Setup(c => c.GetDatabase("MyDb", null)).Returns(mockDb.Object);

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        var factory = CreateFactory(
            client: mockClient.Object,
            httpContextAccessor: accessor.Object,
            configValues: new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = "MyDb",
                ["MongoDb:UseTenantScoping"] = "true"
            });

        var db = factory.GetDatabase();

        db.Should().BeSameAs(mockDb.Object);
    }

    [Fact]
    public void GetDatabase_ByTenantId_WithTenantScoping_ReturnsScopedDatabase()
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        mockClient.Setup(c => c.GetDatabase("MyDb_explicit-tenant", null)).Returns(mockDb.Object);

        var factory = CreateFactory(
            client: mockClient.Object,
            configValues: new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = "MyDb",
                ["MongoDb:UseTenantScoping"] = "true"
            });

        var db = factory.GetDatabase("explicit-tenant");

        db.Should().BeSameAs(mockDb.Object);
    }

    [Fact]
    public void GetDatabase_ByTenantId_WithoutTenantScoping_ReturnsBaseDatabase()
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        mockClient.Setup(c => c.GetDatabase("MyDb", null)).Returns(mockDb.Object);

        var factory = CreateFactory(
            client: mockClient.Object,
            configValues: new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = "MyDb",
                ["MongoDb:UseTenantScoping"] = "false"
            });

        var db = factory.GetDatabase("any-tenant");

        db.Should().BeSameAs(mockDb.Object);
        mockClient.Verify(c => c.GetDatabase("MyDb", null), Times.Once);
    }

    [Theory]
    [InlineData("tenant/with/slashes", "tenant_with_slashes")]
    [InlineData("tenant\\backslash", "tenant_backslash")]
    [InlineData("tenant.dot", "tenant_dot")]
    [InlineData("tenant with spaces", "tenant_with_spaces")]
    [InlineData("tenant\"quotes", "tenant_quotes")]
    [InlineData("tenant$dollar", "tenant_dollar")]
    [InlineData("tenant*star", "tenant_star")]
    [InlineData("tenant<>angle", "tenant__angle")]
    [InlineData("tenant:colon", "tenant_colon")]
    [InlineData("tenant|pipe", "tenant_pipe")]
    [InlineData("tenant?question", "tenant_question")]
    [InlineData("clean-tenant-id", "clean-tenant-id")]
    public void GetDatabase_ByTenantId_SanitizesInvalidCharacters(string tenantId, string expectedSanitized)
    {
        var mockClient = new Mock<IMongoClient>();
        var mockDb = new Mock<IMongoDatabase>();
        var expectedDbName = $"Db_{expectedSanitized}";
        mockClient.Setup(c => c.GetDatabase(expectedDbName, null)).Returns(mockDb.Object);

        var factory = CreateFactory(
            client: mockClient.Object,
            configValues: new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = "Db",
                ["MongoDb:UseTenantScoping"] = "true"
            });

        var db = factory.GetDatabase(tenantId);

        db.Should().BeSameAs(mockDb.Object);
        mockClient.Verify(c => c.GetDatabase(expectedDbName, null), Times.Once);
    }
}
