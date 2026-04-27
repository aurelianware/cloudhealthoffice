using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ProviderService.Adapters;

namespace CloudHealthOffice.ProviderService.Tests.Adapters;

/// <summary>
/// Verifies that <see cref="ProviderAdapterFactory"/> resolves the correct
/// adapter by tenant config and that <see cref="ProviderTenantConfigCache"/>
/// falls back gracefully when tenant-service is unavailable.
/// </summary>
public class ProviderAdapterFactoryTests
{
    [Fact]
    public async Task Factory_resolves_cho_adapter_by_default()
    {
        var (factory, _) = BuildFactory(stubResponse: null);

        var adapter = await factory.GetAdapterAsync("tenant-without-config");

        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task Factory_resolves_qnxt_adapter_when_tenant_configured_qnxt()
    {
        var json = """
        {
          "configuration": {
            "providerPlatform": {
              "platform": "qnxt",
              "platformSettings": {
                "baseUrl": "https://qnxt.example.com"
              }
            }
          }
        }
        """;
        var (factory, _) = BuildFactory(stubResponse: json);

        var (adapter, settings) = await factory.GetAdapterWithSettingsAsync("tenant-qnxt");

        adapter.Platform.Should().Be("qnxt");
        settings.Should().ContainKey("baseUrl").WhoseValue.Should().Be("https://qnxt.example.com");
    }

    [Fact]
    public async Task Factory_is_case_insensitive_on_platform_match()
    {
        var json = """{"configuration":{"providerPlatform":{"platform":"FACETS"}}}""";
        var (factory, _) = BuildFactory(stubResponse: json);

        var adapter = await factory.GetAdapterAsync("tenant-facets-uppercase");

        adapter.Platform.Should().Be("facets");
    }

    [Fact]
    public async Task Factory_falls_back_to_cho_when_platform_unknown()
    {
        var json = """{"configuration":{"providerPlatform":{"platform":"unknown-platform"}}}""";
        var (factory, _) = BuildFactory(stubResponse: json);

        var adapter = await factory.GetAdapterAsync("tenant-bogus");

        // Unknown platform names short-circuit to the CHO adapter so a
        // misconfigured tenant can't break provider reads.
        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task Factory_falls_back_to_cho_when_tenant_service_unreachable()
    {
        var (factory, _) = BuildFactory(simulateNetworkFailure: true);

        var adapter = await factory.GetAdapterAsync("tenant-network-error");

        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task Cache_avoids_second_http_call_within_ttl()
    {
        var json = """{"configuration":{"providerPlatform":{"platform":"healthedge"}}}""";
        var (factory, handler) = BuildFactory(stubResponse: json);

        await factory.GetAdapterAsync("tenant-cached");
        await factory.GetAdapterAsync("tenant-cached");

        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task Cache_clear_forces_refetch()
    {
        var json = """{"configuration":{"providerPlatform":{"platform":"healthedge"}}}""";
        var (factory, handler, cache) = BuildFactoryWithCache(stubResponse: json);

        await factory.GetAdapterAsync("tenant-clear");
        cache.Clear();
        await factory.GetAdapterAsync("tenant-clear");

        handler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static (ProviderAdapterFactory Factory, Mock<HttpMessageHandler> Handler) BuildFactory(
        string? stubResponse = null,
        bool simulateNetworkFailure = false)
    {
        var (factory, handler, _) = BuildFactoryWithCache(stubResponse, simulateNetworkFailure);
        return (factory, handler);
    }

    private static (ProviderAdapterFactory Factory, Mock<HttpMessageHandler> Handler, ProviderTenantConfigCache Cache) BuildFactoryWithCache(
        string? stubResponse = null,
        bool simulateNetworkFailure = false)
    {
        // Loose mock — HttpClient may invoke Dispose on the handler during
        // teardown which would trip strict-mode verification.
        var handler = new Mock<HttpMessageHandler>();
        var setup = handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        if (simulateNetworkFailure)
        {
            setup.ThrowsAsync(new HttpRequestException("simulated network failure"));
        }
        else
        {
            setup.ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = stubResponse is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                Content = new StringContent(stubResponse ?? string.Empty, Encoding.UTF8, "application/json"),
            });
        }

        var httpClient = new HttpClient(handler.Object);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(ProviderTenantConfigCache.HttpClientName))
            .Returns(httpClient);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TenantService"] = "http://tenant-service.test/api/v1",
            })
            .Build();

        var cache = new ProviderTenantConfigCache(
            httpClientFactory.Object,
            configuration,
            NullLogger<ProviderTenantConfigCache>.Instance);

        var adapters = new IProviderAdapter[]
        {
            new ChoProviderAdapter(new Fakes.InMemoryProviderRepository(), NullLogger<ChoProviderAdapter>.Instance),
            new QnxtProviderAdapter(),
            new FacetsProviderAdapter(),
            new HealthEdgeProviderAdapter(),
        };

        var factory = new ProviderAdapterFactory(
            adapters, cache, NullLogger<ProviderAdapterFactory>.Instance);

        return (factory, handler, cache);
    }
}
