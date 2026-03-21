using System.Net;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class TenantHttpMessageHandlerTests
{
    private readonly Mock<ITenantContextService> _tenantContextService;
    private readonly Mock<ILogger<TenantHttpMessageHandler>> _logger;
    private readonly TenantHttpMessageHandler _sut;
    private readonly HttpMessageInvoker _invoker;

    public TenantHttpMessageHandlerTests()
    {
        _tenantContextService = new Mock<ITenantContextService>();
        _logger = new Mock<ILogger<TenantHttpMessageHandler>>();
        _sut = new TenantHttpMessageHandler(_tenantContextService.Object, _logger.Object)
        {
            InnerHandler = new TestHttpMessageHandler()
        };
        _invoker = new HttpMessageInvoker(_sut);
    }

    [Fact]
    public async Task SendAsync_WhenTenantIdExists_AddsXTenantIdHeader()
    {
        // Arrange
        var tenantId = "tenant-123";
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ReturnsAsync(tenantId);

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.Should().Contain(h => h.Key == "X-Tenant-ID");
        request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(tenantId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WhenTenantIdIsNull_DoesNotAddHeader()
    {
        // Arrange
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ReturnsAsync((string?)null);

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.Should().NotContain(h => h.Key == "X-Tenant-ID");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WhenTenantIdIsEmpty_DoesNotAddHeader()
    {
        // Arrange
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ReturnsAsync(string.Empty);

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.Should().NotContain(h => h.Key == "X-Tenant-ID");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_PreservesExistingHeaders()
    {
        // Arrange
        var tenantId = "tenant-123";
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("Authorization", "Bearer test-token");
        request.Headers.Add("X-Custom-Header", "custom-value");

        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ReturnsAsync(tenantId);

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        request.Headers.Should().Contain(h => h.Key == "Authorization");
        request.Headers.Should().Contain(h => h.Key == "X-Custom-Header");
        request.Headers.Should().Contain(h => h.Key == "X-Tenant-ID");
        request.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer test-token");
        request.Headers.GetValues("X-Custom-Header").Should().ContainSingle().Which.Should().Be("custom-value");
        request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(tenantId);
    }

    [Fact]
    public async Task SendAsync_WorksWithDifferentHttpMethods()
    {
        // Arrange
        var tenantId = "tenant-123";
        var requestUri = new Uri("https://api.example.com/claims");
        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ReturnsAsync(tenantId);

        var httpMethods = new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch };

        foreach (var method in httpMethods)
        {
            var request = new HttpRequestMessage(method, requestUri);

            // Act
            var response = await _invoker.SendAsync(request, CancellationToken.None);

            // Assert
            request.Headers.Should().Contain(h => h.Key == "X-Tenant-ID");
            request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(tenantId);
        }
    }

    [Fact]
    public async Task SendAsync_CallsBaseHandlerAfterAddingHeader()
    {
        // Arrange
        var tenantId = "tenant-123";
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ReturnsAsync(tenantId);

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _tenantContextService.Verify(x => x.GetTenantIdAsync(), Times.Once);
    }

    [Fact]
    public async Task SendAsync_HandlesMultipleRequests_WithDifferentTenantIds()
    {
        // Arrange
        var tenantId1 = "tenant-123";
        var tenantId2 = "tenant-456";
        var requestUri = new Uri("https://api.example.com/claims");
        
        var request1 = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var request2 = new HttpRequestMessage(HttpMethod.Get, requestUri);

        _tenantContextService.SetupSequence(x => x.GetTenantIdAsync())
            .ReturnsAsync(tenantId1)
            .ReturnsAsync(tenantId2);

        // Act
        var response1 = await _invoker.SendAsync(request1, CancellationToken.None);
        var response2 = await _invoker.SendAsync(request2, CancellationToken.None);

        // Assert
        request1.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(tenantId1);
        request2.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(tenantId2);
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_PropagatesInnerHandlerExceptions()
    {
        // Arrange
        var tenantId = "tenant-123";
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var failingHandler = new TenantHttpMessageHandler(_tenantContextService.Object, _logger.Object)
        {
            InnerHandler = new FailingHttpMessageHandler()
        };
        var failingInvoker = new HttpMessageInvoker(failingHandler);

        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ReturnsAsync(tenantId);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await failingInvoker.SendAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WhenHeaderAlreadyPresent_SkipsResolution()
    {
        // Arrange — simulates the header set by HttpClient.DefaultRequestHeaders in MainLayout
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("X-Tenant-ID", "pre-set-tenant");

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert — handler must NOT call ITenantContextService at all
        _tenantContextService.Verify(x => x.GetTenantIdAsync(), Times.Never);
        request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("pre-set-tenant");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WhenAuthStateProviderThrows_DoesNotCrash()
    {
        // Arrange — simulates the IHttpClientFactory scope issue in Blazor Server
        var requestUri = new Uri("https://api.example.com/claims");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        _tenantContextService.Setup(x => x.GetTenantIdAsync())
            .ThrowsAsync(new InvalidOperationException(
                "Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor component."));

        // Act
        var response = await _invoker.SendAsync(request, CancellationToken.None);

        // Assert — request should proceed without the header, not throw
        request.Headers.Should().NotContain(h => h.Key == "X-Tenant-ID");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// Test helper: HTTP handler that always returns OK
public class TestHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
            RequestMessage = request
        });
    }
}

// Test helper: HTTP handler that always fails
public class FailingHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        throw new HttpRequestException("Simulated network failure");
    }
}
