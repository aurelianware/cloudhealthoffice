using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Services;
using FhirService.Services.PublicDirectory;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// D-29: the public Provider Directory. Anonymous callers read a published
/// plan's directory; the tenant comes from the published directory id only, a
/// credential on the request changes nothing, and CHO-internal content never
/// leaves.
/// </summary>
public class PublicProviderDirectoryTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "tenant-acme";
    private const string IntegrityExt = PublicDirectoryResponseFilter.ChoExtensionBase + "provider-integrity-score";
    private const string PanelExt = PublicDirectoryResponseFilter.ChoExtensionBase + "practitionerrole-panel-gating";

    private readonly FhirTestWebAppFactory _factory;

    public PublicProviderDirectoryTests(FhirTestWebAppFactory factory) => _factory = factory;

    private (WebApplicationFactory<Program> Host, Upstream Providers, Upstream Plans) Host(int permitPerMinute = 1000)
    {
        var providers = new Upstream();
        var plans = new Upstream();
        var host = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("PublicProviderDirectory:Directories:acme-health", Tenant);
            b.UseSetting("PublicProviderDirectory:PermitPerMinute", permitPerMinute.ToString());
            b.ConfigureServices(services =>
            {
                services.AddHttpClient("ProviderService").ConfigurePrimaryHttpMessageHandler(() => providers);
                services.AddHttpClient(UpstreamClientNames.BenefitPlanService).ConfigurePrimaryHttpMessageHandler(() => plans);
            });
        });
        return (host, providers, plans);
    }

    private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> host, string path, string? bearer = null)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private static string PractitionerBundle() => $$$"""
        {"resourceType":"Bundle","type":"searchset","total":2,"entry":[
          {"resource":{"resourceType":"Practitioner","id":"1234567893","active":true,
            "identifier":[{"system":"http://hl7.org/fhir/sid/us-npi","value":"1234567893"}],
            "name":[{"family":"Smith"}],
            "extension":[{"url":"{{{IntegrityExt}}}","extension":[{"url":"score","valueInteger":42}]}]}},
          {"resource":{"resourceType":"Practitioner","id":"1111111112","active":false,"name":[{"family":"Gone"}]}}
        ]}
        """;

    private static void AssertServiceTokenFor(HttpRequestMessage sent, string tenant)
    {
        sent.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(tenant);
        sent.Headers.Authorization.Should().NotBeNull();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization!.Parameter);
        jwt.Issuer.Should().Be(ChoDevelopmentAuth.ServiceIssuer);
        jwt.Subject.Should().Be("fhir-service");
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == tenant);
    }

    [Fact]
    public async Task Anonymous_search_reads_the_published_plan_as_fhir_service()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, PractitionerBundle());

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner?family=Smith");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = providers.Last!;
        sent.RequestUri!.PathAndQuery.Should().Be("/fhir/Practitioner?family=Smith");
        AssertServiceTokenFor(sent, Tenant);
    }

    [Fact]
    public async Task Search_results_lose_internal_extensions_and_inactive_providers()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, PractitionerBundle());

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("provider-integrity-score").And.NotContain("Gone");
        var bundle = JsonNode.Parse(body)!.AsObject();
        bundle["entry"]!.AsArray().Should().ContainSingle();
        bundle.ContainsKey("total").Should().BeFalse("the upstream total counted the inactive provider");
    }

    [Fact]
    public async Task An_unpublished_directory_is_not_found_and_nothing_is_called()
    {
        var (host, providers, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, "/fhir/r4/directory/other-plan/Practitioner");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        providers.Last.Should().BeNull();
    }

    [Fact]
    public async Task A_cho_token_for_another_tenant_changes_nothing()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, PractitionerBundle());
        var token = ChoDevelopmentAuth.UserToken("tenant-other", ChoRolePermissions.UMCoordinator);

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertServiceTokenFor(providers.Last!, Tenant);
        providers.Last!.Headers.Authorization!.Parameter.Should().NotBe(token);
    }

    [Fact]
    public async Task A_smart_token_is_never_forwarded_or_used()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, PractitionerBundle());
        var token = _factory.IssueToken("patient/*.read", "member-1", "tenant-other");

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertServiceTokenFor(providers.Last!, Tenant);
        providers.Last!.Headers.Authorization!.Parameter.Should().NotBe(token);
    }

    [Fact]
    public async Task A_token_with_no_tenant_is_not_refused()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, PractitionerBundle());

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner",
            _factory.IssueToken("user/*.read", tenantId: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reading_an_inactive_provider_is_not_found()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, """{"resourceType":"Practitioner","id":"1111111112","active":false}""");

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner/1111111112");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_organization_loses_its_tax_id_but_keeps_its_npi()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, $$"""
            {"resourceType":"Organization","id":"org-1","active":true,"identifier":[
              {"system":"http://hl7.org/fhir/sid/us-npi","value":"1234567893"},
              {"system":"{{PublicDirectoryResponseFilter.EinSystem}}","value":"12-3456789"}]}
            """);

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Organization/org-1");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("1234567893").And.NotContain("12-3456789");
        providers.Last!.RequestUri!.AbsolutePath.Should().Be("/fhir/Organization/org-1");
    }

    [Fact]
    public async Task Insurance_plans_come_from_benefit_plan_service()
    {
        var (host, _, plans) = Host();
        using var _h = host;
        plans.Respond(HttpStatusCode.OK, """{"resourceType":"Bundle","type":"searchset","entry":[]}""");

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/InsurancePlan");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertServiceTokenFor(plans.Last!, Tenant);
        plans.Last!.RequestUri!.AbsolutePath.Should().Be("/fhir/InsurancePlan");
    }

    [Fact]
    public async Task Upstream_failure_is_a_bad_gateway_without_its_body()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond(HttpStatusCode.InternalServerError, "internal detail that must not leak");

        using var response = await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("internal detail");
    }

    [Fact]
    public async Task Callers_past_the_permit_are_throttled()
    {
        var (host, providers, _) = Host(permitPerMinute: 2);
        using var _h = host;
        providers.Respond(HttpStatusCode.OK, PractitionerBundle());

        for (var i = 0; i < 2; i++)
            (await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetAsync(host, "/fhir/r4/directory/acme-health/Practitioner")).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_authenticated_directory_still_requires_a_token()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, "/fhir/r4/Practitioner");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("acme-health", Tenant)]
    [InlineData("Acme-Health", null)]
    [InlineData("-acme", null)]
    [InlineData("acme health", null)]
    [InlineData("", null)]
    [InlineData("unknown", null)]
    public void Directory_ids_resolve_only_when_published_and_well_formed(string id, string? expected)
    {
        var options = new PublicProviderDirectoryOptions { Directories = { ["acme-health"] = Tenant } };
        options.ResolveTenant(id).Should().Be(expected);
    }

    [Fact]
    public void Public_cho_extensions_are_kept()
    {
        var filtered = PublicDirectoryResponseFilter.Filter($$"""
            {"resourceType":"PractitionerRole","id":"r1","extension":[
              {"url":"{{PanelExt}}","extension":[{"url":"accepting-new-patients","valueBoolean":true}]},
              {"url":"{{IntegrityExt}}"}]}
            """);

        filtered.Should().Contain(PanelExt).And.Contain("accepting-new-patients").And.NotContain("provider-integrity-score");
    }

    [Fact]
    public void A_body_that_is_not_a_resource_is_withheld()
    {
        PublicDirectoryResponseFilter.Filter("not json").Should().BeNull();
        PublicDirectoryResponseFilter.Filter("""{"id":"x"}""").Should().BeNull();
    }

    internal sealed class Upstream : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];
        private (HttpStatusCode Status, string Body) _response = (HttpStatusCode.OK, "{}");

        public HttpRequestMessage? Last
        {
            get { lock (_requests) return _requests.LastOrDefault(); }
        }

        public void Respond(HttpStatusCode status, string body) => _response = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(_response.Status)
            {
                Content = new StringContent(_response.Body, Encoding.UTF8, "application/fhir+json"),
            });
        }
    }
}
