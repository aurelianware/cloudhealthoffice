using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EdiOperationsServiceTests
{
    private readonly Mock<ILogger<EdiOperationsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public EdiOperationsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000",
                ["Services:PaymentService"] = "http://localhost:5006"
            })
            .Build();
    }

    private EdiOperationsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new EdiOperationsService(httpClient, _configuration, _logger.Object);
    }

    // ── Get834BatchesAsync ──

    [Fact]
    public async Task Get834BatchesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.Get834BatchesAsync());
    }

    [Fact]
    public async Task Get834BatchesAsync_WithDateRange_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.Get834BatchesAsync(DateTime.Today.AddDays(-7), DateTime.Today));
    }

    // ── Get834BatchRecordsAsync ──

    [Fact]
    public async Task Get834BatchRecordsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.Get834BatchRecordsAsync("BATCH-001"));
    }

    // ── Resolve834RecordAsync ──

    [Fact]
    public async Task Resolve834RecordAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.Resolve834RecordAsync(new Edi834ResolutionRequest()));
    }

    // ── Get277CaAcknowledgmentsAsync ──

    [Fact]
    public async Task Get277CaAcknowledgmentsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.Get277CaAcknowledgmentsAsync());
    }

    // ── Download277CaAsync ──

    [Fact]
    public async Task Download277CaAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.Download277CaAsync("CLM-2026-00001"));
    }

    // ── GetErasAsync ──

    [Fact]
    public async Task GetErasAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetErasAsync());
    }

    // ── DownloadEraAsync ──

    [Fact]
    public async Task DownloadEraAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.DownloadEraAsync("PAY-001"));
    }

    // ── GetTransactionHistoryAsync ──

    [Fact]
    public async Task GetTransactionHistoryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetTransactionHistoryAsync(null, null, null, null, null, 1, 20));
    }

    [Fact]
    public async Task Get834BatchesAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.Get834BatchesAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
