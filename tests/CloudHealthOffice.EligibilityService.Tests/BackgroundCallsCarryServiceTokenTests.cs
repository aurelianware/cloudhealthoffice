using System.Net;
using EligibilityService.Adapters;
using EligibilityService.Repositories;
using EligibilityService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// eligibility-service's calls to coverage, benefit-plan and tenant-service
/// must name the tenant, so with no inbound caller (the batch worker) the
/// shared outbound handler mints a service token for it. And the tenant
/// platform lookup follows the shared rule: a refusal fails and is never
/// cached, and only real answers are cached.
/// </summary>
public class BackgroundCallsCarryServiceTokenTests
{
    private const string Tenant = "tenant-elig";
    private const string ClientId = "eligibility-service";

    private static readonly Dictionary<string, string?> Urls = new()
    {
        ["Services:CoverageService"] = "http://coverage-service/api/v1",
        ["Services:BenefitPlanService"] = "http://benefit-plan-service/api",
        ["Services:MemberService"] = "http://member-service/api",
        ["Services:TenantService"] = "http://tenant-service/api/v1",
    };

    private static NoCallerHost Host() => new(ClientId, (services, outbound) =>
    {
        services.AddHttpClient(EligibilityAdapterFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddHttpClient("EligibilityServiceImpl").ConfigurePrimaryHttpMessageHandler(() => outbound);
    }, Urls);

    private static EligibilityServiceImpl Service(NoCallerHost host)
    {
        var factory = host.Services.GetRequiredService<IHttpClientFactory>();
        return new EligibilityServiceImpl(
            Substitute.For<IEligibilityRepository>(),
            factory.CreateClient("EligibilityServiceImpl"),
            NullLogger<EligibilityServiceImpl>.Instance,
            host.Configuration,
            new EligibilityAdapterFactory(Array.Empty<IEligibilityAdapter>(), factory, host.Configuration,
                NullLogger<EligibilityAdapterFactory>.Instance));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Route(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("/coverage/member/")
            ? Json("""[{"id":"cov-1","planId":"plan-1","insuranceLineCode":"HLT","effectiveDate":"2020-01-01T00:00:00","terminationDate":null,"status":"Active","lineOfBusiness":"Commercial"}]""")
            : request.RequestUri.AbsolutePath.EndsWith("/benefits") ? Json("[]") : Json("{}");

    [Fact]
    public async Task BenefitDetails_WithoutCaller_EveryCallCarriesServiceTokenForNamedTenant()
    {
        using var host = Host();
        host.Outbound.Respond = Route;

        await Service(host).GetBenefitDetailsAsync(Tenant, "SUB-1", "30", DateTime.Today);

        Assert.Equal(2, host.Outbound.Requests.Count);
        Assert.Contains(host.Outbound.Requests, r => r.RequestUri!.AbsolutePath == "/api/benefit-plans/plan-1/benefits");
        Assert.All(host.Outbound.Requests, r => Assert.Equal((Tenant, ClientId), NoCallerHost.TokenOf(r)));
    }

    [Fact]
    public async Task Accumulation_WithoutCaller_EveryCallCarriesServiceTokenForNamedTenant()
    {
        using var host = Host();
        host.Outbound.Respond = Route;

        await Service(host).GetAccumulationAsync(Tenant, "SUB-1");

        Assert.Equal(2, host.Outbound.Requests.Count);
        Assert.Contains(host.Outbound.Requests, r => r.RequestUri!.AbsolutePath == "/api/benefit-plans/plan-1/accumulation/SUB-1");
        Assert.All(host.Outbound.Requests, r => Assert.Equal((Tenant, ClientId), NoCallerHost.TokenOf(r)));
    }

    [Fact]
    public async Task PlatformLookup_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = Host();
        var factory = new EligibilityAdapterFactory(new IEligibilityAdapter[] { new StubAdapter("cho") },
            host.Services.GetRequiredService<IHttpClientFactory>(), host.Configuration,
            NullLogger<EligibilityAdapterFactory>.Instance);

        await factory.GetAdapterAsync(Tenant);

        Assert.Equal((Tenant, ClientId), NoCallerHost.TokenOf(host.Outbound.Last));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PlatformLookup_Refusal_FailsAndIsNotCached(HttpStatusCode refusal)
    {
        using var host = Host();
        host.Outbound.Respond = _ => new HttpResponseMessage(refusal);
        var factory = new EligibilityAdapterFactory(new IEligibilityAdapter[] { new StubAdapter("cho") },
            host.Services.GetRequiredService<IHttpClientFactory>(), host.Configuration,
            NullLogger<EligibilityAdapterFactory>.Instance);

        var ex = await Assert.ThrowsAsync<EligibilityTenantConfigUnavailableException>(() => factory.GetAdapterAsync(Tenant));
        Assert.Equal(refusal, ex.StatusCode);
        await Assert.ThrowsAsync<EligibilityTenantConfigUnavailableException>(() => factory.GetAdapterAsync(Tenant));

        Assert.Equal(2, host.Outbound.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PlatformLookup_NoAnswer_UsesChoForThisCallOnly(HttpStatusCode status)
    {
        using var host = Host();
        host.Outbound.Respond = _ => new HttpResponseMessage(status);
        var cho = new StubAdapter("cho");
        var factory = new EligibilityAdapterFactory(new IEligibilityAdapter[] { cho, new StubAdapter("availity") },
            host.Services.GetRequiredService<IHttpClientFactory>(), host.Configuration,
            NullLogger<EligibilityAdapterFactory>.Instance);

        Assert.Same(cho, await factory.GetAdapterAsync(Tenant));
        Assert.Same(cho, await factory.GetAdapterAsync(Tenant));

        Assert.Equal(2, host.Outbound.Requests.Count);
    }

    [Fact]
    public async Task PlatformLookup_Answer_IsCached()
    {
        using var host = Host();
        host.Outbound.Respond = _ => Json("""{"configuration":{"eligibilityPlatform":{"platform":"availity"}}}""");
        var availity = new StubAdapter("availity");
        var factory = new EligibilityAdapterFactory(new IEligibilityAdapter[] { new StubAdapter("cho"), availity },
            host.Services.GetRequiredService<IHttpClientFactory>(), host.Configuration,
            NullLogger<EligibilityAdapterFactory>.Instance);

        Assert.Same(availity, await factory.GetAdapterAsync(Tenant));
        Assert.Same(availity, await factory.GetAdapterAsync(Tenant));

        Assert.Single(host.Outbound.Requests);
    }

    private sealed class StubAdapter : IEligibilityAdapter
    {
        public StubAdapter(string platform) => Platform = platform;

        public string Platform { get; }

        public Task<EligibilityAdapterResponse> VerifyEligibilityAsync(
            EligibilityAdapterRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
