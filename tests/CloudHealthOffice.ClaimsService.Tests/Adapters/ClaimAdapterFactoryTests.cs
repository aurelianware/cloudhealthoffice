using ClaimsService.Adapters;
using ClaimsService.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters;

public class ClaimAdapterFactoryTests
{
    private static (ClaimAdapterFactory factory,
                    ClaimTenantConfigCache cache,
                    FakeHttpMessageHandler handler,
                    StubClaimAdapter cho,
                    StubClaimAdapter qnxt) BuildFactory(
        FakeHttpMessageHandler handler,
        params IClaimAdapter[] extra)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:TenantService"] = "http://tenant-service.test/api/v1",
        }).Build();

        var cache = new ClaimTenantConfigCache(
            new StubHttpClientFactory(handler),
            config,
            NullLogger<ClaimTenantConfigCache>.Instance);

        var cho = new StubClaimAdapter("cho");
        var qnxt = new StubClaimAdapter("qnxt");
        var adapters = new List<IClaimAdapter> { cho, qnxt };
        adapters.AddRange(extra);

        var factory = new ClaimAdapterFactory(
            adapters, cache, NullLogger<ClaimAdapterFactory>.Instance);
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
            """{"configuration": {"claimsPlatform": {"platform": "qnxt"}}}""");
        var (factory, _, _, _, qnxt) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-q");

        adapter.Should().BeSameAs(qnxt);
    }

    [Fact]
    public async Task GetAdapterAsync_matches_platform_case_insensitively()
    {
        var handler = FakeHttpMessageHandler.Json(
            """{"configuration": {"claimsPlatform": {"platform": "QNXT"}}}""");
        var (factory, _, _, _, qnxt) = BuildFactory(handler);

        var adapter = await factory.GetAdapterAsync("tenant-q");

        adapter.Should().BeSameAs(qnxt);
    }

    [Fact]
    public async Task GetAdapterAsync_falls_back_to_cho_on_unknown_platform()
    {
        var handler = FakeHttpMessageHandler.Json(
            """{"configuration": {"claimsPlatform": {"platform": "snowflake"}}}""");
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
            """{"configuration": {"claimsPlatform": {"platform": "qnxt"}}}""");
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
            {"configuration": {"claimsPlatform": {"platform": "qnxt", "platformSettings": {"qnxt:baseUrl": "https://qnxt.example/"}}}}
            """);
        var (factory, _, _, _, _) = BuildFactory(handler);

        var (_, settings) = await factory.GetAdapterWithSettingsAsync("tenant-s");
        settings["qnxt:baseUrl"].Should().Be("https://qnxt.example/");

        // Mutating the returned dict must not affect the next call.
        settings["qnxt:baseUrl"] = "https://attacker.example/";

        var (_, fresh) = await factory.GetAdapterWithSettingsAsync("tenant-s");
        fresh["qnxt:baseUrl"].Should().Be("https://qnxt.example/");
    }

    private sealed class StubClaimAdapter : IClaimAdapter
    {
        public string Platform { get; }
        public StubClaimAdapter(string platform) { Platform = platform; }

        public Task<ClaimAdapterResponse> GetClaimAsync(ClaimAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new ClaimAdapterResponse { Platform = Platform });
        public Task<ClaimAdapterResponse> GetClaimByNumberAsync(ClaimAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new ClaimAdapterResponse { Platform = Platform });
        public Task<ClaimAdapterResponse> GetClaimVersionAsync(ClaimAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new ClaimAdapterResponse { Platform = Platform });
        public Task<ClaimVersionListAdapterResponse> ListClaimVersionsAsync(ClaimAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new ClaimVersionListAdapterResponse { Platform = Platform });
        public Task<ClaimAdapterResponse> SubmitClaimAsync(ClaimSubmissionAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new ClaimAdapterResponse { Platform = Platform });
        public Task<ClaimSearchAdapterResponse> SearchClaimsAsync(ClaimSearchAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new ClaimSearchAdapterResponse { Platform = Platform });
        public Task<ClaimSearchAdapterResponse> SearchClaimsForMemberAsync(ClaimMemberSearchAdapterRequest r, CancellationToken ct = default)
            => Task.FromResult(new ClaimSearchAdapterResponse { Platform = Platform });
    }
}
