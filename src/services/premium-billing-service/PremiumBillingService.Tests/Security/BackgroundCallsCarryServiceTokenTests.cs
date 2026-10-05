using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PremiumBillingService.Clients;

namespace PremiumBillingService.Tests.Security;

/// <summary>
/// Calls premium-billing-service makes to other CHO services with no inbound
/// caller (a scheduled billing or delinquency run) must name their tenant, so
/// the shared outbound handler mints a service token for it. Without the tenant
/// the call goes out with no token and the callee answers 401.
/// </summary>
public class BackgroundCallsCarryServiceTokenTests
{
    private const string Tenant = "tenant-pb";
    private const string ClientId = "premium-billing-service";

    private static NoCallerHost SponsorHost() => new(ClientId, (services, outbound) =>
    {
        services.AddHttpClient(SponsorServiceClient.HttpClientName, c => c.BaseAddress = new Uri("http://sponsor-service/"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddSingleton<ISponsorServiceClient, SponsorServiceClient>();
    });

    [Fact]
    public async Task ActiveSponsorList_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = SponsorHost();
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sponsors":[],"continuationToken":null,"totalCount":0}"""),
        };

        await host.Services.GetRequiredService<ISponsorServiceClient>().GetActiveSponsorsAsync(Tenant);

        host.Outbound.Last!.RequestUri!.AbsolutePath.Should().Be("/api/v1/sponsors");
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task SponsorSuspension_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = SponsorHost();

        var outcome = await host.Services.GetRequiredService<ISponsorServiceClient>()
            .SuspendSponsorAsync(Tenant, "GRP-1", "Premium delinquency");

        outcome.Success.Should().BeTrue();
        host.Outbound.Last!.Method.Should().Be(HttpMethod.Put);
        host.Outbound.Last.RequestUri!.AbsolutePath.Should().Be("/api/v1/sponsors/GRP-1/status");
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task CoverageList_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddHttpClient(CoverageServiceClient.HttpClientName, c => c.BaseAddress = new Uri("http://coverage-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<ICoverageServiceClient, CoverageServiceClient>();
        });
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"coverage":[],"continuationToken":null,"totalCount":0}"""),
        };

        await host.Services.GetRequiredService<ICoverageServiceClient>().GetActiveCoveragesByGroupAsync(Tenant, "GRP-1");

        host.Outbound.Last!.RequestUri!.AbsolutePath.Should().Be("/api/v1/coverage");
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }
}
