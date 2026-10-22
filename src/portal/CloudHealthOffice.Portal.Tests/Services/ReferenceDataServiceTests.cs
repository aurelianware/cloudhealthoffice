using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ReferenceDataServiceTests
{
    private readonly Mock<ILogger<ReferenceDataService>> _logger = new();
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:ReferenceDataService"] = "http://reference-data-service.cloudhealthoffice/"
        })
        .Build();

    private ReferenceDataService CreateService(FakeHandler handler) =>
        new(new HttpClient(handler), _configuration, _logger.Object);

    private const string PageJson = """
        {"items":[{"id":"icd-e11.9","coding":{"codeSystem":"ICD-10-CM","code":"E11.9","display":"Type 2 diabetes"},
                   "description":"Type 2 diabetes mellitus without complications","category":"E08-E13",
                   "effectiveFrom":"2025-10-01","active":true,"sourceId":"CMS","sourceVersion":"2026",
                   "licenseClassification":"Public","exposureClassification":"PublicReference",
                   "importedAt":"2026-10-01T00:00:00Z","checksum":"x"}],
         "total":1,"pageNumber":1,"pageSize":50}
        """;

    [Fact]
    public async Task Search_calls_the_canonical_codes_route_with_the_code_system()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, PageJson);
        var page = await CreateService(handler).SearchCodesAsync("ICD-10-CM", " E11 ", ReferenceSearchMode.Prefix, active: true, page: 2, pageSize: 25);

        handler.CapturedUrls.Single().Should().Be(
            "http://reference-data-service.cloudhealthoffice/api/reference-data/codes?codeSystem=ICD-10-CM&searchMode=Prefix&page=2&pageSize=25&search=E11&active=true");
        page.Total.Should().Be(1);
        var code = page.Items.Single();
        code.Code.Should().Be("E11.9");
        code.CodeSystem.Should().Be("ICD-10-CM");
        code.Text.Should().Be("Type 2 diabetes mellitus without complications");
        code.EffectiveFrom.Should().Be(new DateOnly(2025, 10, 1));
        code.LicenseClassification.Should().Be("Public");
    }

    [Fact]
    public async Task Code_systems_with_spaces_are_escaped()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"items":[],"total":0,"pageNumber":1,"pageSize":50}""");
        await CreateService(handler).SearchCodesAsync("Revenue Codes", null, ReferenceSearchMode.Text);
        handler.CapturedUrls.Single().Should().Contain("codeSystem=Revenue%20Codes&searchMode=Text");
        handler.CapturedUrls.Single().Should().NotContain("search=");
    }

    [Fact]
    public async Task Get_returns_the_code_or_null_when_not_found()
    {
        var single = PageJson[(PageJson.IndexOf("[{") + 1)..(PageJson.IndexOf("}],") + 1)];
        var handler = new FakeHandler(HttpStatusCode.OK, single);
        (await CreateService(handler).GetCodeAsync("ICD-10-CM", "E11.9"))!.Code.Should().Be("E11.9");
        handler.CapturedUrls.Single().Should().EndWith("/api/reference-data/codes/ICD-10-CM/E11.9");

        (await CreateService(new FakeHandler(HttpStatusCode.NotFound)).GetCodeAsync("ICD-10-CM", "ZZZ")).Should().BeNull();
    }

    [Fact]
    public async Task Failures_are_reported_by_service_name()
    {
        var sut = CreateService(new FakeHandler(HttpStatusCode.InternalServerError));
        (await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.SearchCodesAsync("CPT", "99213", ReferenceSearchMode.Exact)))
            .ServiceName.Should().Be(ReferenceDataService.ServiceName);
        (await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetCodeAsync("CPT", "99213")))
            .ServiceName.Should().Be(ReferenceDataService.ServiceName);
    }
}
