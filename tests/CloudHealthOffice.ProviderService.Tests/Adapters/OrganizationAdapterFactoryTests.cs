using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using ProviderService.Adapters;

namespace CloudHealthOffice.ProviderService.Tests.Adapters;

/// <summary>
/// Routing tests for <see cref="OrganizationAdapterFactory"/>. Mirrors the
/// ProviderAdapterFactory fallback semantics — flaky tenant-service or an
/// unknown platform must fall back to the CHO adapter so reads keep working.
/// </summary>
public class OrganizationAdapterFactoryTests
{
    [Fact]
    public async Task GetAdapterAsync_returns_cho_when_tenant_service_fails()
    {
        var factory = NewFactory(out _, simulateFailure: true);

        var adapter = await factory.GetAdapterAsync("tenant-broken");

        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetAdapterAsync_returns_cho_for_unknown_platform()
    {
        var factory = NewFactory(out _, platform: "doesnotexist");

        var adapter = await factory.GetAdapterAsync("tenant-x");

        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetAdapterAsync_routes_to_qnxt_when_configured()
    {
        var factory = NewFactory(out _, platform: "qnxt");

        var adapter = await factory.GetAdapterAsync("tenant-qnxt");

        adapter.Platform.Should().Be("qnxt");
    }

    private static OrganizationAdapterFactory NewFactory(
        out ProviderTenantConfigCache cache,
        string? platform = null,
        bool simulateFailure = false)
    {
        var handler = new Mock<HttpMessageHandler>();
        if (simulateFailure)
        {
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("simulated outage"));
        }
        else
        {
            var body = new
            {
                configuration = new
                {
                    providerPlatform = new
                    {
                        platform = platform ?? "cho",
                        platformSettings = new Dictionary<string, string>()
                    }
                }
            };
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(body)
                });
        }

        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object));

        var configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TenantService"] = "http://tenant-service.test/api/v1"
            })
            .Build();

        cache = new ProviderTenantConfigCache(
            clientFactory.Object,
            configRoot,
            NullLogger<ProviderTenantConfigCache>.Instance);

        var adapters = new IOrganizationAdapter[]
        {
            new ChoOrganizationAdapter(
                new Fakes.InMemoryOrganizationRepository(),
                NullLogger<ChoOrganizationAdapter>.Instance),
            new QnxtOrganizationAdapter(),
            new FacetsOrganizationAdapter(),
        };

        return new OrganizationAdapterFactory(
            adapters,
            cache,
            NullLogger<OrganizationAdapterFactory>.Instance);
    }
}
