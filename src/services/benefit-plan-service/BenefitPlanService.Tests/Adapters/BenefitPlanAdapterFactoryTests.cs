using BenefitPlanService.Adapters;
using BenefitPlanService.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Adapters;

public class BenefitPlanAdapterFactoryTests
{
    private static (BenefitPlanAdapterFactory factory,
                    BenefitPlanTenantConfigCache cache,
                    FakeHttpMessageHandler handler,
                    StubBenefitPlanAdapter cho,
                    StubBenefitPlanAdapter qnxt) BuildFactory(
        FakeHttpMessageHandler handler,
        params IBenefitPlanAdapter[] extra)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:TenantService"] = "http://tenant-service.test/api/v1",
        }).Build();

        var cache = new BenefitPlanTenantConfigCache(
            new StubHttpClientFactory(handler),
            config,
            NullLogger<BenefitPlanTenantConfigCache>.Instance);

        var cho = new StubBenefitPlanAdapter("cho");
        var qnxt = new StubBenefitPlanAdapter("qnxt");
        var adapters = new List<IBenefitPlanAdapter> { cho, qnxt };
        adapters.AddRange(extra);

        var factory = new BenefitPlanAdapterFactory(
            adapters, cache, NullLogger<BenefitPlanAdapterFactory>.Instance);
        return (factory, cache, handler, cho, qnxt);
    }

    [Fact]
    public async Task GetAdapterAsync_returns_cho_when_tenant_has_no_platform_config()
    {
        var handler = FakeHttpMessageHandler.Json("""{"configuration": {}}""");
        var (factory, _, _, cho, _) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-1");

        adapter.Should().BeSameAs(cho);
    }

    [Fact]
    public async Task GetAdapterAsync_returns_configured_platform()
    {
        var handler = FakeHttpMessageHandler.Json(
            """{"configuration": {"benefitPlanPlatform": {"platform": "qnxt"}}}""");
        var (factory, _, _, _, qnxt) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-q");

        adapter.Should().BeSameAs(qnxt);
    }

    [Fact]
    public async Task GetAdapterAsync_matches_platform_case_insensitively()
    {
        var handler = FakeHttpMessageHandler.Json(
            """{"configuration": {"benefitPlanPlatform": {"platform": "QNXT"}}}""");
        var (factory, _, _, _, qnxt) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-q");

        adapter.Should().BeSameAs(qnxt);
    }

    [Fact]
    public async Task GetAdapterAsync_falls_back_to_cho_on_unknown_platform()
    {
        var handler = FakeHttpMessageHandler.Json(
            """{"configuration": {"benefitPlanPlatform": {"platform": "unknown-vendor"}}}""");
        var (factory, _, _, cho, _) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-x");

        adapter.Should().BeSameAs(cho);
    }

    [Fact]
    public async Task GetAdapterAsync_falls_back_to_cho_on_http_failure()
    {
        var handler = FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.InternalServerError);
        var (factory, _, _, cho, _) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-y");

        adapter.Should().BeSameAs(cho);
    }

    [Fact]
    public async Task GetAdapterAsync_falls_back_to_cho_when_http_throws()
    {
        var handler = FakeHttpMessageHandler.Throw(new HttpRequestException("network down"));
        var (factory, _, _, cho, _) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-z");

        adapter.Should().BeSameAs(cho);
    }

    [Fact]
    public async Task GetAdapterAsync_caches_tenant_lookup()
    {
        var handler = FakeHttpMessageHandler.Json(
            """{"configuration": {"benefitPlanPlatform": {"platform": "qnxt"}}}""");
        var (factory, _, h, _, qnxt) = BuildFactory(handler);

        var first = await factory.GetAdapterAsync("tenant-cache");
        var second = await factory.GetAdapterAsync("tenant-cache");

        first.Should().BeSameAs(qnxt);
        second.Should().BeSameAs(qnxt);
        h.RequestCount.Should().Be(1, "second call should be served from cache");
    }

    [Fact]
    public async Task GetAdapterWithSettingsAsync_returns_isolated_copy_of_settings()
    {
        var handler = FakeHttpMessageHandler.Json("""
            {"configuration": {"benefitPlanPlatform": {"platform": "qnxt", "platformSettings": {"qnxt:baseUrl": "https://qnxt.example/"}}}}
            """);
        var (factory, _, _, _, _) = BuildFactory(handler);

        var (_, settings) = await factory.GetAdapterWithSettingsAsync("tenant-s");
        settings["qnxt:baseUrl"].Should().Be("https://qnxt.example/");

        // Mutating the returned dict must not affect the next call.
        settings["qnxt:baseUrl"] = "https://attacker.example/";

        var (_, fresh) = await factory.GetAdapterWithSettingsAsync("tenant-s");
        fresh["qnxt:baseUrl"].Should().Be("https://qnxt.example/");
    }

    private sealed class StubBenefitPlanAdapter : IBenefitPlanAdapter
    {
        public string Platform { get; }
        public StubBenefitPlanAdapter(string platform) { Platform = platform; }
        public Task<BenefitPlanAdapterResponse> GetPlanAsync(BenefitPlanAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new BenefitPlanAdapterResponse { Platform = Platform });
        public Task<BenefitPlanAdapterResponse> GetPlanVersionAsync(BenefitPlanAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new BenefitPlanAdapterResponse { Platform = Platform });
        public Task<MemberBenefitViewAdapterResponse> GetMemberBenefitViewAsync(BenefitPlanAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new MemberBenefitViewAdapterResponse { Platform = Platform });
    }
}
