using ClaimsService.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters;

public class ClaimTenantConfigCacheTests
{
    private static ClaimTenantConfigCache Build(FakeHttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:TenantService"] = "http://tenant-service.test/api/v1",
        }).Build();
        return new ClaimTenantConfigCache(
            new StubHttpClientFactory(handler),
            config,
            NullLogger<ClaimTenantConfigCache>.Instance);
    }

    /// <summary>
    /// The adjudication subscription resolves the adapter with no HttpContext.
    /// The shared outbound token handler can only mint a service token for a
    /// tenant the request names, so the lookup must carry X-Tenant-ID or
    /// tenant-service rejects it and the claim silently routes to "cho".
    /// </summary>
    [Fact]
    public async Task GetAsync_names_the_tenant_on_the_outbound_request()
    {
        string? tenantHeader = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            tenantHeader = request.Headers.TryGetValues("X-Tenant-ID", out var values)
                ? values.SingleOrDefault()
                : null;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{}"),
            };
        });
        var cache = Build(handler);

        await cache.GetAsync("tenant-from-message");

        tenantHeader.Should().Be("tenant-from-message");
    }

    [Fact]
    public async Task GetAsync_returns_default_when_configuration_block_absent()
    {
        var cache = Build(FakeHttpMessageHandler.Json("""{}"""));

        var (platform, settings) = await cache.GetAsync("t-1");

        platform.Should().Be("cho");
        settings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_returns_default_when_claimsPlatform_block_absent()
    {
        var cache = Build(FakeHttpMessageHandler.Json("""{"configuration": {"otherStuff": true}}"""));

        var (platform, _) = await cache.GetAsync("t-2");

        platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetAsync_returns_configured_platform_and_settings()
    {
        var handler = FakeHttpMessageHandler.Json("""
            {"configuration": {"claimsPlatform": {"platform": "qnxt", "platformSettings": {"qnxt:baseUrl": "https://q.example/"}}}}
            """);
        var cache = Build(handler);

        var (platform, settings) = await cache.GetAsync("t-3");

        platform.Should().Be("qnxt");
        settings["qnxt:baseUrl"].Should().Be("https://q.example/");
    }

    [Fact]
    public async Task GetAsync_falls_back_to_default_on_http_error()
    {
        var cache = Build(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.ServiceUnavailable));

        var (platform, _) = await cache.GetAsync("t-4");

        platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetAsync_falls_back_to_default_on_thrown_exception()
    {
        var cache = Build(FakeHttpMessageHandler.Throw(new HttpRequestException("boom")));

        var (platform, _) = await cache.GetAsync("t-5");

        platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetAsync_caches_within_TTL()
    {
        var handler = FakeHttpMessageHandler.Json("""
            {"configuration": {"claimsPlatform": {"platform": "facets"}}}
            """);
        var cache = Build(handler);

        var first = await cache.GetAsync("t-6");
        var second = await cache.GetAsync("t-6");

        first.Platform.Should().Be("facets");
        second.Platform.Should().Be("facets");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_does_not_cache_the_default_after_failure()
    {
        // A failed lookup (5xx, 404, unreachable) uses "cho" for that call only.
        // Caching the guess would route a QNXT/Facets tenant's claims to CHO for
        // the whole TTL. Same rule as benefit-plan, provider, eligibility, id-card.
        var handler = FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.InternalServerError);
        var cache = Build(handler);

        var first = await cache.GetAsync("t-7");
        var second = await cache.GetAsync("t-7");

        first.Platform.Should().Be("cho");
        second.Platform.Should().Be("cho");
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_does_not_cache_the_default_after_404()
    {
        var handler = FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.NotFound);
        var cache = Build(handler);

        (await cache.GetAsync("t-8")).Platform.Should().Be("cho");
        (await cache.GetAsync("t-8")).Platform.Should().Be("cho");

        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_caches_an_answer_without_a_platform()
    {
        var handler = FakeHttpMessageHandler.Json("""{"configuration": {}}""");
        var cache = Build(handler);

        await cache.GetAsync("t-9");
        await cache.GetAsync("t-9");

        handler.RequestCount.Should().Be(1);

        cache.Clear();
        await cache.GetAsync("t-9");
        handler.RequestCount.Should().Be(2);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task GetAsync_refusal_fails_and_is_not_cached_as_the_default(System.Net.HttpStatusCode refusal)
    {
        // A refusal means claims-service is not trusted by tenant-service. The
        // claim must not silently route to the default platform.
        var handler = FakeHttpMessageHandler.Status(refusal);
        var cache = Build(handler);

        var first = await Assert.ThrowsAsync<ClaimTenantConfigUnavailableException>(() => cache.GetAsync("t-10"));
        first.StatusCode.Should().Be(refusal);
        await Assert.ThrowsAsync<ClaimTenantConfigUnavailableException>(() => cache.GetAsync("t-10"));

        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_url_encodes_tenant_id()
    {
        // Defensive encoding: a tenant id with '/' or '?' must not alter the
        // request path. Mirrors the Uri.EscapeDataString call in the cache.
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"configuration": {}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var cache = Build(handler);

        await cache.GetAsync("weird/tenant?id");

        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsoluteUri.Should().Contain("weird%2Ftenant%3Fid");
    }
}
