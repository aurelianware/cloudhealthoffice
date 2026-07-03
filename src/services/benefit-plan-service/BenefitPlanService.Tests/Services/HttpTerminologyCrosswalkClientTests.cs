using System.Net;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Adapters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Verifies the caching and fallback contract of
/// <see cref="HttpTerminologyCrosswalkClient"/>:
/// <list type="bullet">
///   <item>Cache-hit short-circuiting: a second identical batch issues no HTTP call.</item>
///   <item>Mixed hit/miss ordering: partial cache hits preserve per-line result shape and order.</item>
///   <item>Failure fallback: non-2xx and transport failures return original codes unchanged.</item>
/// </list>
/// </summary>
public sealed class HttpTerminologyCrosswalkClientTests
{
    private const string TenantId = "demo";

    private static readonly CodeCrosswalkRequest CptLine1 = new()
    {
        LineNumber = 1, ProcedureCode = "99213", CodeType = "CPT"
    };

    private static readonly CodeCrosswalkRequest HcpcsLine2 = new()
    {
        LineNumber = 2, ProcedureCode = "G0001", CodeType = "HCPCS"
    };

    [Fact]
    public async Task TranslateBatchAsync_AllCacheHits_NoHttpCallsIssued()
    {
        var handler = FakeHttpMessageHandler.Json(BatchTranslateJson("G9999", mapVersionId: "v1"));
        var client = BuildClient(handler);

        // First call — populates the cache
        await client.TranslateBatchAsync(TenantId, [CptLine1]);

        // Second call — all entries already cached
        var result = await client.TranslateBatchAsync(TenantId, [CptLine1]);

        handler.RequestCount.Should().Be(1,
            "second call is a full cache hit and must not issue an HTTP request");
        result.Should().ContainSingle()
            .Which.ResolvedCode.Should().Be("G9999");
    }

    [Fact]
    public async Task TranslateBatchAsync_MixedHitMiss_PreservesResultOrder()
    {
        var cache = NewCache();

        // Warm cache for line 1
        var warmHandler = FakeHttpMessageHandler.Json(BatchTranslateJson("G9999", mapVersionId: "v1"));
        await BuildClient(warmHandler, cache).TranslateBatchAsync(TenantId, [CptLine1]);
        warmHandler.RequestCount.Should().Be(1);

        // Second call: line 1 is a hit, line 2 is a miss
        var missHandler = FakeHttpMessageHandler.Json(BatchTranslateJson("G0002", mapVersionId: "v2"));
        var results = await BuildClient(missHandler, cache)
            .TranslateBatchAsync(TenantId, [CptLine1, HcpcsLine2]);

        missHandler.RequestCount.Should().Be(1,
            "only the miss (line 2) should trigger an HTTP call");
        results.Should().HaveCount(2);
        results[0].LineNumber.Should().Be(1);
        results[0].ResolvedCode.Should().Be("G9999", "line 1 served from cache");
        results[0].WasTranslated.Should().BeTrue();
        results[1].LineNumber.Should().Be(2);
        results[1].ResolvedCode.Should().Be("G0002", "line 2 from HTTP response");
        results[1].WasTranslated.Should().BeTrue();
    }

    [Fact]
    public async Task TranslateBatchAsync_NonSuccessResponse_ReturnsOriginalCodes()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable);
        var client = BuildClient(handler);

        var results = await client.TranslateBatchAsync(TenantId, [CptLine1, HcpcsLine2]);

        results.Should().HaveCount(2);
        results[0].ResolvedCode.Should().Be(CptLine1.ProcedureCode);
        results[0].WasTranslated.Should().BeFalse();
        results[1].ResolvedCode.Should().Be(HcpcsLine2.ProcedureCode);
        results[1].WasTranslated.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateBatchAsync_TransportFailure_ReturnsOriginalCodes()
    {
        var handler = FakeHttpMessageHandler.Throw(new HttpRequestException("connection refused"));
        var client = BuildClient(handler);

        var results = await client.TranslateBatchAsync(TenantId, [CptLine1, HcpcsLine2]);

        results.Should().HaveCount(2);
        results[0].ResolvedCode.Should().Be(CptLine1.ProcedureCode);
        results[0].WasTranslated.Should().BeFalse();
        results[1].ResolvedCode.Should().Be(HcpcsLine2.ProcedureCode);
        results[1].WasTranslated.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateBatchAsync_FailurePassthrough_LineNumbers_ArePreserved()
    {
        // Verifies that the passthrough path preserves per-line metadata so that
        // adjudication can correlate results back to the original claim lines.
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var client = BuildClient(handler);

        var results = await client.TranslateBatchAsync(TenantId, [CptLine1, HcpcsLine2]);

        results[0].LineNumber.Should().Be(CptLine1.LineNumber);
        results[0].OriginalCode.Should().Be(CptLine1.ProcedureCode);
        results[1].LineNumber.Should().Be(HcpcsLine2.LineNumber);
        results[1].OriginalCode.Should().Be(HcpcsLine2.ProcedureCode);
    }

    private static HttpTerminologyCrosswalkClient BuildClient(
        HttpMessageHandler handler, IMemoryCache? cache = null)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://terminology-service/")
        };
        return new HttpTerminologyCrosswalkClient(
            httpClient,
            cache ?? NewCache(),
            NullLogger<HttpTerminologyCrosswalkClient>.Instance);
    }

    private static IMemoryCache NewCache()
        => new MemoryCache(Options.Create(new MemoryCacheOptions()));

    /// <summary>
    /// Builds a single-element batch-translate JSON response array with one match.
    /// </summary>
    private static string BatchTranslateJson(string code, string mapVersionId)
        => $"[{{\"result\":true,\"mapVersionId\":\"{mapVersionId}\","
         + $"\"matches\":[{{\"concept\":{{\"code\":\"{code}\"}}}}]}}]";
}
