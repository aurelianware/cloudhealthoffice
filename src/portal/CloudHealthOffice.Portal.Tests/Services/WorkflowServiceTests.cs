using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class WorkflowServiceTests
{
    private readonly IConfiguration _configuration;

    public WorkflowServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ArgoWorkflows"] = "http://localhost:2746"
            })
            .Build();
    }

    private WorkflowService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new WorkflowService(httpClient, _configuration);
    }

    // ── GetWorkflowRunsAsync ──

    [Fact]
    public async Task GetWorkflowRunsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetWorkflowRunsAsync());
        ex.ServiceName.Should().Be("Argo Workflows");
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_WithLimit_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetWorkflowRunsAsync(50));
        ex.ServiceName.Should().Be("Argo Workflows");
    }

    // ── GetWorkflowDetailsAsync ──

    [Fact]
    public async Task GetWorkflowDetailsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetWorkflowDetailsAsync("wf-001"));
        ex.ServiceName.Should().Be("Argo Workflows");
    }

    // ── GetActiveWorkflowsAsync ──

    [Fact]
    public async Task GetActiveWorkflowsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetActiveWorkflowsAsync());
        ex.ServiceName.Should().Be("Argo Workflows");
    }

    // ── RetriggerWorkflowAsync ──

    [Fact]
    public async Task RetriggerWorkflowAsync_WhenApiReturnsError_ReturnsFalse()
    {
        var sut = CreateService();
        var result = await sut.RetriggerWorkflowAsync("wf-001");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetWorkflowRunsAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetWorkflowRunsAsync_WhenApiReturns200_DeserializesWorkflowList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { workflowId = "WF-1", name = "daily-834-ingest", status = "Succeeded",
                  startTime = "2025-03-01T08:00:00Z", durationSeconds = 120 },
            new { workflowId = "WF-2", name = "nightly-claims-adjudication", status = "Running",
                  startTime = "2025-03-01T22:00:00Z", durationSeconds = 0 }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetWorkflowRunsAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("daily-834-ingest");
        result[0].Status.Should().Be("Succeeded");
        result[1].Status.Should().Be("Running");
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_PassesLimitAsQueryParam()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetWorkflowRunsAsync(10);

        handler.CapturedUrls[0].Should().Contain("limit=10");
    }

    [Fact]
    public async Task GetWorkflowDetailsAsync_WhenApiReturns200_DeserializesDetailsWithSteps()
    {
        var json = JsonSerializer.Serialize(new
        {
            workflowId = "WF-1", name = "daily-834-ingest", status = "Succeeded",
            startTime = "2025-03-01T08:00:00Z", durationSeconds = 120,
            steps = new[]
            {
                new { name = "download", status = "Succeeded",
                      startTime = "2025-03-01T08:00:00Z", finishTime = "2025-03-01T08:00:30Z" },
                new { name = "parse", status = "Succeeded",
                      startTime = "2025-03-01T08:00:30Z", finishTime = "2025-03-01T08:02:00Z" }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetWorkflowDetailsAsync("WF-1");

        result.Should().NotBeNull();
        result!.Steps.Should().HaveCount(2);
        result.Steps[0].Name.Should().Be("download");
        handler.CapturedUrls[0].Should().Contain("/cho-workflows/WF-1");
    }

    [Fact]
    public async Task GetActiveWorkflowsAsync_WhenApiReturns200_UrlFiltersByPhaseRunning()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetActiveWorkflowsAsync();

        handler.CapturedUrls[0].Should().Contain("phase=Running");
    }

    [Fact]
    public async Task RetriggerWorkflowAsync_WhenApiReturns200_ReturnsTrue()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "")));

        var result = await sut.RetriggerWorkflowAsync("WF-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task RetriggerWorkflowAsync_UrlContainsWorkflowIdAndRetry()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.RetriggerWorkflowAsync("WF-42");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/cho-workflows/WF-42/retry");
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetWorkflowRunsAsync();
        result.Should().BeEmpty();
    }
}
