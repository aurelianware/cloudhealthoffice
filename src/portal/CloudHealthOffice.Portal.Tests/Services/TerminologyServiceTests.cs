using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class TerminologyServiceTests
{
    private readonly Mock<ILogger<TerminologyServiceImpl>> _logger = new();
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public TerminologyServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TerminologyService"] = "http://localhost:5030"
            })
            .Build();
    }

    private TerminologyServiceImpl CreateService(HttpClient httpClient)
        => new(httpClient, _configuration, _logger.Object);

    // ── TranslateAsync ──

    [Fact]
    public async Task TranslateAsync_WhenApiReturns200_DeserializesResult()
    {
        var json = JsonSerializer.Serialize(new
        {
            result = true,
            message = "Translation found",
            mapVersionId = "MAP-1",
            translatedAt = "2025-03-15T10:00:00Z",
            matches = new[]
            {
                new { equivalence = "equivalent",
                      concept = new { system = "http://hl7.org/fhir/sid/icd-10-cm", code = "J06.9", display = "Acute upper respiratory infection" },
                      isContextResolved = false, isOverride = false, source = "CMS-GEM" }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.TranslateAsync("http://hl7.org/fhir/sid/icd-9-cm", "460", "http://hl7.org/fhir/sid/icd-10-cm");

        result.Result.Should().BeTrue();
        result.Matches.Should().HaveCount(1);
        result.Matches[0].Concept.Code.Should().Be("J06.9");
        result.Matches[0].Source.Should().Be("CMS-GEM");
    }

    [Fact]
    public async Task TranslateAsync_UrlContainsRequiredParams()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { result = false, matches = Array.Empty<object>() }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.TranslateAsync("ICD9", "460", "ICD10", tenantId: "T1", age: 45, gender: "M", state: "CA");

        var url = handler.CapturedUrls[0];
        url.Should().Contain("system=ICD9");
        url.Should().Contain("code=460");
        url.Should().Contain("target=ICD10");
        url.Should().Contain("tenantId=T1");
        url.Should().Contain("age=45");
        url.Should().Contain("gender=M");
        url.Should().Contain("state=CA");
    }

    [Fact]
    public async Task TranslateAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.TranslateAsync("ICD9", "460", "ICD10"));
        ex.ServiceName.Should().Be("Terminology Service");
    }

    // ── GetMapVersionsAsync ──

    [Fact]
    public async Task GetMapVersionsAsync_WhenApiReturns200_DeserializesList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "MAP-1", mapName = "ICD-9 to ICD-10 GEM", version = "2025",
                  sourceSystem = "ICD-9-CM", targetSystem = "ICD-10-CM",
                  importedAt = "2025-01-01", isActive = true, entryCount = 24000 }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetMapVersionsAsync();

        result.Should().HaveCount(1);
        result[0].MapName.Should().Be("ICD-9 to ICD-10 GEM");
        result[0].EntryCount.Should().Be(24000);
    }

    [Fact]
    public async Task GetMapVersionsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetMapVersionsAsync());
    }

    // ── GetHealthAsync ──

    [Fact]
    public async Task GetHealthAsync_WhenApiReturns200_DeserializesStatus()
    {
        var json = JsonSerializer.Serialize(new
        {
            status = "Healthy", service = "TerminologyService",
            totalActiveEntries = 48000,
            activeMaps = new[] { new { id = "MAP-1", mapName = "GEM", version = "2025",
                sourceSystem = "ICD9", targetSystem = "ICD10",
                importedAt = "2025-01-01", isActive = true, entryCount = 24000 } },
            timestamp = "2025-03-15T10:00:00Z"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetHealthAsync();

        result.Status.Should().Be("Healthy");
        result.TotalActiveEntries.Should().Be(48000);
        result.ActiveMaps.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetHealthAsync_UrlPointsToHealthEndpoint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { status = "OK", service = "x", totalActiveEntries = 0, activeMaps = Array.Empty<object>(), timestamp = "2025-01-01" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.GetHealthAsync();

        handler.CapturedUrls[0].Should().Contain("/health");
    }

    [Fact]
    public async Task GetHealthAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetHealthAsync());
    }
}
