using System.Net;
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
}
