using System.Net;
using CoverageService.Repositories;
using CoverageService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoverageService.Tests.Security;

/// <summary>
/// coverage-service reads providers from provider-service
/// (<c>GET /api/Providers/npi/{npi}</c>) during PCP assignment and from the
/// panel reconciliation job, which has no inbound caller. The call must name
/// the tenant so the shared outbound handler mints a service token for it;
/// otherwise it goes out with no token and provider-service answers 401.
/// </summary>
public class BackgroundCallsCarryServiceTokenTests
{
    private const string Tenant = "tenant-cov";
    private const string ClientId = "coverage-service";

    private static NoCallerHost Host() => new(ClientId, (services, outbound) =>
    {
        services.Configure<ProviderServiceOptions>(o => o.BaseUrl = "http://provider-service:8080");
        services.AddHttpClient<IProviderServiceClient, HttpProviderServiceClient>(
                c => c.BaseAddress = new Uri("http://provider-service:8080"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
    });

    [Fact]
    public async Task ProviderByNpi_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = Host();
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"p-1","npi":"1234567893"}""", System.Text.Encoding.UTF8, "application/json"),
        };

        var provider = await host.Services.GetRequiredService<IProviderServiceClient>().GetByNpiAsync(Tenant, "1234567893");

        provider.Should().NotBeNull();
        host.Outbound.Last!.RequestUri!.AbsolutePath.Should().Be("/api/Providers/npi/1234567893");
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task PanelReconciliationJob_ProviderReads_CarryServiceTokenForTheScannedTenant()
    {
        using var host = Host();
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var job = new PcpPanelReconciliationJob(
            Mock.Of<IPcpAssignmentRepository>(),
            host.Services.GetRequiredService<IProviderServiceClient>(),
            Mock.Of<ILogger<PcpPanelReconciliationJob>>());

        await job.ScanAsync(Tenant, new[] { "1234567893" });

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }
}
