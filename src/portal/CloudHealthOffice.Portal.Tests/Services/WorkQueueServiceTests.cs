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

    // ── PR #1278 round 3 (M5): payer order and the service's refusal ──

    [Fact]
    public async Task ResolvePendedClaimAsync_SendsThePayerSequence()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ResolvePendedClaimAsync("CLM-1", "Approved", "EOBs show two payers", null, "examiner-1", payerSequence: 3);

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"payerSequence\":3");
    }

    /// <summary>
    /// Round-3 verification (M4): the approval carries the fingerprint of
    /// the pends the examiner viewed — from the work-queue item or the
    /// claim's pend details — so the service can refuse it if they changed.
    /// </summary>
    [Fact]
    public async Task ResolvePendedClaimAsync_SendsThePendFingerprint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ResolvePendedClaimAsync("CLM-1", "Approved", "ok", null, "examiner-1",
            pendFingerprint: "20260419T000000000Z-abc123");

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"pendFingerprint\":\"20260419T000000000Z-abc123\"");
    }

    [Fact]
    public void PendDetailsAndWorkQueueItems_ReadTheServicesFingerprint()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var pend = System.Text.Json.JsonSerializer.Deserialize<ClaimPendDetails>(
            "{\"pendCode\":\"DUPLICATE\",\"additionalPendReasons\":[\"MEDREVIEW: x\"],\"fingerprint\":\"fp-1\"}", options)!;
        var item = System.Text.Json.JsonSerializer.Deserialize<WorkQueueItem>(
            "{\"claimId\":\"C\",\"pendFingerprint\":\"fp-2\"}", options)!;

        pend.Fingerprint.Should().Be("fp-1");
        pend.AdditionalPendReasons.Should().Equal("MEDREVIEW: x");
        item.PendFingerprint.Should().Be("fp-2");
    }

    [Fact]
    public async Task OverrideAsync_SendsThePayerSequence()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.OverrideAsync("CLM-1", "confirmed secondary", payerSequence: 2);

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"payerSequence\":2");
    }

    /// <summary>
    /// A 409 is the service refusing the approval (here: a COB pend without
    /// a payer order) — the examiner sees its reason, not "service
    /// unavailable".
    /// </summary>
    [Fact]
    public async Task ResolvePendedClaimAsync_409_ThrowsTheServicesReason()
    {
        var handler = new FakeHandler(HttpStatusCode.Conflict,
            "{\"error\":\"This claim is pended for coordination of benefits: approving it requires the payer order you confirmed.\"," +
            "\"outcome\":\"Pend\",\"reasons\":[\"CoordinationOfBenefits: payer-order mismatch\"]}");
        var sut = CreateService(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClaimResolutionRefusedException>(
            () => sut.ResolvePendedClaimAsync("CLM-1", "Approved", "ok", null, "examiner-1"));

        ex.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ex.Message.Should().Contain("requires the payer order");
        ex.Reasons.Should().Equal("CoordinationOfBenefits: payer-order mismatch");
        ex.ToDisplayText().Should().Contain("payer-order mismatch");
    }

    [Fact]
    public async Task ResolvePendedClaimAsync_403Problem_ThrowsTheDetail()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden,
            "{\"title\":\"Payer-order override not permitted\",\"detail\":\"payerSequence 2 disagrees with the 837 SBR01; that needs claims:override-approve.\"}");
        var sut = CreateService(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClaimResolutionRefusedException>(
            () => sut.ResolvePendedClaimAsync("CLM-1", "Approved", "ok", null, "examiner-1", 2));

        ex.Message.Should().Contain("claims:override-approve");
    }

    [Fact]
    public async Task ResolvePendedClaimAsync_202_IsWaitingForASecondApprover()
    {
        var handler = new FakeHandler(HttpStatusCode.Accepted,
            "{\"status\":\"awaiting-second-approval\",\"message\":\"needs a second, different approver\"}");
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.ResolvePendedClaimAsync("CLM-1", "Approved", "ok", null, "supervisor-1", 1);

        result.AwaitingSecondApproval.Should().BeTrue();
        result.Message.Should().Contain("second, different approver");
    }

    [Fact]
    public async Task GetQueueSummaryAsync_WhenApiReturnsNull_ReturnsEmptySummary()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetQueueSummaryAsync();
        result.NcciEditFailures.Should().Be(0);
    }
}
