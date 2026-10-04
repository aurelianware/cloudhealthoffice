using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// Every call payment-service makes to another CHO service names the run's
/// tenant, so with no inbound caller (a scheduled run or a background job) the
/// shared outbound handler mints a payment-service token for that tenant.
/// Without the tenant the call goes out with no token and the callee answers
/// 401. One test per outbound call.
/// </summary>
public class BackgroundCallsCarryServiceTokenTests
{
    private const string Tenant = "tenant-pay";
    private const string ClientId = "payment-service";

    private static NoCallerHost ClaimsHost() => new(ClientId, (services, outbound) =>
    {
        services.AddHttpClient(ClaimsServiceClient.HttpClientName, c => c.BaseAddress = new Uri("http://claims-service/"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddSingleton<IClaimsServiceClient, ClaimsServiceClient>();
    });

    private static void AssertServiceTokenFor(NoCallerHost host, HttpMethod method, string path)
    {
        var request = host.Outbound.Last;
        Assert.NotNull(request);
        Assert.Equal(method, request!.Method);
        Assert.Equal(path, request.RequestUri!.AbsolutePath);
        Assert.Equal((Tenant, ClientId), NoCallerHost.TokenOf(request));
    }

    [Fact]
    public async Task ClaimSearch_WithoutCaller_CarriesServiceTokenForRunTenant()
    {
        using var host = ClaimsHost();
        await host.Services.GetRequiredService<IClaimsServiceClient>().SearchClaimsAsync(Tenant, "status=5&pageSize=5000");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/claims/search");
    }

    [Fact]
    public async Task ClaimRead_WithoutCaller_CarriesServiceTokenForRunTenant()
    {
        using var host = ClaimsHost();
        await host.Services.GetRequiredService<IClaimsServiceClient>().GetClaimAsync(Tenant, "clm-1");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/claims/clm-1");
    }

    [Fact]
    public async Task Remittance_WithoutCaller_CarriesServiceTokenForRunTenant()
    {
        using var host = ClaimsHost();
        await host.Services.GetRequiredService<IClaimsServiceClient>().PostRemittanceAsync(Tenant, "clm-1", new { checkNumber = "0001000000" });
        AssertServiceTokenFor(host, HttpMethod.Post, "/api/claims/clm-1/remittance");
    }

    [Fact]
    public async Task ClaimVoid_WithoutCaller_CarriesServiceTokenForRunTenant()
    {
        using var host = ClaimsHost();
        await host.Services.GetRequiredService<IClaimsServiceClient>().VoidClaimAsync(Tenant, "clm-1", new { reason = "reversal" });
        AssertServiceTokenFor(host, HttpMethod.Post, "/api/claims/clm-1/void");
    }

    [Fact]
    public async Task AdjustmentList_WithoutCaller_CarriesServiceTokenForRunTenant()
    {
        using var host = ClaimsHost();
        await host.Services.GetRequiredService<IClaimsServiceClient>().ListAdjustmentsAsync(Tenant, "status=PendingReversal&page=1&pageSize=200");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/v1/adjustments");
    }

    [Fact]
    public async Task AdjustmentRead_WithoutCaller_CarriesServiceTokenForRunTenant()
    {
        using var host = ClaimsHost();
        await host.Services.GetRequiredService<IClaimsServiceClient>().GetAdjustmentAsync(Tenant, "adj-1");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/v1/adjustments/adj-1");
    }

    [Fact]
    public async Task TradingPartnerLookup_WithoutCaller_CarriesServiceTokenForRunTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddHttpClient(TradingPartnersClient.HttpClientName, c => c.BaseAddress = new Uri("http://trading-partner-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<ITradingPartnersClient, TradingPartnersClient>();
        });
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        await host.Services.GetRequiredService<ITradingPartnersClient>().GetByBillingProviderNpiAsync(Tenant, "1234567890", "Production");

        AssertServiceTokenFor(host, HttpMethod.Get, $"/api/tradingpartners/by-npi/{Tenant}/1234567890/Production");
    }
}
