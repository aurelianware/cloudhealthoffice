using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class SalesInquiryServiceTests
{
    private readonly Mock<IMongoClient> _mongoClient = new();
    private readonly Mock<IMongoDatabase> _database = new();
    private readonly Mock<IMongoCollection<SalesInquiry>> _inquiriesCol = new();
    private readonly Mock<IEmailNotificationService> _emailService = new();
    private readonly Mock<ILogger<SalesInquiryService>> _logger = new();
    private readonly IConfiguration _configuration;

    public SalesInquiryServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDB:DatabaseName"] = "TestDB",
                ["MongoDB:SalesInquiriesCollection"] = "SalesInquiries"
            })
            .Build();

        _mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(_database.Object);
        _database.Setup(d => d.GetCollection<SalesInquiry>("SalesInquiries", null)).Returns(_inquiriesCol.Object);
    }

    private SalesInquiryService CreateService()
        => new(_mongoClient.Object, _configuration, _emailService.Object, _logger.Object);

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

    // ── CreateInquiryAsync ──

    [Fact]
    public async Task CreateInquiryAsync_InsertsAndReturnsInquiryId()
    {
        _inquiriesCol.Setup(c => c.InsertOneAsync(
            It.IsAny<SalesInquiry>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _emailService.Setup(e => e.SendSalesInquiryNotificationAsync(It.IsAny<SalesInquiry>()))
            .Returns(Task.CompletedTask);

        var sut = CreateService();

        var result = await sut.CreateInquiryAsync(new CreateSalesInquiryRequest
        {
            FirstName = "John", LastName = "Doe", Email = "john@acme.com",
            CompanyName = "Acme Corp", InquiryType = "Demo Request",
            Message = "Want a demo"
        });

        result.Should().StartWith("inquiry-");
        _inquiriesCol.Verify(c => c.InsertOneAsync(
            It.Is<SalesInquiry>(i => i.FirstName == "John" && i.Status == "New"),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateInquiryAsync_SendsEmailNotification()
    {
        _inquiriesCol.Setup(c => c.InsertOneAsync(
            It.IsAny<SalesInquiry>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _emailService.Setup(e => e.SendSalesInquiryNotificationAsync(It.IsAny<SalesInquiry>()))
            .Returns(Task.CompletedTask);

        var sut = CreateService();

        await sut.CreateInquiryAsync(new CreateSalesInquiryRequest
        {
            FirstName = "Jane", LastName = "Smith", Email = "jane@beta.com",
            CompanyName = "Beta Corp", InquiryType = "Pricing",
            Message = "Pricing info"
        });

        _emailService.Verify(e => e.SendSalesInquiryNotificationAsync(
            It.Is<SalesInquiry>(i => i.Email == "jane@beta.com")), Times.Once);
    }

    // ── GetInquiryByIdAsync ──

    [Fact]
    public async Task GetInquiryByIdAsync_WhenExists_ReturnsInquiry()
    {
        var inquiry = new SalesInquiry
        {
            Id = "inquiry-1", FirstName = "John", LastName = "Doe",
            Email = "john@acme.com", CompanyName = "Acme", Status = "New"
        };

        var cursor = CreateCursor(new List<SalesInquiry> { inquiry });
        _inquiriesCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<SalesInquiry>>(),
            It.IsAny<FindOptions<SalesInquiry, SalesInquiry>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        var result = await sut.GetInquiryByIdAsync("inquiry-1");

        result.Should().NotBeNull();
        result!.FirstName.Should().Be("John");
        result.CompanyName.Should().Be("Acme");
    }

    [Fact]
    public async Task GetInquiryByIdAsync_WhenNotExists_ReturnsNull()
    {
        var cursor = CreateCursor(new List<SalesInquiry>());
        _inquiriesCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<SalesInquiry>>(),
            It.IsAny<FindOptions<SalesInquiry, SalesInquiry>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        var result = await sut.GetInquiryByIdAsync("inquiry-nope");

        result.Should().BeNull();
    }

    // ── UpdateInquiryStatusAsync ──

    [Fact]
    public async Task UpdateInquiryStatusAsync_WhenInquiryNotFound_ThrowsInvalidOperationException()
    {
        var cursor = CreateCursor(new List<SalesInquiry>());
        _inquiriesCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<SalesInquiry>>(),
            It.IsAny<FindOptions<SalesInquiry, SalesInquiry>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.UpdateInquiryStatusAsync("inquiry-nope", "Contacted"));
    }

    [Fact]
    public async Task UpdateInquiryStatusAsync_WhenExists_UpdatesStatus()
    {
        var inquiry = new SalesInquiry
        {
            Id = "inquiry-1", FirstName = "John", Status = "New"
        };

        var cursor = CreateCursor(new List<SalesInquiry> { inquiry });
        _inquiriesCol.Setup(c => c.FindAsync(
            It.IsAny<FilterDefinition<SalesInquiry>>(),
            It.IsAny<FindOptions<SalesInquiry, SalesInquiry>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var updateResult = new Mock<UpdateResult>();
        updateResult.Setup(r => r.MatchedCount).Returns(1);
        _inquiriesCol.Setup(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<SalesInquiry>>(),
            It.IsAny<UpdateDefinition<SalesInquiry>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult.Object);

        var sut = CreateService();

        await sut.UpdateInquiryStatusAsync("inquiry-1", "Contacted", "Called");

        _inquiriesCol.Verify(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<SalesInquiry>>(),
            It.IsAny<UpdateDefinition<SalesInquiry>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
