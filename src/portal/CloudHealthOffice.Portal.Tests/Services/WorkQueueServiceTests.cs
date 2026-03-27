using System.Net;
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
}
