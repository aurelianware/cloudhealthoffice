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
    public async Task FindDescriptionAsync_TerminologyHitForUnknownCode_ReturnsTerminologyDisplay()
    {
        var terminologyHandler = FakeHttpMessageHandler.Json(
            """{"result":true,"display":"Terminology display for custom code"}""");
        var referenceHandler = FakeHttpMessageHandler.Throw(
            new InvalidOperationException("Reference data should not be called when terminology resolves."));
        var lookup = CreateLookup(referenceHandler, terminologyHandler);

        var description = await lookup.FindDescriptionAsync("ZZZ.456");

        Assert.Equal("Terminology display for custom code", description);
        Assert.Equal(1, terminologyHandler.RequestCount);
        Assert.Equal(0, referenceHandler.RequestCount);
    }

    [Fact]
    public async Task FindDescriptionAsync_TerminologyHitForSyntheticCode_ReturnsTerminologyDisplay()
    {
        var terminologyHandler = FakeHttpMessageHandler.Json(
            """{"result":true,"display":"Terminology catalog display for diabetes"}""");
        var referenceHandler = FakeHttpMessageHandler.Throw(
            new InvalidOperationException("Reference data should not be called when terminology resolves."));
        var lookup = CreateLookup(referenceHandler, terminologyHandler);

        var description = await lookup.FindDescriptionAsync("E11.65");

        Assert.Equal("Terminology catalog display for diabetes", description);
        Assert.Equal(1, terminologyHandler.RequestCount);
        Assert.Equal(0, referenceHandler.RequestCount);
    }

    [Fact]
    public async Task FindDescriptionAsync_TerminologyUnavailable_UsesSyntheticFallback()
    {
        var terminologyHandler = FakeHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable);
        var referenceHandler = FakeHttpMessageHandler.Throw(
            new InvalidOperationException("Reference data should not be called for known synthetic codes."));
        var lookup = CreateLookup(referenceHandler, terminologyHandler);

        var description = await lookup.FindDescriptionAsync("M79.3");

        Assert.Equal("Panniculitis, unspecified", description);
        Assert.Equal(1, terminologyHandler.RequestCount);
        Assert.Equal(0, referenceHandler.RequestCount);
    }

    [Fact]
    public async Task FindDescriptionAsync_TerminologyUnavailable_UsesReferenceDataFallback()
    {
        var terminologyHandler = FakeHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable);
        var referenceHandler = FakeHttpMessageHandler.Json(
            """{"description":"Reference data display"}""");
        var lookup = CreateLookup(referenceHandler, terminologyHandler);

        var description = await lookup.FindDescriptionAsync("ZZZ.123");

        Assert.Equal("Reference data display", description);
        Assert.Equal(1, terminologyHandler.RequestCount);
        Assert.Equal(1, referenceHandler.RequestCount);
    }

    [Fact]
    public async Task EnrichAsync_MalformedReferenceDataResponse_DoesNotFailClaimRead()
    {
        var terminologyHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var referenceHandler = FakeHttpMessageHandler.Json("<html>bad gateway</html>");
        var lookup = CreateLookup(referenceHandler, terminologyHandler);
        var enricher = new ClaimDiagnosisMetadataEnricher(lookup);
        var claim = new Claim
        {
            DiagnosisCodes =
            [
                new() { Code = "ZZZ.999" }
            ]
        };

        await enricher.EnrichAsync(claim);

        Assert.Equal(1, terminologyHandler.RequestCount);
        Assert.Equal(1, referenceHandler.RequestCount);
        Assert.Null(claim.DiagnosisCodes[0].Description);
        Assert.Equal("ABK", claim.DiagnosisCodes[0].CodeQualifier);
        Assert.Equal(1, claim.DiagnosisCodes[0].PointerNumber);
    }

    [Fact]
    public async Task EnrichAsync_ClaimList_LooksUpDistinctMissingCodesOnce()
    {
        var terminologyHandler = new FakeHttpMessageHandler(request =>
        {
            var code = QueryValue(request.RequestUri, "code") ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"result":true,"display":"Description for {{code}}"}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        var referenceHandler = FakeHttpMessageHandler.Throw(
            new InvalidOperationException("Reference data should not be called when terminology resolves."));
        var lookup = CreateLookup(referenceHandler, terminologyHandler);
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

        Assert.Equal(2, terminologyHandler.RequestCount);
        Assert.Equal(0, referenceHandler.RequestCount);
        Assert.Equal("Description for ZZZ.101", claims[0].DiagnosisCodes[0].Description);
        Assert.Equal("Description for ZZZ.202", claims[0].DiagnosisCodes[1].Description);
        Assert.Equal("Description for ZZZ.101", claims[1].DiagnosisCodes[0].Description);
    }

    private static DiagnosisDescriptionLookup CreateLookup(
        FakeHttpMessageHandler referenceDataHandler,
        FakeHttpMessageHandler? terminologyHandler = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ReferenceDataDisplayLookupTimeoutMilliseconds"] = "500",
                ["Services:TerminologyDisplayLookupTimeoutMilliseconds"] = "500"
            })
            .Build();

        return new DiagnosisDescriptionLookup(
            new DiagnosisLookupHttpClientFactory(
                referenceDataHandler,
                terminologyHandler ?? FakeHttpMessageHandler.Status(HttpStatusCode.NotFound)),
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            NullLogger<DiagnosisDescriptionLookup>.Instance);
    }

    private static string? QueryValue(Uri? uri, string key)
    {
        var query = uri?.Query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Split('=', 2);
            if (tokens.Length == 2 && tokens[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(tokens[1]);
            }
        }

        return null;
    }

    private sealed class DiagnosisLookupHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _referenceDataHandler;
        private readonly HttpMessageHandler _terminologyHandler;

        public DiagnosisLookupHttpClientFactory(
            HttpMessageHandler referenceDataHandler,
            HttpMessageHandler terminologyHandler)
        {
            _referenceDataHandler = referenceDataHandler;
            _terminologyHandler = terminologyHandler;
        }

        public HttpClient CreateClient(string name)
        {
            var (handler, baseAddress) = name switch
            {
                UpstreamClientNames.TerminologyService => (_terminologyHandler, "http://terminology.test/"),
                UpstreamClientNames.ReferenceDataService => (_referenceDataHandler, "http://reference-data.test/"),
                _ => throw new InvalidOperationException($"Unexpected client: {name}")
            };

            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri(baseAddress)
            };
        }
    }
}
