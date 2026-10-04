using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using NSubstitute;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests.Security;

/// <summary>
/// Who may call the API and which tenant a call acts for: CHO callers with a
/// CHO token (tenant from the token, eligibility:check required) and provider
/// applications with an API key (tenant bound to the key, never chosen by the
/// request).
/// </summary>
public sealed class ProviderEligibilityCallerAuthTests : IDisposable
{
    private const string CheckPath = "/api/v1/eligibility/check";
    private const string SecondTenant = "second-practice";

    private readonly ProviderEligibilityApiFactory _factory = new();

    public ProviderEligibilityCallerAuthTests()
    {
        GatewayAnswers(_factory);
        _factory.Payers.SearchAsync(Arg.Any<PayerSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PayerReference>)new List<PayerReference>());
    }

    public void Dispose() => _factory.Dispose();

    // ── Provider API key: tenant from the credential ─────────────────

    [Fact]
    public async Task Api_key_cannot_switch_tenant_with_the_tenant_header()
    {
        // Before: a key listed for two tenants acted for whichever one the
        // request named in X-Tenant-ID. Now the key is bound to one tenant and
        // a header naming another is refused. (The legacy Tenants list is set
        // too, so the same configuration exercised the old behaviour.)
        var key = Guid.NewGuid().ToString("N");
        using var factory = new ProviderEligibilityApiFactory(settings: new Dictionary<string, string?>
        {
            ["ProviderApi:Clients:2:Name"] = "cdo-group",
            ["ProviderApi:Clients:2:ApiKey"] = key,
            ["ProviderApi:Clients:2:TenantId"] = ProviderEligibilityApiFactory.PracticeTenant,
            ["ProviderApi:Clients:2:Tenants:0"] = ProviderEligibilityApiFactory.PracticeTenant,
            ["ProviderApi:Clients:2:Tenants:1"] = SecondTenant,
        });
        GatewayAnswers(factory);

        var response = await factory.CreateAuthorizedClient(key: key, tenant: SecondTenant)
            .PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Api_key_without_a_bound_tenant_is_refused()
    {
        // A client configured only with the old Tenants list has no bound
        // tenant, so its key authenticates nothing (fail closed).
        var key = Guid.NewGuid().ToString("N");
        using var factory = new ProviderEligibilityApiFactory(settings: new Dictionary<string, string?>
        {
            ["ProviderApi:Clients:2:Name"] = "cdo-legacy",
            ["ProviderApi:Clients:2:ApiKey"] = key,
            ["ProviderApi:Clients:2:Tenants:0"] = ProviderEligibilityApiFactory.PracticeTenant,
        });
        GatewayAnswers(factory);

        var response = await factory.CreateAuthorizedClient(key: key)
            .PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Api_key_configured_for_two_clients_authenticates_neither()
    {
        var key = Guid.NewGuid().ToString("N");
        using var factory = new ProviderEligibilityApiFactory(settings: new Dictionary<string, string?>
        {
            ["ProviderApi:Clients:2:Name"] = "cdo-a",
            ["ProviderApi:Clients:2:ApiKey"] = key,
            ["ProviderApi:Clients:2:TenantId"] = ProviderEligibilityApiFactory.PracticeTenant,
            ["ProviderApi:Clients:2:Tenants:0"] = ProviderEligibilityApiFactory.PracticeTenant,
            ["ProviderApi:Clients:3:Name"] = "cdo-b",
            ["ProviderApi:Clients:3:ApiKey"] = key,
            ["ProviderApi:Clients:3:TenantId"] = SecondTenant,
            ["ProviderApi:Clients:3:Tenants:0"] = SecondTenant,
        });
        GatewayAnswers(factory);

        var response = await factory.CreateAuthorizedClient(key: key, tenant: SecondTenant)
            .PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Api_key_with_a_bearer_token_is_refused()
    {
        var client = _factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken(ProviderEligibilityApiFactory.PracticeTenant, ChoRolePermissions.TenantAdmin));

        var response = await client.PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    // ── CHO callers: CHO token, tenant from the token ────────────────

    [Fact]
    public async Task Cho_caller_with_eligibility_check_runs_a_check_in_its_token_tenant()
    {
        var response = await _factory
            .CreateChoClient(ProviderEligibilityApiFactory.OtherTenant, ChoRolePermissions.MemberServices)
            .PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.Gateway.Received(1).CheckEligibilityAsync(
            Arg.Is<GatewayEligibilityRequest>(r => r.TenantId == ProviderEligibilityApiFactory.OtherTenant),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cho_caller_can_search_payers()
    {
        var response = await _factory
            .CreateChoClient(ProviderEligibilityApiFactory.PracticeTenant, ChoRolePermissions.MemberServices)
            .GetAsync("/api/v1/payers?q=delta");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    [InlineData(ChoRolePermissions.Finance)]
    public async Task Cho_caller_without_eligibility_check_is_forbidden(string role)
    {
        var client = _factory.CreateChoClient(ProviderEligibilityApiFactory.PracticeTenant, role);

        var check = await client.PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());
        var payers = await client.GetAsync("/api/v1/payers?q=delta");

        check.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        payers.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Cho_caller_cannot_name_another_tenant_in_the_header()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken(ProviderEligibilityApiFactory.PracticeTenant, ChoRolePermissions.MemberServices));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", ProviderEligibilityApiFactory.OtherTenant);

        var response = await client.PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Invalid_bearer_token_is_rejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        var response = await client.PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PHI handling ─────────────────────────────────────────────────

    [Fact]
    public async Task Eligibility_answers_are_not_cacheable()
    {
        var response = await _factory.CreateAuthorizedClient()
            .PostAsJsonAsync(CheckPath, EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static void GatewayAnswers(ProviderEligibilityApiFactory factory) =>
        factory.Gateway.CheckEligibilityAsync(Arg.Any<GatewayEligibilityRequest>(), Arg.Any<CancellationToken>())
            .Returns(EligibilityTestData.ActiveCoverage());
}
