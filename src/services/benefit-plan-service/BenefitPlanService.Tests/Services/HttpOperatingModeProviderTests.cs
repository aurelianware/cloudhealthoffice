using System.Net;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Adapters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// The operating mode lookup follows the tenant platform rule: a refusal from
/// tenant-service fails instead of silently treating the tenant as
/// Replace (CHO authoritative), and only real answers are cached.
/// </summary>
public class HttpOperatingModeProviderTests
{
    private static HttpOperatingModeProvider Build(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://tenant-service/") },
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<HttpOperatingModeProvider>.Instance);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refusal_throws_and_is_not_cached(HttpStatusCode refusal)
    {
        var handler = FakeHttpMessageHandler.Status(refusal);
        var provider = Build(handler);

        await Assert.ThrowsAsync<OperatingModeUnavailableException>(() => provider.GetConfigurationAsync("tenant-r"));
        await Assert.ThrowsAsync<OperatingModeUnavailableException>(() => provider.GetConfigurationAsync("tenant-r"));

        handler.RequestCount.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task No_answer_defaults_for_this_call_only(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Status(status);
        var provider = Build(handler);

        (await provider.GetConfigurationAsync("tenant-u")).TenantId.Should().Be("tenant-u");
        await provider.GetConfigurationAsync("tenant-u");

        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task Answer_is_cached_and_the_request_names_the_tenant()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tenantId":"tenant-a"}""", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var provider = Build(handler);

        await provider.GetConfigurationAsync("tenant-a");
        await provider.GetConfigurationAsync("tenant-a");

        handler.RequestCount.Should().Be(1);
        seen!.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-a");
    }
}
