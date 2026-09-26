using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using FluentAssertions;
using NSubstitute;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

public sealed class ProviderApiAuthenticationTests : IDisposable
{
    private readonly ProviderEligibilityApiFactory _factory = new();

    public ProviderApiAuthenticationTests()
    {
        _factory.Gateway.CheckEligibilityAsync(Arg.Any<GatewayEligibilityRequest>(), Arg.Any<CancellationToken>())
            .Returns(EligibilityTestData.ActiveCoverage());
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_endpoint_does_not_require_a_credential()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_credential_is_rejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", ProviderEligibilityApiFactory.PracticeTenant);

        var response = await client.PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Wrong_credential_is_rejected()
    {
        var client = _factory.CreateAuthorizedClient(key: "not-a-real-key");

        var response = await client.PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_tenant_header_is_rejected()
    {
        var client = _factory.CreateAuthorizedClient(tenant: null);

        var response = await client.PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tenant_in_query_string_is_not_accepted()
    {
        var client = _factory.CreateAuthorizedClient(tenant: null);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/eligibility/check?tenantId={ProviderEligibilityApiFactory.PracticeTenant}",
            EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Credential_cannot_act_for_another_practice()
    {
        var client = _factory.CreateAuthorizedClient(
            key: ProviderEligibilityApiFactory.PracticeKey,
            tenant: ProviderEligibilityApiFactory.OtherTenant);

        var response = await client.PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Each_client_reaches_only_its_own_tenant()
    {
        var client = _factory.CreateAuthorizedClient(
            key: ProviderEligibilityApiFactory.OtherKey,
            tenant: ProviderEligibilityApiFactory.OtherTenant);

        var response = await client.PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.Gateway.Received(1).CheckEligibilityAsync(
            Arg.Is<GatewayEligibilityRequest>(r => r.TenantId == ProviderEligibilityApiFactory.OtherTenant),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Api_is_closed_when_no_client_is_configured()
    {
        using var factory = new ProviderEligibilityApiFactory(settings: new Dictionary<string, string?>
        {
            ["ProviderApi:Clients:0:ApiKey"] = "",
            ["ProviderApi:Clients:1:Tenants:0"] = ""
        });
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Payer_search_requires_a_credential()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/payers?q=united");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
