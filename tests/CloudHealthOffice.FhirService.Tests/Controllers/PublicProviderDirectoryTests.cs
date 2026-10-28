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
/// D-29 / G-36: the public Provider Directory, a Plan-Net 1.2.0 server.
/// Anonymous callers read and search a published plan's directory; the tenant
/// comes from the published directory id only, a credential on the request
/// changes nothing, CHO-internal content never leaves, and the directory is
/// built from the owning services as fhir-service.
/// </summary>
public class PublicProviderDirectoryTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "tenant-acme";
    private const string Base = "/fhir/r4/directory/acme-health";
    private const string IntegrityExt = PublicDirectoryResponseFilter.ChoExtensionBase + "provider-integrity-score";
    private const string PanelExt = PublicDirectoryResponseFilter.ChoExtensionBase + "practitionerrole-panel-gating";

    private readonly FhirTestWebAppFactory _factory;

    public PublicProviderDirectoryTests(FhirTestWebAppFactory factory) => _factory = factory;

    private (WebApplicationFactory<Program> Host, Upstream Providers, Upstream Plans) Host(int permitPerMinute = 1000)
    {
        var providers = new Upstream();
        var plans = new Upstream();
        providers.Respond("/fhir/plannet-export/networks", HttpStatusCode.OK, NetworksBundle());
        providers.Respond("/fhir/plannet-export/providers", HttpStatusCode.OK, ProvidersBundle());
        plans.Respond("/fhir/InsurancePlan", HttpStatusCode.OK, PlansBundle());
        plans.Respond("/fhir/Endpoint", HttpStatusCode.OK, EndpointsBundle());

        var host = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("PublicProviderDirectory:Directories:acme-health", Tenant);
            b.UseSetting("PublicProviderDirectory:Payers:acme-health:Name", "Acme Health Plan");
            b.UseSetting("PublicProviderDirectory:Payers:acme-health:City", "Hartford");
            b.UseSetting("PublicProviderDirectory:Payers:acme-health:State", "CT");
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

    private static async Task<JsonObject> GetJsonAsync(WebApplicationFactory<Program> host, string path)
    {
        using var response = await GetAsync(host, path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static IEnumerable<string> EntryIds(JsonObject bundle, string mode = "match")
        => bundle["entry"]!.AsArray()
            .Where(e => e!["search"]!["mode"]!.GetValue<string>() == mode)
            .Select(e => $"{e!["resource"]!["resourceType"]}/{e["resource"]!["id"]}");

    // ── upstream bodies ───────────────────────────────────────────────────

    private static string NetworksBundle() => """
        {"resourceType":"Bundle","type":"collection","entry":[
          {"resource":{"resourceType":"Organization","id":"net-a","active":true,"name":"Acme Gold Network",
            "partOf":{"reference":"Organization/payer"},
            "type":[{"coding":[{"system":"http://hl7.org/fhir/us/davinci-pdex-plan-net/CodeSystem/OrgTypeCS","code":"ntwk"}]}]}}
        ]}
        """;

    private static string ProvidersBundle() => $$$"""
        {"resourceType":"Bundle","type":"collection","entry":[
          {"resource":{"resourceType":"Practitioner","id":"1234567893","active":true,
            "meta":{"lastUpdated":"2026-03-01T00:00:00Z"},
            "identifier":[{"system":"http://hl7.org/fhir/sid/us-npi","value":"1234567893"}],
            "name":[{"family":"Smith","given":["John"]}],
            "extension":[{"url":"{{{IntegrityExt}}}","extension":[{"url":"score","valueInteger":42}]}]}},
          {"resource":{"resourceType":"Practitioner","id":"1111111112","active":false,"name":[{"family":"Gone"}]}},
          {"resource":{"resourceType":"PractitionerRole","id":"r1","active":true,
            "practitioner":{"reference":"Practitioner/1234567893"},
            "extension":[
              {"url":"{{{PanelExt}}}","extension":[{"url":"panel-accepted","valueBoolean":true}]},
              {"url":"http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/network-reference","valueReference":{"reference":"Organization/net-a"}}],
            "location":[{"reference":"Location/1234567893-loc"}]}},
          {"resource":{"resourceType":"Location","id":"1234567893-loc","status":"active","name":"John Smith",
            "address":{"city":"Hartford","state":"CT"} } },
          {"resource":{"resourceType":"Organization","id":"1999999992","active":true,"name":"Hartford General Hospital",
            "identifier":[
              {"system":"http://hl7.org/fhir/sid/us-npi","value":"1999999992"},
              {"system":"{{{PublicDirectoryResponseFilter.EinSystem}}}","value":"12-3456789"}]}}
        ]}
        """;

    private static string PlansBundle() => """
        {"resourceType":"Bundle","type":"searchset","entry":[
          {"resource":{"resourceType":"InsurancePlan","id":"plan-1","status":"active","name":"Acme Gold HMO",
            "type":[{"coding":[
              {"system":"http://terminology.hl7.org/CodeSystem/insurance-plan-type","code":"medical"},
              {"system":"http://fhir.cloudhealthoffice.com/CodeSystem/plan-product-shape","code":"HMO"}],"text":"HMO"}],
            "ownedBy":{"display":"Acme"},
            "network":[{"reference":"Organization/net-a"}]}}
        ]}
        """;

    private static string EndpointsBundle() => """
        {"resourceType":"Bundle","type":"searchset","entry":[
          {"resource":{"resourceType":"Endpoint","id":"doc-1","status":"active","address":"https://acme.example/sbc.pdf",
            "connectionType":{"system":"http://fhir.cloudhealthoffice.com/CodeSystem/endpoint-connection-type","code":"static-document"},
            "payloadType":[{"coding":[{"system":"http://fhir.cloudhealthoffice.com/CodeSystem/plan-document-type","code":"sbc"}],"text":"SBC"}]}},
          {"resource":{"resourceType":"Endpoint","id":"doc-2","status":"off","address":"https://acme.example/old.pdf",
            "payloadType":[{"text":"old"}]}}
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

    // ── access ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymous_search_reads_the_published_plan_as_fhir_service()
    {
        var (host, providers, plans) = Host();
        using var _h = host;

        using var response = await GetAsync(host, $"{Base}/Practitioner?family=Smith");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        providers.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().BeEquivalentTo(
            "/fhir/plannet-export/networks", "/fhir/plannet-export/providers");
        plans.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().BeEquivalentTo(
            "/fhir/InsurancePlan", "/fhir/Endpoint");
        foreach (var sent in providers.Requests.Concat(plans.Requests))
            AssertServiceTokenFor(sent, Tenant);
        providers.Requests.Should().AllSatisfy(r => r.RequestUri!.Query.Should().Contain("payer=payer"));
    }

    [Fact]
    public async Task The_directory_is_built_once_and_reused()
    {
        var (host, providers, _) = Host();
        using var _h = host;

        (await GetAsync(host, $"{Base}/Practitioner")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(host, $"{Base}/PractitionerRole?network=Organization/net-a")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(host, $"{Base}/Organization/net-a")).StatusCode.Should().Be(HttpStatusCode.OK);

        providers.Requests.Should().HaveCount(2, "one networks call and one providers page build the snapshot");
    }

    [Fact]
    public async Task An_unpublished_directory_is_not_found_and_nothing_is_called()
    {
        var (host, providers, plans) = Host();
        using var _h = host;

        using var response = await GetAsync(host, "/fhir/r4/directory/other-plan/Practitioner");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        providers.Requests.Should().BeEmpty();
        plans.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cho_token_for_another_tenant_changes_nothing()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        var token = ChoDevelopmentAuth.UserToken("tenant-other", ChoRolePermissions.UMCoordinator);

        using var response = await GetAsync(host, $"{Base}/Practitioner", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertServiceTokenFor(providers.Requests[0], Tenant);
        providers.Requests.Should().AllSatisfy(r => r.Headers.Authorization!.Parameter.Should().NotBe(token));
    }

    [Fact]
    public async Task A_smart_token_is_never_forwarded_or_used()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        var token = _factory.IssueToken("patient/*.read", "member-1", "tenant-other");

        using var response = await GetAsync(host, $"{Base}/Practitioner", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertServiceTokenFor(providers.Requests[0], Tenant);
        providers.Requests.Should().AllSatisfy(r => r.Headers.Authorization!.Parameter.Should().NotBe(token));
    }

    [Fact]
    public async Task A_token_with_no_tenant_is_not_refused()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, $"{Base}/Practitioner",
            _factory.IssueToken("user/*.read", tenantId: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_authenticated_directory_still_requires_a_token()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, "/fhir/r4/Practitioner");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Callers_past_the_permit_are_throttled()
    {
        var (host, _, _) = Host(permitPerMinute: 2);
        using var _h = host;

        for (var i = 0; i < 2; i++)
            (await GetAsync(host, $"{Base}/Practitioner")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetAsync(host, $"{Base}/Practitioner")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // ── what the public sees ──────────────────────────────────────────────

    [Fact]
    public async Task Search_results_lose_internal_extensions_and_inactive_providers()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, $"{Base}/Practitioner");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("provider-integrity-score").And.NotContain("Gone");
        var bundle = JsonNode.Parse(body)!.AsObject();
        EntryIds(bundle).Should().Equal("Practitioner/1234567893");
        bundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Public_cho_extensions_survive()
    {
        var (host, _, _) = Host();
        using var _h = host;

        var role = await GetJsonAsync(host, $"{Base}/PractitionerRole/r1");

        role.ToJsonString().Should().Contain(PanelExt);
    }

    [Fact]
    public async Task Reading_an_inactive_provider_is_not_found()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, $"{Base}/Practitioner/1111111112");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_organization_loses_its_tax_id_but_keeps_its_npi()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, $"{Base}/Organization/1999999992");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("1999999992").And.NotContain("12-3456789");
    }

    [Fact]
    public async Task The_plan_itself_is_published_as_the_payer_and_owns_its_products()
    {
        var (host, _, _) = Host();
        using var _h = host;

        var payer = await GetJsonAsync(host, $"{Base}/Organization/payer");
        var plan = await GetJsonAsync(host, $"{Base}/InsurancePlan/plan-1");

        payer["name"]!.GetValue<string>().Should().Be("Acme Health Plan");
        payer["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("payer");
        payer["address"]![0]!["city"]!.GetValue<string>().Should().Be("Hartford");
        plan["ownedBy"]!["reference"]!.GetValue<string>().Should().Be("Organization/payer");
        plan["administeredBy"]!["reference"]!.GetValue<string>().Should().Be("Organization/payer");
        plan["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("commhmo");
        plan["meta"]!["profile"]![0]!.GetValue<string>().Should().Be(PlanNetCanonicals.InsurancePlanProfile);
    }

    [Fact]
    public async Task Only_active_endpoints_are_published_in_plan_net_shape()
    {
        var (host, _, _) = Host();
        using var _h = host;

        var bundle = await GetJsonAsync(host, $"{Base}/Endpoint");

        EntryIds(bundle).Should().Equal("Endpoint/doc-1");
        var endpoint = bundle["entry"]![0]!["resource"]!;
        endpoint["connectionType"]!["code"]!.GetValue<string>().Should().Be("rest-non-fhir");
        endpoint["payloadType"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("NA");
        endpoint["payloadType"]![0]!["coding"]![1]!["code"]!.GetValue<string>().Should().Be("sbc");
        endpoint["managingOrganization"]!["reference"]!.GetValue<string>().Should().Be("Organization/payer");
    }

    [Fact]
    public async Task Searches_chain_and_include_across_the_directory()
    {
        var (host, _, _) = Host();
        using var _h = host;

        var bundle = await GetJsonAsync(host,
            $"{Base}/PractitionerRole?location.address-city=Hartford&_include=PractitionerRole:practitioner&_include=PractitionerRole:network");

        EntryIds(bundle).Should().Equal("PractitionerRole/r1");
        EntryIds(bundle, "include").Should().BeEquivalentTo("Practitioner/1234567893", "Organization/net-a");
        bundle["entry"]![0]!["fullUrl"]!.GetValue<string>().Should().EndWith($"{Base}/PractitionerRole/r1");
    }

    [Fact]
    public async Task Paging_links_carry_the_search_forward()
    {
        var (host, _, _) = Host();
        using var _h = host;

        var first = await GetJsonAsync(host, $"{Base}/Organization?_count=1");
        var next = first["link"]!.AsArray().Single(l => l!["relation"]!.GetValue<string>() == "next")!["url"]!.GetValue<string>();
        var second = await GetJsonAsync(host, new Uri(next).PathAndQuery);

        first["total"]!.GetValue<int>().Should().Be(3);
        EntryIds(first).Should().ContainSingle();
        EntryIds(second).Should().ContainSingle().And.NotEqual(EntryIds(first));
        second["link"]!.AsArray().Should().Contain(l => l!["relation"]!.GetValue<string>() == "previous");
    }

    [Fact]
    public async Task Unknown_parameters_are_reported_in_the_bundle()
    {
        var (host, _, _) = Host();
        using var _h = host;

        var bundle = await GetJsonAsync(host, $"{Base}/Practitioner?near=1|2|3|mi");

        var outcome = bundle["entry"]!.AsArray()
            .Single(e => e!["search"]!["mode"]!.GetValue<string>() == "outcome");
        outcome!["resource"]!["issue"]![0]!["diagnostics"]!.GetValue<string>().Should().Contain("near");
    }

    [Fact]
    public async Task A_malformed_search_is_a_bad_request()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, $"{Base}/PractitionerRole?_lastUpdated=yesterday");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_type_the_directory_does_not_serve_is_not_found()
    {
        var (host, providers, _) = Host();
        using var _h = host;

        using var response = await GetAsync(host, $"{Base}/Patient");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        providers.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Vread_serves_the_current_version_and_read_names_it()
    {
        var (host, _, _) = Host();
        using var _h = host;

        using var read = await GetAsync(host, $"{Base}/Location/1234567893-loc");
        var version = JsonNode.Parse(await read.Content.ReadAsStringAsync())!["meta"]!["versionId"]!.GetValue<string>();

        read.Headers.ETag!.Tag.Should().Be($"\"{version}\"");
        (await GetAsync(host, $"{Base}/Location/1234567893-loc/_history/{version}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(host, $"{Base}/Location/1234567893-loc/_history/0")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upstream_failure_is_a_bad_gateway_without_its_body()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond("/fhir/plannet-export/networks", HttpStatusCode.InternalServerError, "internal detail that must not leak");

        using var response = await GetAsync(host, $"{Base}/Practitioner");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("internal detail").And.NotContain("500");
    }

    [Fact]
    public async Task Provider_pages_are_followed_to_the_end()
    {
        var (host, providers, _) = Host();
        using var _h = host;
        providers.Respond("/fhir/plannet-export/providers", HttpStatusCode.OK, request =>
            request.RequestUri!.Query.Contains("skip=0")
                ? """{"resourceType":"Bundle","type":"collection","link":[{"relation":"next","url":"providers?payer=payer&skip=500&count=500"}],"entry":[{"resource":{"resourceType":"Practitioner","id":"p1","active":true}}]}"""
                : """{"resourceType":"Bundle","type":"collection","entry":[{"resource":{"resourceType":"Practitioner","id":"p2","active":true}}]}""");

        var bundle = await GetJsonAsync(host, $"{Base}/Practitioner");

        EntryIds(bundle).Should().Equal("Practitioner/p1", "Practitioner/p2");
        providers.Requests.Select(r => r.RequestUri!.Query).Should().Contain(q => q.Contains("skip=500"));
    }

    // ── conformance declaration ───────────────────────────────────────────

    [Fact]
    public async Task Metadata_declares_a_plan_net_server_with_every_shall_parameter()
    {
        var (host, providers, _) = Host();
        using var _h = host;

        var capability = await GetJsonAsync(host, $"{Base}/metadata");

        capability["instantiates"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal(PlanNetCanonicals.CapabilityStatement);
        capability["implementationGuide"]![0]!.GetValue<string>().Should().EndWith("|1.2.0");
        capability["fhirVersion"]!.GetValue<string>().Should().Be("4.0.1");
        var resources = capability["rest"]![0]!["resource"]!.AsArray()
            .ToDictionary(r => r!["type"]!.GetValue<string>(), r => r!.AsObject());

        foreach (var (type, required) in PlanNetShallSearchParameters)
        {
            resources.Should().ContainKey(type);
            var declared = resources[type]["searchParam"]!.AsArray().Select(p => p!["name"]!.GetValue<string>());
            declared.Should().Contain(required, $"Plan-Net 1.2.0 requires them on {type}");
            resources[type]["interaction"]!.AsArray().Select(i => i!["code"]!.GetValue<string>())
                .Should().Contain(["read", "vread", "search-type"]);
        }
        resources["Organization"]["supportedProfile"]!.AsArray().Select(p => p!.GetValue<string>())
            .Should().BeEquivalentTo(PlanNetCanonicals.NetworkProfile, PlanNetCanonicals.OrganizationProfile);
        providers.Requests.Should().BeEmpty("the CapabilityStatement needs no directory data");
    }

    /// <summary>The SHALL search parameters of the Plan-Net 1.2.0 CapabilityStatement (CapabilityStatement-plan-net).</summary>
    private static readonly Dictionary<string, string[]> PlanNetShallSearchParameters = new()
    {
        ["Endpoint"] = ["organization", "_id", "_lastUpdated"],
        ["HealthcareService"] = ["location", "coverage-area", "organization", "endpoint", "name", "service-category", "service-type", "specialty", "_id", "_lastUpdated"],
        ["InsurancePlan"] = ["administered-by", "owned-by", "coverage-area", "name", "plan-type", "identifier", "_id", "_lastUpdated", "type"],
        ["Location"] = ["partof", "organization", "endpoint", "address-city", "address-state", "address-postalcode", "address", "type", "_id", "_lastUpdated"],
        ["Organization"] = ["partof", "endpoint", "address", "name", "_id", "_lastUpdated", "type", "coverage-area"],
        ["OrganizationAffiliation"] = ["primary-organization", "participating-organization", "location", "service", "network", "endpoint", "role", "specialty", "_id", "_lastUpdated"],
        ["Practitioner"] = ["name", "_id", "_lastUpdated", "family", "given"],
        ["PractitionerRole"] = ["practitioner", "organization", "location", "service", "network", "endpoint", "role", "specialty", "_id", "_lastUpdated"],
    };

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
    public void Public_cho_extensions_are_kept_by_the_filter()
    {
        var filtered = PublicDirectoryResponseFilter.Filter($$$"""
            {"resourceType":"PractitionerRole","id":"r1","extension":[
              {"url":"{{{PanelExt}}}","extension":[{"url":"accepting-new-patients","valueBoolean":true}]},
              {"url":"{{{IntegrityExt}}}"}]}
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
        private readonly Dictionary<string, (HttpStatusCode Status, Func<HttpRequestMessage, string> Body)> _responses = new();

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public void Respond(string path, HttpStatusCode status, string body) => _responses[path] = (status, _ => body);

        public void Respond(string path, HttpStatusCode status, Func<HttpRequestMessage, string> body) => _responses[path] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            var (status, body) = _responses.TryGetValue(request.RequestUri!.AbsolutePath, out var response)
                ? response
                : (HttpStatusCode.NotFound, _ => "{}");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body(request), Encoding.UTF8, "application/fhir+json"),
            });
        }
    }
}
