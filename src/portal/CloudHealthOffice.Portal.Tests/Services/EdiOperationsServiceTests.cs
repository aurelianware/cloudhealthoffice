using System.Net;
using System.Text;
using System.Text.Json;
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

    // ── Download277CaAsync ──

    [Fact]
    public async Task Download277CaAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.Download277CaAsync("CLM-2026-00001"));
    }

    // ── DownloadEraAsync ──

    [Fact]
    public async Task DownloadEraAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.DownloadEraAsync("PAY-001"));
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Download277CaAsync_WhenApiReturns200_ReturnsStream()
    {
        var content = "ST*277*0001~";
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent(content, Encoding.UTF8, "application/octet-stream");
            return response;
        });
        var sut = CreateService(new HttpClient(handler));

        var stream = await sut.Download277CaAsync("CLM-1");

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Contain("277");
        handler.CapturedUrls[0].Should().Contain("/claims/CLM-1/277ca");
    }

    [Fact]
    public async Task DownloadEraAsync_WhenApiReturns200_UrlContainsPaymentId()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent("ISA*00", Encoding.UTF8, "application/octet-stream");
            return response;
        });
        var sut = CreateService(new HttpClient(handler));

        await sut.DownloadEraAsync("PAY-5");

        handler.CapturedUrls[0].Should().Contain("/payments/PAY-5/835");
    }

}
