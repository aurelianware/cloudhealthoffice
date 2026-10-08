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
    private const string Tenant = "tenant-gate";

    [Fact]
    public async Task CheckAsync_DefaultPath_FreshProjection_UsesCachedProjection_NoVerificationCall()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 92, rating: "Clear", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json("{}");
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CheckAsync_WithoutTenant_IsRefusedBeforeAnyCall(string? tenant)
    {
        var providerHandler = FakeHttpMessageHandler.Json("{}");
        var verificationHandler = FakeHttpMessageHandler.Json("{}");
        var gate = BuildGate(providerHandler, verificationHandler);

        var act = () => gate.CheckAsync(Npi, tenant!, forceRefresh: true);

        await act.Should().ThrowAsync<ArgumentException>();
        providerHandler.RequestCount.Should().Be(0);
        verificationHandler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_CachesPerTenant()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 70, rating: "Advisory", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json("{}");
        var gate = BuildGate(providerHandler, verificationHandler);

        await gate.CheckAsync(Npi, "tenant-a");
        await gate.CheckAsync(Npi, "tenant-b");

        providerHandler.RequestCount.Should().Be(2, "one tenant's result is never served to another");
    }

    [Fact]
    public async Task CheckAsync_CacheHit_DoesNotIssueAnyHttpCalls()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 70, rating: "Advisory", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json("{}");
        var gate = BuildGate(providerHandler, verificationHandler);

        await gate.CheckAsync(Npi, Tenant);
        await gate.CheckAsync(Npi, Tenant);

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

        var result = await gate.CheckAsync(Npi, Tenant);

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

        var result = await gate.CheckAsync(Npi, Tenant);

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

        var result = await gate.CheckAsync(Npi, Tenant);

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

        var result = await gate.CheckAsync(Npi, Tenant);

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

        var result = await gate.CheckAsync(Npi, Tenant, forceRefresh: true);

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

        var result = await gate.CheckAsync(Npi, Tenant);

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

        await gate.CheckAsync(Npi, Tenant);
        await gate.CheckAsync(Npi, Tenant);

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

        var result = await gate.CheckAsync(Npi, Tenant);

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

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeFalse();
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().Be("PROVIDER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task CheckAsync_BlockedRatingOnCachedProjection_LiveConfirmsExcluded_DenialCodeSurfaces()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 0, rating: "Blocked", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 0, rating: "Blocked", status: "Excluded"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeTrue();
        result.DenialCode.Should().Be("B7");
        verificationHandler.RequestCount.Should().Be(1,
            "a cached Blocked rating cannot distinguish exclusion from a low composite, so it is re-checked live");
    }

    [Fact]
    public async Task CheckAsync_BlockedRatingOnCachedProjection_LiveNotExcluded_RequiresManualReview_NotB7()
    {
        // Unscreened provider whose NPI failed validation: the projection
        // only says "Blocked"; live says ManualReviewRequired, not Excluded.
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 0, rating: "Blocked", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJsonWithFlags(compositeScore: 0, rating: "Unknown", status: "Failed",
                flagCodes: ["NPI_NOT_FOUND", "EXCLUSION_NOT_SCREENED"]));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeFalse("no exclusion was found -- this must not be denied as federally excluded");
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().NotBe("B7");
    }

    [Fact]
    public async Task CheckAsync_BlockedRatingOnCachedProjection_LiveUnavailable_RequiresManualReview_NotB7()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 5, rating: "Blocked", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse("never pay a provider whose cached rating is Blocked");
        result.IsExcluded.Should().BeFalse("the cached projection alone cannot confirm a federal exclusion");
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().Be("PROVIDER_VERIFICATION_UNAVAILABLE");
        verificationHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_StaleBlockedRating_LiveUnavailable_RequiresManualReview_NotB7()
    {
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 5, rating: "Blocked", lastVerifiedAt: stale));
        var verificationHandler = FakeHttpMessageHandler.Throw(new HttpRequestException());
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeFalse();
        result.RequiresManualReview.Should().BeTrue();
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

        var result = await gate.CheckAsync(Npi, Tenant);

        result.IntegrityScore.Should().Be(85, "threshold=0 disables the stale-fallback path");
        verificationHandler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_UnknownRatingOnCachedProjection_FallsBackToLive_NotTreatedAsClear()
    {
        // The verification engine rates a provider Unknown when no real
        // OIG/LEIE/SAM screen was performed. A fresh projection carrying
        // that rating must not short-circuit to a pass.
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 100, rating: "Unknown", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJsonWithFlags(compositeScore: 100, rating: "Unknown", status: "ManualReviewRequired",
                flagCodes: ["EXCLUSION_NOT_SCREENED"]));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse("an unscreened provider is not screened-clear");
        result.IsExcluded.Should().BeFalse("not screened is not a confirmed exclusion");
        result.RequiresManualReview.Should().BeTrue();
        result.DenialReason.Should().Contain("not screened");
        verificationHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_UnknownRatingOnCachedProjection_LiveUnavailable_ReturnsUnavailableForReview()
    {
        var providerHandler = FakeHttpMessageHandler.Json(
            ProviderJson(score: 100, rating: "Unknown", lastVerifiedAt: DateTimeOffset.UtcNow));
        var verificationHandler = FakeHttpMessageHandler.Throw(new HttpRequestException());
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse();
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().Be("PROVIDER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task CheckAsync_LiveExclusionNotScreenedFlag_RequiresManualReview_EvenIfStatusLooksVerified()
    {
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJsonWithFlags(compositeScore: 95, rating: "Clear", status: "Verified",
                flagCodes: ["EXCLUSION_NOT_SCREENED"]));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse();
        result.IsExcluded.Should().BeFalse();
        result.RequiresManualReview.Should().BeTrue();
        result.DenialCode.Should().Be("PROVIDER_VERIFICATION_UNAVAILABLE");
    }

    [Fact]
    public async Task CheckAsync_LiveUnknownRating_RequiresManualReview()
    {
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 90, rating: "Unknown", status: "Pending"));
        var gate = BuildGate(providerHandler, verificationHandler);

        var result = await gate.CheckAsync(Npi, Tenant);

        result.Passed.Should().BeFalse();
        result.RequiresManualReview.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_LiveNotScreenedResult_IsNotCached()
    {
        // LEIE/SAM not screened (Unknown + EXCLUSION_NOT_SCREENED): once the
        // sources recover, the next claim must see the real result instead of
        // pending for the rest of the cache TTL.
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJsonWithFlags(compositeScore: 90, rating: "Unknown", status: "ManualReviewRequired",
                flagCodes: ["EXCLUSION_NOT_SCREENED"]));
        var gate = BuildGate(providerHandler, verificationHandler);

        (await gate.CheckAsync(Npi, Tenant)).RequiresManualReview.Should().BeTrue();
        await gate.CheckAsync(Npi, Tenant);

        verificationHandler.RequestCount.Should().Be(2,
            "a not-screened result is not cached, so the second call re-checks live");
    }

    [Fact]
    public async Task CheckAsync_LiveNotScreenedFlagWithKnownRating_IsNotCached()
    {
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJsonWithFlags(compositeScore: 95, rating: "Clear", status: "Verified",
                flagCodes: ["EXCLUSION_NOT_SCREENED"]));
        var gate = BuildGate(providerHandler, verificationHandler);

        await gate.CheckAsync(Npi, Tenant);
        await gate.CheckAsync(Npi, Tenant);

        verificationHandler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task CheckAsync_LiveScreenedClearResult_IsCached()
    {
        var providerHandler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var verificationHandler = FakeHttpMessageHandler.Json(
            VerificationJson(compositeScore: 95, rating: "Clear", status: "Verified"));
        var gate = BuildGate(providerHandler, verificationHandler);

        (await gate.CheckAsync(Npi, Tenant)).Passed.Should().BeTrue();
        await gate.CheckAsync(Npi, Tenant);

        verificationHandler.RequestCount.Should().Be(1, "a real screened result is still cached");
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

    private static string VerificationJsonWithFlags(int compositeScore, string rating, string status, string[] flagCodes) =>
        "{" +
        $"\"CompositeScore\":{compositeScore}," +
        $"\"Rating\":\"{rating}\"," +
        $"\"Status\":\"{status}\"," +
        $"\"Flags\":[{string.Join(",", flagCodes.Select(c => $"{{\"code\":\"{c}\",\"severity\":1}}"))}]," +
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
