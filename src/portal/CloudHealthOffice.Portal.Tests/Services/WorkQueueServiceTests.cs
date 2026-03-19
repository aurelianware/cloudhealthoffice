using System.Net;
using System.Net.Http.Json;
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
    public async Task GetQueueSummaryAsync_WhenApiFails_ReturnsMockSummary()
    {
        var sut = CreateService();
        var result = await sut.GetQueueSummaryAsync();

        result.Should().NotBeNull();
        result.NcciEditFailures.Should().BeGreaterThan(0);
        result.MissingAuth.Should().BeGreaterThan(0);
        result.ProviderNotContracted.Should().BeGreaterThan(0);
        result.CobRequired.Should().BeGreaterThan(0);
        result.MedicalReview.Should().BeGreaterThan(0);
    }

    // ── GetQueueItemsAsync ──

    [Fact]
    public async Task GetQueueItemsAsync_WhenApiFails_ReturnsMockItems()
    {
        var sut = CreateService();
        var result = await sut.GetQueueItemsAsync();

        result.Should().NotBeEmpty();
        result.Count.Should().BeGreaterOrEqualTo(35);
    }

    [Fact]
    public async Task GetQueueItemsAsync_MockItems_HaveRequiredFields()
    {
        var sut = CreateService();
        var items = await sut.GetQueueItemsAsync();

        foreach (var item in items)
        {
            item.ClaimId.Should().NotBeNullOrEmpty();
            item.MemberName.Should().NotBeNullOrEmpty();
            item.MemberId.Should().NotBeNullOrEmpty();
            item.ProviderName.Should().NotBeNullOrEmpty();
            item.QueueReason.Should().NotBeNullOrEmpty();
            item.QueueReasonCode.Should().NotBeNullOrEmpty();
            item.Priority.Should().BeOneOf("High", "Medium", "Low");
            item.AssignedTo.Should().NotBeNullOrEmpty();
            item.TotalCharged.Should().BeGreaterThan(0);
            item.ProcedureCodes.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task GetQueueItemsAsync_MockItems_SortedByDaysInQueueDescending()
    {
        var sut = CreateService();
        var items = await sut.GetQueueItemsAsync();

        for (int i = 1; i < items.Count; i++)
        {
            items[i - 1].DaysInQueue.Should().BeGreaterOrEqualTo(items[i].DaysInQueue);
        }
    }

    [Theory]
    [InlineData("NCCI")]
    [InlineData("AUTH")]
    [InlineData("OON")]
    [InlineData("COB")]
    [InlineData("MED")]
    public async Task GetQueueItemsAsync_FilterByQueueType_ReturnsOnlyMatchingItems(string queueType)
    {
        var sut = CreateService();
        var items = await sut.GetQueueItemsAsync(queueType: queueType);

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.QueueReasonCode == queueType);
    }

    [Fact]
    public async Task GetQueueItemsAsync_FilterByAssignee_ReturnsOnlyMatchingItems()
    {
        var sut = CreateService();
        var items = await sut.GetQueueItemsAsync(assignedTo: "Sarah Williams");

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.AssignedTo == "Sarah Williams");
    }

    [Fact]
    public async Task GetQueueItemsAsync_MockSummaryCountsMatchItemCounts()
    {
        var sut = CreateService();
        var summary = await sut.GetQueueSummaryAsync();
        var allItems = await sut.GetQueueItemsAsync();

        allItems.Count(i => i.QueueReasonCode == "NCCI").Should().Be(summary.NcciEditFailures);
        allItems.Count(i => i.QueueReasonCode == "AUTH").Should().Be(summary.MissingAuth);
        allItems.Count(i => i.QueueReasonCode == "OON").Should().Be(summary.ProviderNotContracted);
        allItems.Count(i => i.QueueReasonCode == "COB").Should().Be(summary.CobRequired);
        allItems.Count(i => i.QueueReasonCode == "MED").Should().Be(summary.MedicalReview);
    }

    [Fact]
    public async Task GetQueueItemsAsync_HighPriorityItems_HaveHighDollarOrOldAge()
    {
        var sut = CreateService();
        var items = await sut.GetQueueItemsAsync();
        var highPriority = items.Where(i => i.Priority == "High").ToList();

        highPriority.Should().NotBeEmpty();
        foreach (var item in highPriority)
        {
            // High priority = >7 days OR >$10,000
            (item.DaysInQueue > 7 || item.TotalCharged > 10000m).Should().BeTrue(
                $"High-priority item {item.ClaimId} has {item.DaysInQueue} days and ${item.TotalCharged}");
        }
    }

    // ── AssignClaimAsync / OverrideAsync — non-throwing on failure ──

    [Fact]
    public async Task AssignClaimAsync_WhenApiFails_DoesNotThrow()
    {
        var sut = CreateService();
        var act = () => sut.AssignClaimAsync("CLM-2026-04201", "David Chen");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OverrideAsync_WhenApiFails_DoesNotThrow()
    {
        var sut = CreateService();
        var act = () => sut.OverrideAsync("CLM-2026-04201", "Examiner override");
        await act.Should().NotThrowAsync();
    }
}
