using System.Net;
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
                ["Services:ClaimsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private AppealsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new AppealsService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenApiFails_ReturnsMockSummary()
    {
        var sut = CreateService();
        var result = await sut.GetSummaryAsync();

        result.Should().NotBeNull();
        result.OpenAppeals.Should().BeGreaterThan(0);
        result.UrgentExpedited.Should().BeGreaterThan(0);
        result.DueThisWeek.Should().BeGreaterThan(0);
        result.OverturnedRate.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task SearchAppealsAsync_NoFilters_ReturnsAllMockAppeals()
    {
        var sut = CreateService();
        var result = await sut.SearchAppealsAsync();

        result.Should().NotBeEmpty();
        result.Count.Should().BeGreaterOrEqualTo(15);
    }

    [Fact]
    public async Task SearchAppealsAsync_MockAppeals_HaveRequiredFields()
    {
        var sut = CreateService();
        var appeals = await sut.SearchAppealsAsync();

        foreach (var a in appeals)
        {
            a.AppealId.Should().StartWith("APL-");
            a.MemberName.Should().NotBeNullOrEmpty();
            a.MemberId.Should().StartWith("MBR-");
            a.AppealType.Should().BeOneOf("Claim", "Authorization", "Coverage");
            a.OriginalDecisionId.Should().NotBeNullOrEmpty();
            a.Status.Should().BeOneOf("Received", "Under Review", "Decision Made", "Escalated", "Withdrawn");
            a.ComplianceStatus.Should().BeOneOf("On Track", "At Risk", "Overdue", "N/A");
            a.OriginalDenialReason.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task SearchAppealsAsync_FilterByAppealId_ReturnsMatching()
    {
        var sut = CreateService();
        var result = await sut.SearchAppealsAsync(appealId: "APL-2026-0001");

        result.Should().ContainSingle();
        result[0].AppealId.Should().Be("APL-2026-0001");
    }

    [Fact]
    public async Task SearchAppealsAsync_FilterByMemberId_ReturnsMatching()
    {
        var sut = CreateService();
        var result = await sut.SearchAppealsAsync(memberId: "MBR-8201");

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(a => a.MemberId == "MBR-8201");
    }

    [Fact]
    public async Task SearchAppealsAsync_IncludesExpeditedAppeals()
    {
        var sut = CreateService();
        var appeals = await sut.SearchAppealsAsync();
        var expedited = appeals.Where(a => a.IsExpedited).ToList();

        expedited.Should().HaveCountGreaterOrEqualTo(2);
        // Expedited appeals should have short deadlines (72 hours = ~3 days max)
        expedited.Should().OnlyContain(a => a.DueDate <= a.FiledDate.AddDays(4));
    }

    [Fact]
    public async Task SearchAppealsAsync_IncludesOverdueAppeals()
    {
        var sut = CreateService();
        var appeals = await sut.SearchAppealsAsync();
        var overdue = appeals.Where(a => a.ComplianceStatus == "Overdue").ToList();

        overdue.Should().NotBeEmpty();
        overdue.Should().OnlyContain(a => a.DaysRemaining < 0);
    }

    [Fact]
    public async Task GetAppealByIdAsync_WhenApiFails_ReturnsMockDetail()
    {
        var sut = CreateService();
        var detail = await sut.GetAppealByIdAsync("APL-2026-0001");

        detail.Should().NotBeNull();
        detail!.AppealId.Should().Be("APL-2026-0001");
        detail.AppealReason.Should().NotBeNullOrEmpty();
        detail.Documents.Should().NotBeEmpty();
        detail.Timeline.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetAppealByIdAsync_DecidedAppeal_HasFinalDecision()
    {
        var sut = CreateService();
        var detail = await sut.GetAppealByIdAsync("APL-2026-0013");

        detail.Should().NotBeNull();
        detail!.Status.Should().Be("Decision Made");
        detail.FinalDecision.Should().Be("Overturned");
        detail.DecisionDate.Should().NotBeNull();
        detail.FinalDecisionNotes.Should().NotBeNullOrEmpty();
        detail.Timeline.Should().Contain(e => e.EventType == "Decision");
        detail.Timeline.Should().Contain(e => e.EventType == "Notified");
    }

    [Fact]
    public async Task GetAppealByIdAsync_WithdrawnAppeal_HasWithdrawnDecision()
    {
        var sut = CreateService();
        var detail = await sut.GetAppealByIdAsync("APL-2026-0015");

        detail.Should().NotBeNull();
        detail!.FinalDecision.Should().Be("Withdrawn");
        detail.Timeline.Should().Contain(e => e.EventType == "Withdrawn");
    }

    [Fact]
    public async Task GetAppealByIdAsync_NonexistentId_ReturnsNull()
    {
        var sut = CreateService();
        var detail = await sut.GetAppealByIdAsync("APL-DOES-NOT-EXIST");

        detail.Should().BeNull();
    }
}
