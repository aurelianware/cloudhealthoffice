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
    public async Task GetAsync_caches_default_after_failure()
    {
        // Failure responses are cached with the default — repeated calls during
        // the TTL window do NOT hammer tenant-service. Mirrors Provider/BP.
        var handler = FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.InternalServerError);
        var cache = Build(handler);

        var first = await cache.GetAsync("t-7");
        var second = await cache.GetAsync("t-7");

        first.Platform.Should().Be("cho");
        second.Platform.Should().Be("cho");
        handler.RequestCount.Should().Be(1);

        cache.Clear();
        var afterReset = await cache.GetAsync("t-7");
        afterReset.Platform.Should().Be("cho");
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
