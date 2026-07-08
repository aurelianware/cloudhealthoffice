using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// Capability 5.12a — verifies
/// <see cref="HttpBenefitCalculationEngineClient.ReverseClaimAsync"/>
/// dispatches the reversal HTTP call to BP service's
/// <c>POST /api/v1/adjudication/reverse-claim</c> endpoint with the
/// expected payload, propagates the X-Tenant-ID header from the scoped
/// adjudication context, and surfaces non-2xx responses as
/// <see cref="HttpRequestException"/>.
/// </summary>
public class HttpBenefitCalculationEngineClientReverseTests
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly IHttpClientFactory _factory;
    private readonly IHttpContextAccessor _httpContext = Substitute.For<IHttpContextAccessor>();
    private readonly IAdjudicationTenantContext _tenantContext = Substitute.For<IAdjudicationTenantContext>();

    public HttpBenefitCalculationEngineClientReverseTests()
    {
        _factory = Substitute.For<IHttpClientFactory>();
        _factory.CreateClient(HttpBenefitCalculationEngineClient.HttpClientName).Returns(_ =>
            new HttpClient(_handler) { BaseAddress = new Uri("http://benefit-plan-service:8080") });
    }

    private HttpBenefitCalculationEngineClient CreateClient() => new(
        _factory, _httpContext, _tenantContext, NullLogger<HttpBenefitCalculationEngineClient>.Instance);

    [Fact]
    public async Task CalculateAsync_RoundTripsStringEnumsInRequestAndResponse()
    {
        _tenantContext.TenantId.Returns("tenant-x");
        _handler.RespondWith(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "success": true,
                  "lines": [],
                  "totals": {},
                  "accumulatorSnapshot": [
                    {
                      "type": "IndividualDeductible",
                      "scope": "Individual",
                      "networkTier": "InNetwork",
                      "limitAmount": 1500,
                      "accumulatedAmountBefore": 100,
                      "amountApplied": 25,
                      "accumulatedAmountAfter": 125,
                      "remainingAmount": 1375,
                      "limitReached": false
                    }
                  ],
                  "timings": {}
                }
                """,
                Encoding.UTF8,
                "application/json"),
        });

        var sut = CreateClient();
        var result = await sut.CalculateAsync(new BenefitResolutionRequest
        {
            ClaimId = "claim-1",
            MemberId = "member-1",
            SubscriberId = "subscriber-1",
            BenefitPlanId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ServiceDate = new DateOnly(2026, 6, 1),
            NetworkTier = NetworkTier.InNetwork,
            Lines =
            [
                new ClaimLineInput
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    PlaceOfService = "11",
                    BilledAmount = 100m,
                },
            ],
        });

        Assert.True(result.Success);
        var snapshot = Assert.Single(result.AccumulatorSnapshot);
        Assert.Equal(AccumulatorType.IndividualDeductible, snapshot.Type);
        Assert.Equal(AccumulatorScope.Individual, snapshot.Scope);
        Assert.Equal(NetworkTier.InNetwork, snapshot.NetworkTier);

        Assert.NotNull(_handler.LastBodyJson);
        using var requestJson = JsonDocument.Parse(_handler.LastBodyJson!);
        Assert.Equal("InNetwork", requestJson.RootElement.GetProperty("networkTier").GetString());
    }

    [Fact]
    public async Task ReverseClaimAsync_HappyPath_PostsToReverseEndpointAndReturns()
    {
        _tenantContext.TenantId.Returns("tenant-x");
        _handler.RespondWith(new HttpResponseMessage(HttpStatusCode.NoContent));

        var sut = CreateClient();
        await sut.ReverseClaimAsync(
            memberId: "m1",
            subscriberId: "sub-1",
            benefitPlanId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            serviceDate: new DateOnly(2026, 5, 1),
            originalClaimId: "claim-99");

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal(HttpMethod.Post, _handler.LastRequest!.Method);
        Assert.Equal("/api/v1/adjudication/reverse-claim", _handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("tenant-x", _handler.LastRequest.Headers.GetValues("X-Tenant-ID").Single());

        Assert.NotNull(_handler.LastBodyJson);
        var doc = JsonDocument.Parse(_handler.LastBodyJson!);
        Assert.Equal("m1", doc.RootElement.GetProperty("memberId").GetString());
        Assert.Equal("sub-1", doc.RootElement.GetProperty("subscriberId").GetString());
        Assert.Equal("claim-99", doc.RootElement.GetProperty("originalClaimId").GetString());
    }

    [Fact]
    public async Task ReverseClaimAsync_HttpFailure_ThrowsHttpRequestException()
    {
        _tenantContext.TenantId.Returns("tenant-x");
        _handler.RespondWith(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("engine error")
        });

        var sut = CreateClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.ReverseClaimAsync(
            "m1", "sub-1",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new DateOnly(2026, 5, 1),
            "claim-99"));
    }

    [Fact]
    public async Task ReverseClaimAsync_BlankMemberId_Throws()
    {
        var sut = CreateClient();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.ReverseClaimAsync(
            "", "sub-1", Guid.NewGuid(), new DateOnly(2026, 5, 1), "claim-99"));
    }

    [Fact]
    public async Task ReverseClaimAsync_BlankOriginalClaimId_Throws()
    {
        var sut = CreateClient();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.ReverseClaimAsync(
            "m1", "sub-1", Guid.NewGuid(), new DateOnly(2026, 5, 1), ""));
    }

    [Fact]
    public async Task ReverseClaimAsync_TenantIdFallsBackToHttpContextWhenScopedContextEmpty()
    {
        _tenantContext.TenantId.Returns(string.Empty);
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = "fallback-tenant";
        _httpContext.HttpContext.Returns(ctx);
        _handler.RespondWith(new HttpResponseMessage(HttpStatusCode.NoContent));

        var sut = CreateClient();
        await sut.ReverseClaimAsync(
            "m1", "sub-1", Guid.NewGuid(), new DateOnly(2026, 5, 1), "claim-99");

        Assert.Equal("fallback-tenant", _handler.LastRequest!.Headers.GetValues("X-Tenant-ID").Single());
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private HttpResponseMessage _response = new(HttpStatusCode.NoContent);
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBodyJson { get; private set; }

        public void RespondWith(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastBodyJson = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _response;
        }
    }
}
