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

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get834BatchesAsync_WhenApiReturns200_DeserializesBatchList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { batchId = "B-1", tradingPartnerId = "TP-1", tradingPartnerName = "Blue Cross",
                  receivedDate = "2025-03-01", totalRecords = 500, acceptedCount = 490,
                  rejectedCount = 5, pendingCount = 5, status = "Completed" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.Get834BatchesAsync();

        result.Should().HaveCount(1);
        result[0].BatchId.Should().Be("B-1");
        result[0].TotalRecords.Should().Be(500);
        result[0].AcceptedCount.Should().Be(490);
    }

    [Fact]
    public async Task Get834BatchesAsync_WithDateRange_IncludesDatesInUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.Get834BatchesAsync(new DateTime(2025, 1, 1), new DateTime(2025, 1, 31));

        handler.CapturedUrls[0].Should().Contain("from=2025-01-01");
        handler.CapturedUrls[0].Should().Contain("to=2025-01-31");
    }

    [Fact]
    public async Task Get834BatchRecordsAsync_WhenApiReturns200_DeserializesRecords()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { transactionId = "TX-1", batchId = "B-1", memberId = "M-1",
                  memberName = "John Doe", maintenanceTypeCode = "021",
                  maintenanceReasonCode = "AI", transactionSetPurpose = "Original",
                  transactionDate = "2025-03-01", status = "Accepted", errors = Array.Empty<string>() }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.Get834BatchRecordsAsync("B-1");

        result.Should().HaveCount(1);
        result[0].MemberName.Should().Be("John Doe");
        result[0].MaintenanceTypeCode.Should().Be("021");
        handler.CapturedUrls[0].Should().Contain("/edi/834-batches/B-1/records");
    }

    [Fact]
    public async Task Resolve834RecordAsync_WhenApiReturns200_PostsToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.Resolve834RecordAsync(new Edi834ResolutionRequest
        {
            BatchId = "B-1", TransactionId = "TX-1", Action = "Accept", Notes = "Verified"
        });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/edi/834-batches/B-1/resolve");
    }

    [Fact]
    public async Task Get277CaAcknowledgmentsAsync_WhenApiReturns200_DeserializesList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { ackId = "ACK-1", claimId = "CLM-1", claimNumber = "CN-001",
                  memberName = "Jane Doe", providerName = "Dr. Smith",
                  generatedDate = "2025-03-01", ackStatus = "Accepted",
                  statusCategoryCode = "A1", statusCode = "19", statusDescription = "Accepted" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.Get277CaAcknowledgmentsAsync();

        result.Should().HaveCount(1);
        result[0].AckStatus.Should().Be("Accepted");
        result[0].ClaimNumber.Should().Be("CN-001");
    }

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
    public async Task GetErasAsync_WhenApiReturns200_DeserializesEraSummaryList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { eraId = "ERA-1", paymentId = "PAY-1", payerName = "Acme Health",
                  payeeNPI = "1234567890", payeeName = "Dr. Smith",
                  paymentDate = "2025-03-15", paymentMethod = "ACH", checkNumber = "ACH-001",
                  totalPaymentAmount = 25000m, claimCount = 50, status = "Generated" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetErasAsync();

        result.Should().HaveCount(1);
        result[0].TotalPaymentAmount.Should().Be(25000m);
        result[0].PaymentMethod.Should().Be("ACH");
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

    [Fact]
    public async Task GetTransactionHistoryAsync_WhenApiReturns200_DeserializesList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { transactionId = "TH-1", transactionType = "834",
                  transactionDate = "2025-03-01", tradingPartnerId = "TP-1",
                  tradingPartnerName = "Blue Cross", direction = "Inbound",
                  status = "Processed", recordCount = 100 }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetTransactionHistoryAsync(null, null, "834", null, null, 1, 20);

        result.Should().HaveCount(1);
        result[0].TransactionType.Should().Be("834");
        handler.CapturedUrls[0].Should().Contain("page=1");
        handler.CapturedUrls[0].Should().Contain("pageSize=20");
        handler.CapturedUrls[0].Should().Contain("type=834");
    }

    [Fact]
    public async Task Get834BatchesAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.Get834BatchesAsync();
        result.Should().BeEmpty();
    }
}
