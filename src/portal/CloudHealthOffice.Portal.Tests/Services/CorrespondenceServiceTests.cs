using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class CorrespondenceServiceTests
{
    private readonly Mock<ILogger<CorrespondenceService>> _logger = new();
    private readonly IConfiguration _configuration;

    public CorrespondenceServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private CorrespondenceService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new CorrespondenceService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetQueueAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueAsync_WithTypeFilter_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetQueueAsync(type: "EOB"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueAsync_WithStatusFilter_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetQueueAsync(status: "Queued"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetOutstandingRfaisAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetSummaryAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.Message.Should().Contain("Claims Service");
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetOutstandingRfaisAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetSummaryAsync_WhenApiReturns200_DeserializesSummary()
    {
        var json = JsonSerializer.Serialize(new
        {
            pendingGeneration = 15, generatedToday = 42, sentThisWeek = 180, failedReturned = 3
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetSummaryAsync();

        result.PendingGeneration.Should().Be(15);
        result.GeneratedToday.Should().Be(42);
        result.SentThisWeek.Should().Be(180);
        handler.CapturedUrls[0].Should().Contain("/correspondence/summary");
    }

    [Fact]
    public async Task GetQueueAsync_WhenApiReturns200_DeserializesItemList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { letterId = "LTR-1", letterType = "EOB", recipientName = "John Doe",
                  recipientType = "Member", relatedId = "CLM-1",
                  generatedDate = "2025-03-01", status = "Generated",
                  deliveryMethod = "Mail" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetQueueAsync();

        result.Should().HaveCount(1);
        result[0].LetterType.Should().Be("EOB");
        result[0].DeliveryMethod.Should().Be("Mail");
    }

    [Fact]
    public async Task GetQueueAsync_WithFilters_BuildsQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetQueueAsync(type: "DenialLetter", status: "Queued", limit: 25);

        var url = handler.CapturedUrls[0];
        url.Should().Contain("limit=25");
        url.Should().Contain("type=DenialLetter");
        url.Should().Contain("status=Queued");
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_WhenApiReturns200_DeserializesRfaiList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { rfaiId = "RFAI-1", recipientName = "Dr. Smith", recipientType = "Provider",
                  relatedClaimId = "CLM-1", documentsRequested = "Op notes, imaging",
                  sentDate = "2025-02-15", responseDeadline = "2025-03-15",
                  daysSinceSent = 28, daysUntilDeadline = 0, status = "Overdue" }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetOutstandingRfaisAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be("Overdue");
        result[0].DocumentsRequested.Should().Contain("Op notes");
        handler.CapturedUrls[0].Should().Contain("/correspondence/rfais/outstanding");
    }

    [Fact]
    public async Task GetQueueAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetQueueAsync();
        result.Should().BeEmpty();
    }
}
