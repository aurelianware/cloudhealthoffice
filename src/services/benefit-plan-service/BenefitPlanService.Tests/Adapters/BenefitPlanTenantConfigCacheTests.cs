using System.Net;
using BenefitPlanService.Adapters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Adapters;

/// <summary>
/// The tenant platform rule shared with claims, provider, eligibility and
/// id-card: only an answer tenant-service gave is cached; a refusal (401/403)
/// fails and is never cached or answered with the default; anything else uses
/// "cho" for that call only.
/// </summary>
public class BenefitPlanTenantConfigCacheTests
{
    private static BenefitPlanTenantConfigCache Build(FakeHttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:TenantService"] = "http://tenant-service.test/api/v1",
        }).Build();
        return new BenefitPlanTenantConfigCache(
            new StubHttpClientFactory(handler), config, NullLogger<BenefitPlanTenantConfigCache>.Instance);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refusal_throws_and_is_not_cached_as_the_default(HttpStatusCode refusal)
    {
        var handler = FakeHttpMessageHandler.Status(refusal);
        var cache = Build(handler);

        var first = await Assert.ThrowsAsync<BenefitPlanTenantConfigUnavailableException>(() => cache.GetAsync("tenant-r"));
        first.StatusCode.Should().Be(refusal);
        await Assert.ThrowsAsync<BenefitPlanTenantConfigUnavailableException>(() => cache.GetAsync("tenant-r"));

        handler.RequestCount.Should().Be(2, "a refusal must not be cached");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task No_answer_uses_cho_for_this_call_only(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Status(status);
        var cache = Build(handler);

        (await cache.GetAsync("tenant-u")).Platform.Should().Be("cho");
        (await cache.GetAsync("tenant-u")).Platform.Should().Be("cho");

        handler.RequestCount.Should().Be(2, "a guess must not be cached");
    }

    [Fact]
    public async Task Transport_failure_uses_cho_for_this_call_only()
    {
        var handler = FakeHttpMessageHandler.Throw(new HttpRequestException("down"));
        var cache = Build(handler);

        (await cache.GetAsync("tenant-t")).Platform.Should().Be("cho");
        (await cache.GetAsync("tenant-t")).Platform.Should().Be("cho");

        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task Answer_without_a_platform_is_cached_as_cho()
    {
        var handler = FakeHttpMessageHandler.Json("""{"configuration": {}}""");
        var cache = Build(handler);

        (await cache.GetAsync("tenant-n")).Platform.Should().Be("cho");
        (await cache.GetAsync("tenant-n")).Platform.Should().Be("cho");

        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Names_the_tenant_and_encodes_it()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await Build(handler).GetAsync("weird/tenant");

        seen!.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("weird/tenant");
        seen.RequestUri!.AbsoluteUri.Should().EndWith("/tenants/weird%2Ftenant");
    }
}
