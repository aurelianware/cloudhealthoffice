using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// The run-execution clients (claims-service, trading-partner-service), wired as
/// Program.cs wires them. Inside an approved run (<see cref="RunExecutionGrant"/>)
/// every call carries payment-service's service token for the run's tenant.
/// Outside one, or for another tenant, a call carries no credential at all, and
/// never the caller's token: the shared forwarding handler is not on these clients.
/// </summary>
public class RunExecutionClientTokenTests
{
    private const string Tenant = "tenant-pay";
    private const string ClientId = "payment-service";
    private const string UserToken = "user-token-must-not-leave";

    private static NoCallerHost ClaimsHost(HttpContext? caller = null) => new(ClientId, (services, outbound) =>
    {
        if (caller != null)
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = caller });
        services.AddHttpClient(ClaimsServiceClient.HttpClientName, c => c.BaseAddress = new Uri("http://claims-service/"))
            .AddRunExecutionServiceToken()
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddHttpClient("SomeOtherClient", c => c.BaseAddress = new Uri("http://member-service/"));
        services.AddSingleton<IClaimsServiceClient, ClaimsServiceClient>();
    });

    /// <summary>An authenticated inbound request carrying a user token (an approver's).</summary>
    private static HttpContext ApproverRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + UserToken;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "approver-1") }, "Bearer"));
        context.Items["TenantId"] = Tenant;
        return context;
    }

    private static void AssertServiceTokenFor(NoCallerHost host, HttpMethod method, string path)
    {
        var request = host.Outbound.Last;
        Assert.NotNull(request);
        Assert.Equal(method, request!.Method);
        Assert.Equal(path, request.RequestUri!.AbsolutePath);
        Assert.Equal((Tenant, ClientId), NoCallerHost.TokenOf(request));
        Assert.Equal(Tenant, request.Headers.GetValues("X-Tenant-ID").Single());
    }

    private static RunExecutionGrant Approved() => RunExecutionGrant.Open(Tenant, "run-1", "approver-1");

    [Fact]
    public async Task ClaimSearch_InApprovedRun_CarriesServiceToken()
    {
        using var host = ClaimsHost();
        using (Approved())
            await host.Services.GetRequiredService<IClaimsServiceClient>().SearchClaimsAsync(Tenant, "status=5&pageSize=5000");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/claims/search");
    }

    [Fact]
    public async Task ClaimRead_InApprovedRun_CarriesServiceToken()
    {
        using var host = ClaimsHost();
        using (Approved())
            await host.Services.GetRequiredService<IClaimsServiceClient>().GetClaimAsync(Tenant, "clm-1");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/claims/clm-1");
    }

    [Fact]
    public async Task Remittance_InApprovedRun_CarriesServiceToken()
    {
        using var host = ClaimsHost();
        using (Approved())
            await host.Services.GetRequiredService<IClaimsServiceClient>().PostRemittanceAsync(Tenant, "clm-1", new { checkNumber = "0001000000" });
        AssertServiceTokenFor(host, HttpMethod.Post, "/api/claims/clm-1/remittance");
    }

    [Fact]
    public async Task ClaimVoid_InApprovedRun_CarriesServiceToken()
    {
        using var host = ClaimsHost();
        using (Approved())
            await host.Services.GetRequiredService<IClaimsServiceClient>().VoidClaimAsync(Tenant, "clm-1", new { reason = "reversal" });
        AssertServiceTokenFor(host, HttpMethod.Post, "/api/claims/clm-1/void");
    }

    [Fact]
    public async Task AdjustmentList_InApprovedRun_CarriesServiceToken()
    {
        using var host = ClaimsHost();
        using (Approved())
            await host.Services.GetRequiredService<IClaimsServiceClient>().ListAdjustmentsAsync(Tenant, "status=PendingReversal&page=1&pageSize=200");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/v1/adjustments");
    }

    [Fact]
    public async Task AdjustmentRead_InApprovedRun_CarriesServiceToken()
    {
        using var host = ClaimsHost();
        using (Approved())
            await host.Services.GetRequiredService<IClaimsServiceClient>().GetAdjustmentAsync(Tenant, "adj-1");
        AssertServiceTokenFor(host, HttpMethod.Get, "/api/v1/adjustments/adj-1");
    }

    [Fact]
    public async Task TradingPartnerLookup_InApprovedRun_CarriesServiceToken()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddHttpClient(TradingPartnersClient.HttpClientName, c => c.BaseAddress = new Uri("http://trading-partner-service/"))
                .AddRunExecutionServiceToken()
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<ITradingPartnersClient, TradingPartnersClient>();
        });
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        using (Approved())
            await host.Services.GetRequiredService<ITradingPartnersClient>().GetByBillingProviderNpiAsync(Tenant, "1234567890", "Production");

        AssertServiceTokenFor(host, HttpMethod.Get, $"/api/tradingpartners/by-npi/{Tenant}/1234567890/Production");
    }

    [Fact]
    public async Task InApprovedRun_WithApproverRequest_TheApproverTokenNeverLeaves()
    {
        using var host = ClaimsHost(ApproverRequest());

        using (Approved())
            await host.Services.GetRequiredService<IClaimsServiceClient>().SearchClaimsAsync(Tenant, "status=5");

        var request = host.Outbound.Last!;
        Assert.NotEqual(UserToken, request.Headers.Authorization?.Parameter);
        Assert.Equal((Tenant, ClientId), NoCallerHost.TokenOf(request));
    }

    [Fact]
    public async Task OutsideAnApprovedRun_NoCredential_NotEvenTheCallersToken()
    {
        using var host = ClaimsHost(ApproverRequest());

        await host.Services.GetRequiredService<IClaimsServiceClient>().SearchClaimsAsync(Tenant, "status=5");

        Assert.Null(host.Outbound.Last!.Headers.Authorization);
    }

    [Fact]
    public async Task ApprovedRunForAnotherTenant_NoCredential()
    {
        using var host = ClaimsHost();

        using (RunExecutionGrant.Open("tenant-other", "run-9", "approver-1"))
            await host.Services.GetRequiredService<IClaimsServiceClient>().SearchClaimsAsync(Tenant, "status=5");

        Assert.Null(host.Outbound.Last!.Headers.Authorization);
    }

    [Fact]
    public async Task GrantEndsWhenDisposed()
    {
        using var host = ClaimsHost();
        var client = host.Services.GetRequiredService<IClaimsServiceClient>();

        using (Approved())
            await client.SearchClaimsAsync(Tenant, "status=5");
        Assert.Null(RunExecutionGrant.Current);
        await client.SearchClaimsAsync(Tenant, "status=5");

        Assert.NotNull(host.Outbound.Requests[0].Headers.Authorization);
        Assert.Null(host.Outbound.Requests[1].Headers.Authorization);
    }

    [Fact]
    public void RunExecutionClients_DoNotCarryTheSharedForwardingHandler_OtherClientsStillDo()
    {
        using var host = ClaimsHost();
        var factory = host.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        var runChain = Chain(factory.CreateHandler(ClaimsServiceClient.HttpClientName));
        Assert.Contains(runChain, h => h is RunExecutionServiceTokenHandler);
        Assert.DoesNotContain(runChain, h => h is ChoOutboundTokenHandler);

        var otherChain = Chain(factory.CreateHandler("SomeOtherClient"));
        Assert.Contains(otherChain, h => h is ChoOutboundTokenHandler);
        Assert.DoesNotContain(otherChain, h => h is RunExecutionServiceTokenHandler);
    }

    private static List<HttpMessageHandler> Chain(HttpMessageHandler handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? h = handler; h != null; h = (h as DelegatingHandler)?.InnerHandler)
            chain.Add(h);
        return chain;
    }
}
