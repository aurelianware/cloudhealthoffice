using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class PaymentRunServiceTests
{
    private readonly Mock<ILogger<PaymentRunService>> _logger = new();
    private readonly IConfiguration _configuration;

    public PaymentRunServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:PaymentService"] = "http://localhost:5006"
            })
            .Build();
    }

    private PaymentRunService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new PaymentRunService(httpClient, _configuration, _logger.Object);
    }

    // ── GetPaymentRunsAsync ──

    [Fact]
    public async Task GetPaymentRunsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetPaymentRunsAsync());
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── GetPaymentRunByIdAsync ──

    [Fact]
    public async Task GetPaymentRunByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetPaymentRunByIdAsync("RUN-001"));
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── CreatePaymentRunAsync ──

    [Fact]
    public async Task CreatePaymentRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreatePaymentRunAsync(new CreatePaymentRunRequest()));
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── CancelPaymentRunAsync ──

    [Fact]
    public async Task CancelPaymentRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CancelPaymentRunAsync("RUN-001"));
        ex.ServiceName.Should().Be("Payment Service");
    }

    // ── DownloadEraForRunAsync ──

    [Fact]
    public async Task DownloadEraForRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.DownloadEraForRunAsync("RUN-001"));
        ex.ServiceName.Should().Be("Payment Service");
    }

    [Fact]
    public async Task GetPaymentRunsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetPaymentRunsAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
