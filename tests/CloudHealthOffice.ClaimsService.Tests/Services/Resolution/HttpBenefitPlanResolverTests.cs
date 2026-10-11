using ClaimsService.Services.Resolution;
using CloudHealthOffice.ClaimsService.Tests.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

/// <summary>
/// G-25: the plan's existing prior-authorization flags
/// (<c>benefits[].priorAuthRequired</c> / <c>requiresPriorAuth</c> with
/// <c>cptCodes</c>, as benefit-plan-service's <c>GET /api/v1/plans/{id}</c>
/// returns them) reach the claims pipeline.
/// </summary>
public sealed class HttpBenefitPlanResolverTests
{
    private const string PlanJson = """
        {
          "id": "6f1c2a52-6d7e-4c1a-9a43-0b3a3d2f9d10",
          "planName": "Synthetic Medicaid Plan",
          "planType": "HMO",
          "networkTiers": [ { "tierName": "Tier 1", "tierLevel": 1, "networkId": "net-1" } ],
          "benefits": [
            { "id": "b1", "benefitType": "medical", "serviceCategory": "Imaging", "isCovered": true,
              "cptCodes": ["72148", " 70553 "], "priorAuthRequired": true, "requiresPriorAuth": false },
            { "id": "b2", "benefitType": "medical", "serviceCategory": "Surgery", "isCovered": true,
              "cptCodes": ["27447"], "priorAuthRequired": false, "requiresPriorAuth": true },
            { "id": "b3", "benefitType": "medical", "serviceCategory": "Office", "isCovered": true,
              "cptCodes": ["99213"], "priorAuthRequired": false, "requiresPriorAuth": false },
            { "id": "b4", "benefitType": "medical", "serviceCategory": "Excluded", "isCovered": false,
              "cptCodes": ["S9999"], "priorAuthRequired": true },
            { "id": "b5", "benefitType": "medical", "serviceCategory": "DME", "isCovered": true,
              "cptCodes": [], "priorAuthRequired": true }
          ]
        }
        """;

    [Fact]
    public async Task GetPlanAsync_ProjectsPriorAuthRequiredProcedureCodes()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(PlanJson, System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var sut = new HttpBenefitPlanResolver(
            new BaseAddressFactory(handler), NullLogger<HttpBenefitPlanResolver>.Instance);

        var plan = await sut.GetPlanAsync("tenant-1", "6f1c2a52-6d7e-4c1a-9a43-0b3a3d2f9d10");

        Assert.NotNull(plan);
        Assert.Equal(
            new[] { "27447", "70553", "72148" },
            plan!.PriorAuthRequiredProcedureCodes.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Contains("72148", plan.PriorAuthRequiredProcedureCodes);
        Assert.Equal("tenant-1", captured!.Headers.GetValues("X-Tenant-ID").Single());
    }

    [Fact]
    public async Task GetPlanAsync_PlanWithoutBenefits_HasNoPriorAuthCodes()
    {
        var handler = FakeHttpMessageHandler.Json("""{ "id": "6f1c2a52-6d7e-4c1a-9a43-0b3a3d2f9d10", "planName": "P" }""");
        var sut = new HttpBenefitPlanResolver(
            new BaseAddressFactory(handler), NullLogger<HttpBenefitPlanResolver>.Instance);

        var plan = await sut.GetPlanAsync("tenant-1", "6f1c2a52-6d7e-4c1a-9a43-0b3a3d2f9d10");

        Assert.NotNull(plan);
        Assert.Empty(plan!.PriorAuthRequiredProcedureCodes);
    }

    private sealed class BaseAddressFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://benefit-plan-service.test") };
    }
}
