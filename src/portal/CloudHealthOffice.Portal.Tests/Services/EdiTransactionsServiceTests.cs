using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EdiTransactionsServiceTests
{
    private readonly Mock<ILogger<EdiTransactionsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public EdiTransactionsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5001",
                ["Services:EnrollmentImportService"] = "http://localhost:5011",
            })
            .Build();
    }

    private EdiTransactionsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new EdiTransactionsService(httpClient, _configuration, _logger.Object);
    }

    // ── GetEnrollment834TransactionsAsync ──

    [Fact]
    public async Task GetEnrollment834TransactionsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetEnrollment834TransactionsAsync());
        ex.ServiceName.Should().Be("Enrollment Import Service");
    }

    [Fact]
    public async Task GetEnrollment834TransactionsAsync_ParsesResponse_AndHitsExpectedUrl()
    {
        var body = """
            [
              { "transactionId": "T1", "batchId": "B1", "memberId": "M-001", "memberName": "John Smith",
                "maintenanceTypeCode": "021", "status": "Accepted", "errors": [] }
            ]
            """;
        var handler = new FakeHandler(HttpStatusCode.OK, body);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetEnrollment834TransactionsAsync(50);

        result.Should().ContainSingle();
        result[0].MemberId.Should().Be("M-001");
        result[0].Status.Should().Be("Accepted");
        handler.CapturedUrls.Should().ContainSingle(u =>
            u.Contains("/v1/enrollment/transactions/recent") && u.Contains("limit=50"));
    }

    [Fact]
    public async Task GetEnrollment834TransactionsAsync_ClampsLimitTo500()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetEnrollment834TransactionsAsync(99999);

        handler.CapturedUrls.Should().ContainSingle(u => u.Contains("limit=500"));
    }

    // ── GetClaimImportTransactionsAsync ──

    [Fact]
    public async Task GetClaimImportTransactionsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetClaimImportTransactionsAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetClaimImportTransactionsAsync_ParsesResponse_AndHitsExpectedUrl()
    {
        var body = """
            [
              { "id": "TX-1", "claimNumber": "CLM-1", "claimId": "claim-1", "memberId": "M-001",
                "fileName": "test.837", "status": "Accepted", "errors": [] }
            ]
            """;
        var handler = new FakeHandler(HttpStatusCode.OK, body);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetClaimImportTransactionsAsync(50);

        result.Should().ContainSingle();
        result[0].ClaimNumber.Should().Be("CLM-1");
        result[0].ClaimId.Should().Be("claim-1");
        handler.CapturedUrls.Should().ContainSingle(u =>
            u.Contains("/v1/claims/import-transactions") && u.Contains("limit=50"));
    }

    // ── GetEnrollmentImportRunsAsync ──

    [Fact]
    public async Task GetEnrollmentImportRunsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetEnrollmentImportRunsAsync());
        ex.ServiceName.Should().Be("Enrollment Import Service");
    }

    [Fact]
    public async Task GetEnrollmentImportRunsAsync_ParsesResponse_AndHitsExpectedUrl()
    {
        var body = """
            [
              { "id": "RUN-1", "batchId": "B1", "fileName": "test.834", "successCount": 3, "failedCount": 1,
                "membersCreated": 2, "membersUpdated": 1, "coverageRecordsCreated": 2,
                "coverageMappingsUnresolved": 0, "errors": [] }
            ]
            """;
        var handler = new FakeHandler(HttpStatusCode.OK, body);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetEnrollmentImportRunsAsync(50);

        result.Should().ContainSingle();
        result[0].BatchId.Should().Be("B1");
        result[0].SuccessCount.Should().Be(3);
        result[0].FailedCount.Should().Be(1);
        handler.CapturedUrls.Should().ContainSingle(u =>
            u.Contains("/v1/enrollment/import-runs") && u.Contains("limit=50"));
    }

    [Fact]
    public async Task GetEnrollmentImportRunsAsync_ClampsLimitTo500()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetEnrollmentImportRunsAsync(99999);

        handler.CapturedUrls.Should().ContainSingle(u => u.Contains("limit=500"));
    }
}
