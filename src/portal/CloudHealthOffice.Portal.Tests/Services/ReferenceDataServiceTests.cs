using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ReferenceDataServiceTests
{
    private readonly Mock<ILogger<ReferenceDataService>> _logger = new();
    private readonly IConfiguration _configuration;

    public ReferenceDataServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ReferenceDataService"] = "http://localhost:5011"
            })
            .Build();
    }

    private ReferenceDataService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new ReferenceDataService(httpClient, _configuration, _logger.Object);
    }

    // ── SearchCodesAsync ──

    [Fact]
    public async Task SearchCodesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SearchCodesAsync());
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    [Fact]
    public async Task SearchCodesAsync_WithFilters_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchCodesAsync(codeSystem: "ICD-10", searchTerm: "diabetes"));
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    // ── GetCodeDetailsAsync ──

    [Fact]
    public async Task GetCodeDetailsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCodeDetailsAsync("ICD-10", "E11.9"));
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    // ── GetCodeSystemsAsync ──

    [Fact]
    public async Task GetCodeSystemsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetCodeSystemsAsync());
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    // ── GetCodeUsageStatsAsync ──

    [Fact]
    public async Task GetCodeUsageStatsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCodeUsageStatsAsync("ICD-10", "E11.9"));
        ex.ServiceName.Should().Be("Reference Data Service");
    }

    [Fact]
    public async Task SearchCodesAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SearchCodesAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SearchCodesAsync_WhenApiReturns200_DeserializesCodeList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { codeSystem = "ICD-10-CM", code = "E11.9", shortDescription = "Type 2 diabetes mellitus without complications",
                  category = "Endocrine", effectiveDate = "2020-10-01", status = "Active" },
            new { codeSystem = "ICD-10-CM", code = "E11.65", shortDescription = "Type 2 diabetes mellitus with hyperglycemia",
                  category = "Endocrine", effectiveDate = "2020-10-01", status = "Active" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.SearchCodesAsync();

        result.Should().HaveCount(2);
        result[0].Code.Should().Be("E11.9");
        result[0].CodeSystem.Should().Be("ICD-10-CM");
    }

    [Fact]
    public async Task SearchCodesAsync_WithFilters_IncludesQueryParams()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.SearchCodesAsync(codeSystem: "CPT", searchTerm: "office visit");

        handler.CapturedUrls[0].Should().Contain("codeSystem=CPT");
        handler.CapturedUrls[0].Should().Contain("search=office%20visit");
    }

    [Fact]
    public async Task GetCodeDetailsAsync_WhenApiReturns200_DeserializesDetails()
    {
        var json = JsonSerializer.Serialize(new
        {
            codeSystem = "CPT", code = "99213", shortDescription = "Office visit, level 3",
            category = "E/M", effectiveDate = "2021-01-01", status = "Active",
            longDescription = "Office or other outpatient visit, established patient, low complexity",
            keywords = new[] { "office", "visit", "established" },
            relatedCodes = new[]
            {
                new { codeSystem = "CPT", code = "99214", description = "Office visit, level 4", relationType = "Alternative" }
            },
            requiresPriorAuth = false
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCodeDetailsAsync("CPT", "99213");

        result.Should().NotBeNull();
        result!.LongDescription.Should().Contain("established patient");
        result.Keywords.Should().Contain("office");
        result.RelatedCodes.Should().HaveCount(1);
        handler.CapturedUrls[0].Should().Contain("/codes/CPT/99213");
    }

    [Fact]
    public async Task GetCodeSystemsAsync_WhenApiReturns200_DeserializesStringList()
    {
        var json = JsonSerializer.Serialize(new[] { "ICD-10-CM", "CPT", "HCPCS", "Revenue" }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetCodeSystemsAsync();

        result.Should().HaveCount(4);
        result.Should().Contain("CPT");
    }

    [Fact]
    public async Task GetCodeUsageStatsAsync_WhenApiReturns200_DeserializesStats()
    {
        var json = JsonSerializer.Serialize(new
        {
            codeSystem = "CPT", code = "99213", claimsCount = 5000,
            authorizationsCount = 200, benefitsCount = 10,
            lastUsedDate = "2025-03-15", totalBilledAmount = 750000m
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCodeUsageStatsAsync("CPT", "99213");

        result.ClaimsCount.Should().Be(5000);
        result.TotalBilledAmount.Should().Be(750000m);
        handler.CapturedUrls[0].Should().Contain("/codes/CPT/99213/usage");
    }

    [Fact]
    public async Task SearchCodesAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.SearchCodesAsync();
        result.Should().BeEmpty();
    }
}
