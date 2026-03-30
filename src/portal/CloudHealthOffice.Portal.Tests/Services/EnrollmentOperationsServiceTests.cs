using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EnrollmentOperationsServiceTests
{
    private readonly Mock<ILogger<EnrollmentOperationsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public EnrollmentOperationsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:MemberService"] = "http://localhost:5001"
            })
            .Build();
    }

    private EnrollmentOperationsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new EnrollmentOperationsService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetTodaySummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetTodaySummaryAsync());
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task GetRecentFilesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentFilesAsync());
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task GetFileDetailAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetFileDetailAsync("ENR-FILE-001"));
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task GetTodaySummaryAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetTodaySummaryAsync());
        ex.Message.Should().Contain("Member Service");
    }

    [Fact]
    public async Task GetRecentFilesAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentFilesAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetTodaySummaryAsync_WhenApiReturns200_DeserializesSummary()
    {
        var json = JsonSerializer.Serialize(new
        {
            filesReceived = 5, totalTransactions = 450,
            membersAdded = 200, membersTermed = 30, membersChanged = 50, errorCount = 12
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetTodaySummaryAsync();

        result.FilesReceived.Should().Be(5);
        result.TotalTransactions.Should().Be(450);
        result.MembersAdded.Should().Be(200);
        result.ErrorCount.Should().Be(12);
        handler.CapturedUrls[0].Should().Contain("/enrollment-ops/summary/today");
    }

    [Fact]
    public async Task GetRecentFilesAsync_WhenApiReturns200_DeserializesFileList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { fileId = "F-1", fileName = "834_BlueCross_20250301.edi",
                  receivedTime = "2025-03-01T08:00:00Z", sponsorName = "Acme Corp",
                  groupNumber = "GRP-100", transactionCount = 100,
                  addedCount = 80, termedCount = 10, changedCount = 8, rejectedCount = 2,
                  status = "Completed" }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetRecentFilesAsync(14);

        result.Should().HaveCount(1);
        result[0].FileName.Should().Contain("834_BlueCross");
        result[0].TransactionCount.Should().Be(100);
        handler.CapturedUrls[0].Should().Contain("days=14");
    }

    [Fact]
    public async Task GetFileDetailAsync_WhenApiReturns200_DeserializesDetailWithRejections()
    {
        var json = JsonSerializer.Serialize(new
        {
            fileId = "F-1", fileName = "834_BlueCross.edi",
            receivedTime = "2025-03-01T08:00:00Z", sponsorName = "Acme",
            groupNumber = "GRP-100", transactionCount = 100,
            addedCount = 98, termedCount = 0, changedCount = 0, rejectedCount = 2,
            status = "Completed",
            rejections = new[]
            {
                new { memberId = "MBR-BAD", memberName = "Invalid Member",
                      errorCode = "E001", errorDescription = "Invalid SSN format",
                      rawSegmentReference = "INS*Y*18*021*AI~" }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetFileDetailAsync("F-1");

        result.Rejections.Should().HaveCount(1);
        result.Rejections[0].ErrorCode.Should().Be("E001");
        handler.CapturedUrls[0].Should().Contain("/enrollment-ops/files/F-1");
    }

    [Fact]
    public async Task GetRecentFilesAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetRecentFilesAsync();
        result.Should().BeEmpty();
    }
}
