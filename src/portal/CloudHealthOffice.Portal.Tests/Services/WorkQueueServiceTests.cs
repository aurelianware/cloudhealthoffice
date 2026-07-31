using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class WorkQueueServiceTests
{
    private readonly Mock<ILogger<WorkQueueService>> _logger = new();
    private readonly IConfiguration _configuration;

    public WorkQueueServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private WorkQueueService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new WorkQueueService(httpClient, _configuration, _logger.Object);
    }

    // ── GetQueueSummaryAsync ──

    [Fact]
    public async Task GetQueueSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetQueueSummaryAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── GetQueueItemsAsync ──

    [Fact]
    public async Task GetQueueItemsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetQueueItemsAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueItemsAsync_WithQueueTypeFilter_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetQueueItemsAsync(queueType: "NCCI"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueItemsAsync_WithAssigneeFilter_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetQueueItemsAsync(assignedTo: "Sarah Williams"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── AssignClaimAsync / OverrideAsync ──

    [Fact]
    public async Task AssignClaimAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.AssignClaimAsync("CLM-2026-04201", "David Chen"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task OverrideAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.OverrideAsync("CLM-2026-04201", "Examiner override"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetQueueSummaryAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetQueueSummaryAsync());
        ex.Message.Should().Contain("Claims Service");
    }

    [Fact]
    public async Task AssignClaimAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.AssignClaimAsync("CLM-2026-04201", "David Chen"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetQueueSummaryAsync_WhenApiReturns200_DeserializesSummary()
    {
        var json = JsonSerializer.Serialize(new
        {
            ncciEditFailures = 12, missingAuth = 8, providerNotContracted = 5,
            cobRequired = 3, medicalReview = 7
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetQueueSummaryAsync();

        result.NcciEditFailures.Should().Be(12);
        result.MissingAuth.Should().Be(8);
        result.MedicalReview.Should().Be(7);
        handler.CapturedUrls[0].Should().Contain("/work-queue/summary");
    }

    [Fact]
    public async Task GetQueueItemsAsync_WhenApiReturns200_DeserializesItemList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { claimId = "CLM-1", memberName = "John Doe", memberId = "MBR-1",
                  providerName = "Dr. Smith", serviceDate = "2025-03-01",
                  queueReason = "NCCI Edit Failure", queueReasonCode = "NCCI",
                  daysInQueue = 3, priority = "High", assignedTo = "Sarah",
                  totalCharged = 2500m, procedureCodes = new[] { "99213", "99214" },
                  aiRecommendedDisposition = "RequestInfo", aiConfidenceScore = 0.87,
                  aiRationale = "Confirm distinct procedural services.",
                  aiPolicyCitations = new[] { "CMS NCCI Policy Manual Ch. 1" } }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetQueueItemsAsync();

        result.Should().HaveCount(1);
        result[0].QueueReason.Should().Be("NCCI Edit Failure");
        result[0].Priority.Should().Be("High");
        result[0].ProcedureCodes.Should().Contain("99213");
        result[0].AiRecommendedDisposition.Should().Be("RequestInfo");
        result[0].AiConfidenceScore.Should().Be(0.87);
        result[0].AiPolicyCitations.Should().ContainSingle();
    }

    [Fact]
    public async Task GetQueueItemsAsync_WithFilters_BuildsQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetQueueItemsAsync(queueType: "NCCI", assignedTo: "Sarah Williams", limit: 50);

        var url = handler.CapturedUrls[0];
        url.Should().Contain("limit=50");
        url.Should().Contain("queueType=NCCI");
        url.Should().Contain("assignedTo=Sarah");
    }

    [Fact]
    public async Task AssignClaimAsync_WhenApiReturns200_PostsToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.AssignClaimAsync("CLM-1", "David Chen");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/work-queue/CLM-1/assign");
    }

    [Fact]
    public async Task OverrideAsync_WhenApiReturns200_PostsToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.OverrideAsync("CLM-1", "Medical director override");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/work-queue/CLM-1/override");
    }

    [Fact]
    public async Task ResolvePendedClaimAsync_PostsDispositionAndAiFeedbackToDedicatedEndpoint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ResolvePendedClaimAsync(
            "CLM-1",
            "Approved",
            "Documentation supports modifier 59",
            "Overridden",
            "examiner-1");

        var request = handler.CapturedRequests[0];
        request.Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/work-queue/CLM-1/resolve");

        var body = await request.Content!.ReadAsStringAsync();
        body.Should().Contain("\"disposition\":\"Approved\"");
        body.Should().Contain("\"aiExaminerAgreement\":\"Overridden\"");
        body.Should().Contain("\"examinerUserId\":\"examiner-1\"");
    }

    [Fact]
    public async Task GetQueueSummaryAsync_WhenApiReturnsNull_ReturnsEmptySummary()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetQueueSummaryAsync();
        result.NcciEditFailures.Should().Be(0);
    }
}
