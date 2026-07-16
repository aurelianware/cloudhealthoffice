using System.Net;
using ClaimsService.Models;
using ClaimsService.Services;
using CloudHealthOffice.ClaimsService.Tests.Adapters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

public sealed class DiagnosisMetadataEnricherTests
{
    [Fact]
    public async Task EnrichAsync_MalformedReferenceDataResponse_DoesNotFailClaimRead()
    {
        var handler = FakeHttpMessageHandler.Json("<html>bad gateway</html>");
        var lookup = CreateLookup(handler);
        var enricher = new ClaimDiagnosisMetadataEnricher(lookup);
        var claim = new Claim
        {
            DiagnosisCodes =
            [
                new() { Code = "ZZZ.999" }
            ]
        };

        await enricher.EnrichAsync(claim);

        Assert.Equal(1, handler.RequestCount);
        Assert.Null(claim.DiagnosisCodes[0].Description);
        Assert.Equal("ABK", claim.DiagnosisCodes[0].CodeQualifier);
        Assert.Equal(1, claim.DiagnosisCodes[0].PointerNumber);
    }

    [Fact]
    public async Task EnrichAsync_ClaimList_LooksUpDistinctMissingCodesOnce()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var code = request.RequestUri?.Segments.LastOrDefault(s => s != "validate")?.Trim('/') ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"description":"Description for {{code}}"}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        var lookup = CreateLookup(handler);
        var enricher = new ClaimDiagnosisMetadataEnricher(lookup);
        var claims = new[]
        {
            new Claim
            {
                DiagnosisCodes =
                [
                    new() { Code = "ZZZ.101" },
                    new() { Code = "ZZZ.202" }
                ]
            },
            new Claim
            {
                DiagnosisCodes =
                [
                    new() { Code = "ZZZ.101" }
                ]
            }
        };

        await enricher.EnrichAsync(claims);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("Description for ZZZ.101", claims[0].DiagnosisCodes[0].Description);
        Assert.Equal("Description for ZZZ.202", claims[0].DiagnosisCodes[1].Description);
        Assert.Equal("Description for ZZZ.101", claims[1].DiagnosisCodes[0].Description);
    }

    private static DiagnosisDescriptionLookup CreateLookup(FakeHttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ReferenceDataDisplayLookupTimeoutMilliseconds"] = "500"
            })
            .Build();

        return new DiagnosisDescriptionLookup(
            new ReferenceDataHttpClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            NullLogger<DiagnosisDescriptionLookup>.Instance);
    }

    private sealed class ReferenceDataHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public ReferenceDataHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://reference-data.test/")
            };
    }
}
