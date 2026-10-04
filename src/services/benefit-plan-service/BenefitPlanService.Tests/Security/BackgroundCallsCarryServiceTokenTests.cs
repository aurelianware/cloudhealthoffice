using System.Net;
using BenefitPlanService.Adapters;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.OperatingMode;
using Microsoft.Extensions.DependencyInjection;

namespace BenefitPlanService.Tests.Security;

/// <summary>
/// Calls benefit-plan-service makes to other CHO services with no inbound
/// caller (backfill jobs, accumulator rebuilds, cache misses outside a
/// request) must name their tenant, so the shared outbound handler mints a
/// service token for it. Without the tenant the call goes out with no token and
/// the callee answers 401.
/// </summary>
public class BackgroundCallsCarryServiceTokenTests
{
    private const string Tenant = "tenant-bp";
    private const string ClientId = "benefit-plan-service";

    [Fact]
    public async Task ProviderNetworkLookup_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddHttpClient(HttpProviderIntegrityGate.ProviderServiceClientName,
                    c => c.BaseAddress = new Uri("http://provider-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<IOrganizationLookupClient, HttpOrganizationLookupClient>();
        });
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"organizationId":"net-1","name":"Net","effectiveDate":"2025-01-01T00:00:00Z"}"""),
        };

        var org = await host.Services.GetRequiredService<IOrganizationLookupClient>().GetOrganizationAsync(Tenant, "net-1");

        org.Should().NotBeNull();
        host.Outbound.Last!.RequestUri!.AbsolutePath.Should().Be("/api/v1/networks/net-1");
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task TenantPlatformLookup_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddHttpClient(BenefitPlanTenantConfigCache.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<BenefitPlanTenantConfigCache>();
        }, new Dictionary<string, string?> { ["Services:TenantService"] = "http://tenant-service/api/v1" });

        await host.Services.GetRequiredService<BenefitPlanTenantConfigCache>().GetAsync(Tenant);

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task OperatingModeLookup_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddMemoryCache();
            services.AddHttpClient<IOperatingModeProvider, HttpOperatingModeProvider>(
                    c => c.BaseAddress = new Uri("http://tenant-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
        });

        await host.Services.GetRequiredService<IOperatingModeProvider>().GetConfigurationAsync(Tenant);

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task ClaimsAccumulatorRebuild_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddHttpClient<IClaimsAccumulatorSource, ClaimsServiceAccumulatorSource>(
                    c => c.BaseAddress = new Uri("http://claims-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
        });
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"totals":[]}""") };

        await host.Services.GetRequiredService<IClaimsAccumulatorSource>().CalculateAccumulatorsAsync(
            Tenant, "member-1", AccumulatorScope.Individual, Guid.NewGuid(), "2026");

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task TerminologyCrosswalk_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddMemoryCache();
            services.AddHttpClient<ITerminologyCrosswalkClient, HttpTerminologyCrosswalkClient>(
                    c => c.BaseAddress = new Uri("http://terminology-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
        });
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };

        await host.Services.GetRequiredService<ITerminologyCrosswalkClient>().TranslateBatchAsync(Tenant,
            [new CodeCrosswalkRequest { ProcedureCode = "99213", CodeType = "CPT", LineNumber = 1 }]);

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderIntegrityGate_WithoutCaller_SendsTenantAndServiceTokenToBothCallees(bool forceRefresh)
    {
        // The production clients: base addresses from Program.cs's defaults,
        // which the service's ChoAuth:Outbound:Hosts admit.
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddMemoryCache();
            services.AddOptions<ProviderIntegrityGateOptions>();
            services.AddHttpClient(HttpProviderIntegrityGate.ProviderServiceClientName,
                    c => c.BaseAddress = new Uri("http://provider-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddHttpClient(HttpProviderIntegrityGate.VerificationServiceClientName,
                    c => c.BaseAddress = new Uri("http://provider-verification-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<IProviderIntegrityGate, HttpProviderIntegrityGate>();
        });
        host.Outbound.Respond = request => request.RequestUri!.Host == "provider-service"
            // A projection that was never refreshed, so the gate goes live.
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"IntegrityScore":null}""") }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"CompositeScore":91,"Rating":"Clear","Status":"Verified"}"""),
            };

        var result = await host.Services.GetRequiredService<IProviderIntegrityGate>()
            .CheckAsync("1234567893", Tenant, forceRefresh);

        result.Passed.Should().BeTrue();
        var verification = host.Outbound.Requests.Single(r => r.RequestUri!.Host == "provider-verification-service");
        verification.RequestUri!.AbsolutePath.Should().Be("/api/v1/providers/1234567893/integrity-score");
        verification.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
        NoCallerHost.TokenOf(verification).Should().Be((Tenant, ClientId));
        foreach (var request in host.Outbound.Requests)
            NoCallerHost.TokenOf(request).Should().Be((Tenant, ClientId));
        host.Outbound.Requests.Should().HaveCount(forceRefresh ? 1 : 2);
    }
}
