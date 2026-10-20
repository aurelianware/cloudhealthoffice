using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BenefitPlanService.Adapters;
using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace CloudHealthOffice.BenefitPlanService.Tests.Security;

/// <summary>D-40 producer contract: the real plan route accepts a claims service token.</summary>
public sealed class ClaimsPlanReadAuthenticationTests : IClassFixture<ClaimsPlanReadAuthenticationTests.Factory>
{
    public sealed class Factory : ObservabilityTestFactory<BenefitPlansController>
    {
        public IBenefitPlanAdapter Adapter { get; } = Substitute.For<IBenefitPlanAdapter>();

        public Factory()
        {
            Adapter.Platform.Returns("cho");
            Adapter.GetPlanAsync(Arg.Any<BenefitPlanAdapterRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => new BenefitPlanAdapterResponse
                {
                    Platform = "cho",
                    Plan = new AdapterBenefitPlan
                    {
                        Id = call.Arg<BenefitPlanAdapterRequest>().PlanId!,
                        TenantId = call.Arg<BenefitPlanAdapterRequest>().TenantId,
                        PlanId = "PLAN-1", PlanName = "Plan contract test", Payer = "CHO",
                        EffectiveDate = new DateTime(2026, 1, 1),
                    },
                });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBenefitPlanService>();
                services.AddSingleton(Substitute.For<IBenefitPlanService>());
                services.RemoveAll<IBenefitPlanAdapter>();
                services.AddSingleton(Adapter);
                services.AddHttpClient(BenefitPlanTenantConfigCache.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new TenantConfigHandler());
            });
        }

        private sealed class TenantConfigHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"configuration":{}}""", Encoding.UTF8, "application/json"),
                });
        }
    }

    private readonly Factory _factory;
    public ClaimsPlanReadAuthenticationTests(Factory factory)
    {
        _factory = factory;
        _factory.Adapter.ClearReceivedCalls();
    }

    private HttpClient Client(string token, string tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    [Fact]
    public async Task Claims_service_token_reaches_the_plan_adapter_with_the_token_tenant()
    {
        using var client = Client(ChoDevelopmentAuth.ServiceTokenIssuer()
            .IssueServiceToken("claims-service", "tenant-claims"), "tenant-claims");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/plans/PLAN-1")).StatusCode);
        await _factory.Adapter.Received(1).GetPlanAsync(
            Arg.Is<BenefitPlanAdapterRequest>(r => r.TenantId == "tenant-claims" && r.PlanId == "PLAN-1"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    public async Task A_stored_role_without_benefits_read_still_cannot_read_the_plan_directly(string role)
    {
        using var client = Client(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken("examiner-1", "tenant-claims", [role], permissions: ["claims:work"]), "tenant-claims");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/plans/PLAN-1")).StatusCode);
        await _factory.Adapter.DidNotReceiveWithAnyArgs().GetPlanAsync(default!, default);
    }

    [Fact]
    public async Task A_claims_service_token_cannot_select_another_tenant_by_header()
    {
        using var client = Client(ChoDevelopmentAuth.ServiceTokenIssuer()
            .IssueServiceToken("claims-service", "tenant-claims"), "tenant-other");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/plans/PLAN-1")).StatusCode);
        await _factory.Adapter.DidNotReceiveWithAnyArgs().GetPlanAsync(default!, default);
    }
}
