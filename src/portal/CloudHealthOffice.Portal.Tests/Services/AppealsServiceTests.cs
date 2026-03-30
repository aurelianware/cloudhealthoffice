using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class AppealsServiceTests
{
    private readonly Mock<ILogger<AppealsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public AppealsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:AppealsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private AppealsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new AppealsService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task SearchAppealsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SearchAppealsAsync());
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task SearchAppealsAsync_WithFilters_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchAppealsAsync(appealId: "APL-2026-0001"));
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task SearchAppealsAsync_FilterByMemberId_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchAppealsAsync(memberId: "MBR-8201"));
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task GetAppealByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAppealByIdAsync("APL-2026-0001"));
        ex.ServiceName.Should().Be("Appeals Service");
    }

    [Fact]
    public async Task GetSummaryAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.Message.Should().Contain("Appeals Service");
    }

    [Fact]
    public async Task GetSummaryAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
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
            openAppeals = 25, urgentExpedited = 3, dueThisWeek = 8, overturnedRate = 0.15
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetSummaryAsync();

        result.OpenAppeals.Should().Be(25);
        result.UrgentExpedited.Should().Be(3);
        result.DueThisWeek.Should().Be(8);
        result.OverturnedRate.Should().Be(0.15);
        handler.CapturedUrls[0].Should().Contain("/appeals/summary");
    }

    [Fact]
    public async Task SearchAppealsAsync_WhenApiReturns200_DeserializesAppealList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { appealId = "APL-1", memberName = "John Doe", memberId = "MBR-1",
                  appealType = "Medical", originalDecisionId = "DEC-1",
                  originalDecision = "Denied", originalDenialReason = "Not medically necessary",
                  status = "Open", isExpedited = true, filedDate = "2025-03-01",
                  dueDate = "2025-03-15", daysRemaining = 10,
                  assignedReviewer = "Dr. Smith", complianceStatus = "On Track" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.SearchAppealsAsync();

        result.Should().HaveCount(1);
        result[0].AppealType.Should().Be("Medical");
        result[0].IsExpedited.Should().BeTrue();
        result[0].OriginalDenialReason.Should().Be("Not medically necessary");
    }

    [Fact]
    public async Task SearchAppealsAsync_WithFilters_BuildsQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.SearchAppealsAsync(memberId: "MBR-1", originalClaimId: "CLM-1");

        var url = handler.CapturedUrls[0];
        url.Should().Contain("memberId=MBR-1");
        url.Should().Contain("originalClaimId=CLM-1");
    }

    [Fact]
    public async Task GetAppealByIdAsync_WhenApiReturns200_DeserializesDetailsWithTimeline()
    {
        var json = JsonSerializer.Serialize(new
        {
            appealId = "APL-1", memberName = "John Doe", memberId = "MBR-1",
            appealType = "Medical", originalDecisionId = "DEC-1",
            originalDecision = "Denied", originalDenialReason = "Not medically necessary",
            appealReason = "New clinical evidence", status = "Under Review",
            isExpedited = false, filedDate = "2025-03-01", dueDate = "2025-04-01",
            daysRemaining = 25, assignedReviewer = "Dr. Jones", complianceStatus = "On Track",
            finalDecision = "", finalDecisionNotes = "",
            documents = new[]
            {
                new { documentId = "DOC-1", documentName = "Clinical Notes",
                      documentType = "ClinicalNote", uploadedDate = "2025-03-02", uploadedBy = "admin" }
            },
            timeline = new[]
            {
                new { eventDate = "2025-03-01", eventType = "Filed", description = "Appeal filed", performedBy = "System" }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetAppealByIdAsync("APL-1");

        result.Should().NotBeNull();
        result!.AppealReason.Should().Be("New clinical evidence");
        result.Documents.Should().HaveCount(1);
        result.Timeline.Should().HaveCount(1);
        handler.CapturedUrls[0].Should().Contain("/appeals/APL-1");
    }

    [Fact]
    public async Task GetAppealByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetAppealByIdAsync("APL-NONE");
        result.Should().BeNull();
    }
}
