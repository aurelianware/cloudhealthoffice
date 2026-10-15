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

    // appeals-service wire shapes: list routes return { "items": [Appeal] },
    // GET /appeals/{id} returns a bare Appeal, and enums may arrive as numbers.

    private static object WireAppeal(object? appealType = null, object? status = null,
        string? targetResponseDate = "2099-01-01T00:00:00Z", bool isOverdue = false) => new
    {
        id = "doc-1", appealNumber = "APL-2026-0001", claimId = "clm-doc-1", claimNumber = "CLM-1",
        memberId = "MBR001", patientName = "Jane Doe",
        denialReasonCode = "CO-50", denialReason = "Not medically necessary",
        appealType = appealType ?? 1, status = status ?? 3,
        appealReason = "New clinical evidence", isUrgent = true,
        submittedDate = "2026-10-01T00:00:00Z", targetResponseDate, isOverdue,
        assignedReviewerId = "reviewer-7",
        notes = new[] { new { createdAt = "2026-10-02T00:00:00Z", createdBy = "examiner", noteText = "Records requested" } },
        attachments = new[] { new { attachmentId = "att-1", fileName = "notes.pdf",
            attachmentTypeDescription = "Clinical notes", attachmentTypeCode = "09", uploadedAt = "2026-10-02T00:00:00Z" } }
    };

    private static string ListJson(params object[] items) => JsonSerializer.Serialize(new { items }, JsonOpts);

    [Fact]
    public async Task SearchAppealsAsync_ParsesItemsEnvelopeAndNumericEnums()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ListJson(WireAppeal()));
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.SearchAppealsAsync();

        result.Should().HaveCount(1);
        var a = result[0];
        a.Id.Should().Be("doc-1");
        a.AppealId.Should().Be("APL-2026-0001");
        a.MemberName.Should().Be("Jane Doe");
        a.AppealType.Should().Be("Reconsideration");
        a.Status.Should().Be("InReview");
        a.IsExpedited.Should().BeTrue();
        a.OriginalDecisionId.Should().Be("CLM-1");
        a.OriginalDenialReason.Should().Be("CO-50 — Not medically necessary");
        a.AssignedReviewer.Should().Be("reviewer-7");
        a.ComplianceStatus.Should().Be("On track");
        handler.CapturedUrls[0].Should().Contain("/appeals/search");
    }

    [Fact]
    public async Task SearchAppealsAsync_AcceptsStringEnums()
    {
        var json = ListJson(WireAppeal(appealType: "ExternalReview", status: "Closed"));
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var a = (await sut.SearchAppealsAsync())[0];

        a.AppealType.Should().Be("ExternalReview");
        a.Status.Should().Be("Closed");
        a.ComplianceStatus.Should().Be("Closed");
    }

    [Fact]
    public async Task SearchAppealsAsync_OverdueAndMissingDeadline_SetCompliance()
    {
        var json = ListJson(WireAppeal(isOverdue: true), WireAppeal(targetResponseDate: null));
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchAppealsAsync();

        result[0].ComplianceStatus.Should().Be("Overdue");
        result[1].ComplianceStatus.Should().Be("No deadline set");
        result[1].DaysRemaining.Should().Be(0);
    }

    [Fact]
    public async Task SearchAppealsAsync_ByMemberId_UsesSearchRoute()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ListJson());
        var sut = CreateService(new HttpClient(handler));

        await sut.SearchAppealsAsync(memberId: "MBR-1");

        handler.CapturedUrls[0].Should().EndWith("/appeals/search?memberId=MBR-1");
    }

    [Fact]
    public async Task SearchAppealsAsync_ByOriginalClaimId_UsesClaimRoute()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ListJson(WireAppeal()));
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.SearchAppealsAsync(originalClaimId: "CLM-1");

        result.Should().HaveCount(1);
        handler.CapturedUrls[0].Should().EndWith("/appeals/claim/CLM-1");
    }

    [Fact]
    public async Task SearchAppealsAsync_ByAppealNumber_UsesNumberRoute()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(WireAppeal(), JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.SearchAppealsAsync(appealId: "APL-2026-0001");

        result.Should().ContainSingle().Which.AppealId.Should().Be("APL-2026-0001");
        handler.CapturedUrls[0].Should().EndWith("/appeals/number/APL-2026-0001");
    }

    [Fact]
    public async Task SearchAppealsAsync_ByUnknownAppealNumber_ReturnsEmpty()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.NotFound)));
        var result = await sut.SearchAppealsAsync(appealId: "APL-NONE");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAppealByIdAsync_MapsAttachmentsNotesAndDecision()
    {
        var json = JsonSerializer.Serialize(WireAppeal(), JsonOpts).TrimEnd('}')
            + ",\"decision\":{\"decisionType\":2,\"decisionReason\":\"Criteria not met\",\"decisionDate\":\"2026-10-05T00:00:00Z\"}}";
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetAppealByIdAsync("doc-1");

        result.Should().NotBeNull();
        result!.AppealReason.Should().Be("New clinical evidence");
        result.FinalDecision.Should().Be("Denied");
        result.FinalDecisionNotes.Should().Be("Criteria not met");
        result.Documents.Should().ContainSingle().Which.DocumentName.Should().Be("notes.pdf");
        result.Timeline.Select(t => t.EventType).Should().Equal("Filed", "Note");
        result.Timeline[1].Description.Should().Be("Records requested");
        handler.CapturedUrls[0].Should().EndWith("/appeals/doc-1");
    }

    [Fact]
    public async Task GetAppealByIdAsync_WhenNotFound_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.NotFound)));
        var result = await sut.GetAppealByIdAsync("APL-NONE");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAppealByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetAppealByIdAsync("APL-NONE");
        result.Should().BeNull();
    }
}
