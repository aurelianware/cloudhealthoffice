using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ProviderContractsServiceTests
{
    private readonly Mock<ILogger<ProviderContractsService>> _logger = new();
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ProviderContractsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ProviderContractsService"] = "http://localhost:5050/api"
            })
            .Build();
    }

    private ProviderContractsService CreateService(HttpClient httpClient)
        => new(httpClient, _configuration, _logger.Object);

    // ── GetContractsAsync ──

    [Fact]
    public async Task GetContractsAsync_WhenApiReturns200_DeserializesContractList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "CTR-1", contractNumber = "CN-001", providerNPI = "1234567890",
                  providerName = "Dr. Smith", lineOfBusiness = "Commercial",
                  paymentMethodology = "FullCapitation", networkStatus = "Participating",
                  status = "Active", effectiveDate = "2025-01-01" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetContractsAsync();

        result.Should().HaveCount(1);
        result[0].ContractNumber.Should().Be("CN-001");
        result[0].ProviderNPI.Should().Be("1234567890");
        result[0].PaymentMethodology.Should().Be("FullCapitation");
    }

    [Fact]
    public async Task GetContractsAsync_WithFilters_BuildsQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetContractsAsync(npi: "1234567890", lob: "Medicare", status: "Active");

        var url = handler.CapturedUrls[0];
        url.Should().Contain("npi=1234567890");
        url.Should().Contain("lob=Medicare");
        url.Should().Contain("status=Active");
    }

    [Fact]
    public async Task GetContractsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetContractsAsync());
        ex.ServiceName.Should().Be("Provider Contracts Service");
    }

    // ── GetContractByIdAsync ──

    [Fact]
    public async Task GetContractByIdAsync_WhenApiReturns200_DeserializesContract()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "CTR-1", contractNumber = "CN-001", providerNPI = "1234567890",
            providerName = "Dr. Smith", status = "Active", effectiveDate = "2025-01-01",
            amendments = new[]
            {
                new { id = "AMD-1", effectiveDate = "2025-06-01", amendmentType = "RateChange",
                      description = "Rate increase", createdAt = "2025-05-01" }
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetContractByIdAsync("CTR-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("CTR-1");
        result.Amendments.Should().HaveCount(1);
        result.Amendments[0].AmendmentType.Should().Be("RateChange");
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1");
    }

    [Fact]
    public async Task GetContractByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetContractByIdAsync("CTR-NONE");

        result.Should().BeNull();
    }

    // ── GetContractByNumberAsync ──

    [Fact]
    public async Task GetContractByNumberAsync_WhenApiReturns200_UrlContainsNumber()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "CTR-1", contractNumber = "CN-001", providerNPI = "1234567890",
            providerName = "Dr. Smith", status = "Active", effectiveDate = "2025-01-01"
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetContractByNumberAsync("CN-001");

        result.Should().NotBeNull();
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/number/CN-001");
    }

    // ── CreateContractAsync ──

    [Fact]
    public async Task CreateContractAsync_WhenApiReturns200_ExtractsId()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "CTR-NEW", contractNumber = "CN-NEW", providerNPI = "9876543210",
            providerName = "Dr. Jones", status = "Draft", effectiveDate = "2025-07-01"
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.CreateContractAsync(new ProviderContractSummary
        {
            ProviderNPI = "9876543210", ProviderName = "Dr. Jones"
        });

        result.Should().Be("CTR-NEW");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
    }

    // ── UpdateContractAsync ──

    [Fact]
    public async Task UpdateContractAsync_WhenApiReturns200_SendsPutToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateContractAsync("CTR-1", new ProviderContractSummary { ProviderName = "Dr. Updated" });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1");
    }

    // ── ActivateContractAsync ──

    [Fact]
    public async Task ActivateContractAsync_WhenApiReturns200_SendsPutToActivateUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.ActivateContractAsync("CTR-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1/activate");
    }

    // ── SuspendContractAsync ──

    [Fact]
    public async Task SuspendContractAsync_WhenApiReturns200_SendsPutWithReason()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.SuspendContractAsync("CTR-1", "Non-compliance");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1/suspend");
    }

    // ── TerminateContractAsync ──

    [Fact]
    public async Task TerminateContractAsync_WhenApiReturns200_SendsPutWithReasonAndDate()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.TerminateContractAsync("CTR-1", "Contract expired", new DateTime(2025, 12, 31));

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1/terminate");
    }

    // ── ReinstateContractAsync ──

    [Fact]
    public async Task ReinstateContractAsync_WhenApiReturns200_SendsPutToReinstateUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.ReinstateContractAsync("CTR-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1/reinstate");
    }

    // ── AddAmendmentAsync ──

    [Fact]
    public async Task AddAmendmentAsync_WhenApiReturns200_PostsAmendment()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.AddAmendmentAsync("CTR-1", new ContractAmendmentSummary
        {
            AmendmentType = "RateChange", Description = "Annual rate adjustment"
        });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1/amendments");
    }

    // ── SyncChildrenAsync ──

    [Fact]
    public async Task SyncChildrenAsync_WhenApiReturns200_SendsPutToSyncUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.SyncChildrenAsync("CTR-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1/sync-children");
    }

    // ── GetRateConfigIdsAsync ──

    [Fact]
    public async Task GetRateConfigIdsAsync_WhenApiReturns200_MergesBothLists()
    {
        var json = JsonSerializer.Serialize(new
        {
            capitationRateConfigIds = new[] { "CAP-1", "CAP-2" },
            ffsRateConfigIds = new[] { "FFS-1" }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetRateConfigIdsAsync("CTR-1");

        result.Should().HaveCount(3);
        result.Should().Contain("CAP-1");
        result.Should().Contain("FFS-1");
        handler.CapturedUrls[0].Should().Contain("/v1/contracts/CTR-1/rate-configs");
    }

    [Fact]
    public async Task GetRateConfigIdsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetRateConfigIdsAsync("CTR-NONE");

        result.Should().BeEmpty();
    }

    // ── ProviderContractSummary – remaining properties ────────────────────────

    [Fact]
    public async Task GetContractByIdAsync_WhenApiReturns200_DeserializesAllProviderContractSummaryProperties()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "CTR-FULL", contractNumber = "CN-FULL-001",
            providerNPI = "1234567890", providerName = "Acme Medical Group",
            providerTin = "98-7654321",
            providerType = "Group",
            lineOfBusiness = "Commercial",
            paymentMethodology = "FullCapitation",
            networkStatus = "Participating",
            contractOwner = "contracts@healthplan.com",
            signatoryName = "Dr. Jane Director",
            signedDate = "2025-01-15T00:00:00Z",
            effectiveDate = "2025-01-01T00:00:00Z",
            terminationDate = "2027-12-31T00:00:00Z",
            terminationReason = (string?)null,
            autoRenews = true,
            renewalTermMonths = 12,
            noticeRequiredDays = 90,
            status = "Active",
            createdAt = "2024-12-01T00:00:00Z",
            lastUpdatedAt = "2025-06-01T00:00:00Z",
            amendments = Array.Empty<object>()
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetContractByIdAsync("CTR-FULL");

        result.Should().NotBeNull();
        result!.ProviderTin.Should().Be("98-7654321");
        result.ContractOwner.Should().Be("contracts@healthplan.com");
        result.SignatoryName.Should().Be("Dr. Jane Director");
        result.SignedDate.Should().NotBeNull();
        result.TerminationDate.Should().NotBeNull();
        result.TerminationReason.Should().BeNull();
        result.AutoRenews.Should().BeTrue();
        result.RenewalTermMonths.Should().Be(12);
        result.NoticeRequiredDays.Should().Be(90);
        result.CreatedAt.Should().NotBe(default);
        result.LastUpdatedAt.Should().NotBe(default);
    }
}
