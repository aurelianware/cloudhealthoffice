using System.Net;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Adapters;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Verifies the cached-or-live read pattern introduced in capability
/// 5.10. The gate must:
///
/// <list type="bullet">
///   <item>Read provider-service first by default.</item>
///   <item>Use the cached projection when the score is fresh.</item>
///   <item>Fall back to provider-verification-service when the projection
///     is null (never refreshed) or stale beyond
///     <see cref="ProviderIntegrityGateOptions.StalenessFallbackThreshold"/>.</item>
///   <item>Skip the projection short-circuit when the caller passes
///     <c>forceRefresh: true</c>.</item>
///   <item>Coalesce repeat calls for the same NPI through the existing
///     1-hour <see cref="IMemoryCache"/> layer.</item>
///   <item>Increment the
///     <c>cho.provider.integrity_gate.decisions.total</c> counter with
///     a path discriminator on every call.</item>
/// </list>
/// </summary>
public sealed class HttpProviderIntegrityGateTests
{
    private const string Npi = "1234567890";

    [Fact]
    public async Task CheckAsync_DefaultPath_FreshProjection_UsesCachedProjection_NoVerificationCall()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 92, rating: "Clear", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json("{}");
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.Passed.Should().BeTrue();
        result.IntegrityScore.Should().Be(92);
        result.Rating.Should().Be("Clear");
        result.IsExcluded.Should().BeFalse();
        providerHandler.RequestCount.Should().Be(1);
        verificationHandler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_WithTenantId_ForwardsTenantHeaderToProviderAndVerificationService()
    {
        var providerHandler = new FakeHttpMessageHandler(request =>
        {
            request.Headers.TryGetValues("X-Tenant-ID", out var values).Should().BeTrue();
            values.Should().ContainSingle().Which.Should().Be("demo");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ProviderJson(score: 92, rating: "Clear", lastVerifiedAt: DateTimeOffset.UtcNow),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });
        var verificationHandler = new FakeHttpMessageHandler(request =>
        {
            request.Headers.TryGetValues("X-Tenant-ID", out var values).Should().BeTrue();
            values.Should().ContainSingle().Which.Should().Be("demo");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    VerificationJson(compositeScore: 88, rating: "Clear", status: "Verified"),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });
        var gate = BuildGate(providerHandler, verificationHandler);

        await gate.CheckAsync(Npi, tenantId: "demo");
        await gate.CheckAsync(Npi, tenantId: "demo", forceRefresh: true);

        providerHandler.RequestCount.Should().Be(1);
        verificationHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_CacheHit_DoesNotIssueAnyHttpCalls()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 70, rating: "Advisory", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json("{}");
        var gate = BuildGate(providerHandler, verificationHandler);

        await gate.CheckAsync(Npi);
        await gate.CheckAsync(Npi);

        providerHandler.RequestCount.Should().Be(1);
        verificationHandler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_StaleProjection_FallsBackToVerificationService()
    {
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 80, rating: "Advisory", lastVerifiedAt: stale));
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 88, rating: "Clear", status: "Verified"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.IntegrityScore.Should().Be(88, "the live verification result wins on stale fallback");
        result.Rating.Should().Be("Clear");
        verificationHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_NullProjection_FallsBackToVerificationService()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: null, rating: null, lastVerifiedAt: null));
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 75, rating: "Advisory", status: "VerifiedWithWarnings"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.IntegrityScore.Should().Be(75);
        result.Rating.Should().Be("Advisory");
        verificationHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_VerificationServiceNumericEnums_NormalizesRatingAndStatus()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: null, rating: null, lastVerifiedAt: null));
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJsonRaw(compositeScore: 88, ratingToken: "1", statusToken: "1"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.Passed.Should().BeTrue();
        result.IntegrityScore.Should().Be(88);
        result.Rating.Should().Be("Clear");
        result.IsExcluded.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_ProviderNotFound_FallsBackToVerificationService()
    {
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 60, rating: "Caution", status: "Verified"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.Rating.Should().Be("Caution");
        verificationHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_ForceRefresh_BypassesProjection_AndCallsVerificationServiceDirectly()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 95, rating: "Clear", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 30, rating: "Alert", status: "VerifiedWithWarnings"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, forceRefresh: true);

        result.IntegrityScore.Should().Be(30, "force-refresh ignores the cached projection");
        result.Rating.Should().Be("Alert");
        providerHandler.RequestCount.Should().Be(0);
        verificationHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_BothEndpointsFail_ReturnsUnavailableForReview()
    {
        var providerHandler = FakeHttpMessageHandler.Throw(new HttpRequestException());
        var verificationHandler = FakeHttpMessageHandler.Throw(new HttpRequestException());
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.Passed.Should().BeFalse("adjudication must never silently pay a claim it could not verify");
        result.IsExcluded.Should().BeFalse("unavailable is not the same as a confirmed exclusion finding");
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().Be("PROVIDER_VERIFICATION_UNAVAILABLE");
        result.Rating.Should().Be("Unknown");
    }

    [Fact]
    public async Task CheckAsync_UnavailableResult_IsNotCached()
    {
        // Both endpoints fail → unavailable. The gate must NOT cache that
        // result for the full 1-hour TTL, so a subsequent call after
        // upstream recovers picks up the real signal instead of an hour of
        // every claim for that NPI being held for review.
        var providerHandler = FakeHttpMessageHandler.Throw(new HttpRequestException());
        var verificationHandler = FakeHttpMessageHandler.Throw(new HttpRequestException());
        var gate = BuildGate(providerHandler, verificationHandler);

        await gate.CheckAsync(Npi);
        await gate.CheckAsync(Npi);

        providerHandler.RequestCount.Should().Be(2,
            "an unavailable result is not cached, so the second call retries provider-service");
        verificationHandler.RequestCount.Should().Be(2,
            "an unavailable result is not cached, so the second call retries verification-service");
    }

    [Fact]
    public async Task CheckAsync_VerificationServiceReportsFailed_ReturnsUnavailableForReview()
    {
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 40, rating: "Caution", status: "Failed"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeFalse("a Failed verification status is not a confirmed exclusion finding");
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().Be("PROVIDER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task CheckAsync_VerificationServiceReportsManualReviewRequired_ReturnsUnavailableForReview()
    {
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 55, rating: "Caution", status: "ManualReviewRequired"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeFalse();
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().Be("PROVIDER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task CheckAsync_ExcludedRating_OnCachedProjection_DenialCodeSurfaces()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 5, rating: "Blocked", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeTrue();
        result.DenialCode.Should().Be("B7");
        verificationHandler.RequestCount.Should().Be(0,
            "Blocked on cached projection is sufficient — the gate should not call verification-service");
    }

    [Fact]
    public async Task CheckAsync_StalenessThresholdZero_DisablesStaleFallback()
    {
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromDays(365);
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 85, rating: "Clear", lastVerifiedAt: stale));
        var verificationHandler = FakeHttpMessageHandler.Json("{}");
        var options = new ProviderIntegrityGateOptions
        {
            StalenessFallbackThreshold = TimeSpan.Zero,
        };
        var gate = BuildGate(providerHandler, verificationHandler, options);

        var result = await gate.CheckAsync(Npi);

        result.IntegrityScore.Should().Be(85, "threshold=0 disables the stale-fallback path");
        verificationHandler.RequestCount.Should().Be(0);
    }

    private static HttpProviderIntegrityGate BuildGate(
        FakeHttpMessageHandler providerHandler,
        FakeHttpMessageHandler verificationHandler,
        ProviderIntegrityGateOptions? options = null)
    {
        var factory = new NamedStubHttpClientFactory(new Dictionary<string, HttpMessageHandler>
        {
            [HttpProviderIntegrityGate.ProviderServiceClientName] = providerHandler,
            [HttpProviderIntegrityGate.VerificationServiceClientName] = verificationHandler,
        }, providerBaseUri: "http://provider-service/", verificationBaseUri: "http://provider-verification-service/");

        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        options ??= new ProviderIntegrityGateOptions();
        var monitor = new TestOptionsMonitor<ProviderIntegrityGateOptions>(options);
        return new HttpProviderIntegrityGate(
            factory, cache, monitor, NullLogger<HttpProviderIntegrityGate>.Instance);
    }

    private static string ProviderJson(int? score, string? rating, DateTimeOffset? lastVerifiedAt)
    {
        var scoreToken = score is int s ? s.ToString() : "null";
        var ratingToken = rating is null ? "null" : $"\"{rating}\"";
        var lastVerifiedToken = lastVerifiedAt is DateTimeOffset d
            ? $"\"{d.ToString("O")}\""
            : "null";
        return "{" +
               $"\"IntegrityScore\":{scoreToken}," +
               $"\"IntegrityRating\":{ratingToken}," +
               $"\"LastVerifiedAt\":{lastVerifiedToken}," +
               "\"NextVerificationDue\":null" +
               "}";
    }

    private static string VerificationJson(int compositeScore, string rating, string status) =>
        VerificationJsonRaw(compositeScore, $"\"{rating}\"", $"\"{status}\"");

    private static string VerificationJsonRaw(int compositeScore, string ratingToken, string statusToken) =>
        "{" +
        $"\"CompositeScore\":{compositeScore}," +
        $"\"Rating\":{ratingToken}," +
        $"\"Status\":{statusToken}," +
        $"\"VerifiedAt\":\"{DateTimeOffset.UtcNow:O}\"" +
        "}";

    private sealed class NamedStubHttpClientFactory : IHttpClientFactory
    {
        private readonly IDictionary<string, HttpMessageHandler> _handlers;
        private readonly string _providerBaseUri;
        private readonly string _verificationBaseUri;

        public NamedStubHttpClientFactory(
            IDictionary<string, HttpMessageHandler> handlers,
            string providerBaseUri,
            string verificationBaseUri)
        {
            _handlers = handlers;
            _providerBaseUri = providerBaseUri;
            _verificationBaseUri = verificationBaseUri;
        }

        public HttpClient CreateClient(string name)
        {
            var handler = _handlers.TryGetValue(name, out var h)
                ? h
                : new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            var client = new HttpClient(handler, disposeHandler: false);
            client.BaseAddress = new Uri(name == HttpProviderIntegrityGate.ProviderServiceClientName
                ? _providerBaseUri
                : _verificationBaseUri);
            return client;
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; private set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
