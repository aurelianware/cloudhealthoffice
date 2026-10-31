using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.FhirService.Tests.Security;
using FhirService.Controllers;
using FhirService.Services.ChoNative;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests.ChoNative;

/// <summary>
/// Payer-to-Payer bound to CHO's own services (ADR 020), through the real
/// pipeline: the export sends member-service's Patient, coverage-service's
/// Coverage and claims-service's EOBs as they project them, member match
/// finds candidates by member id, subscriber and birth date, an SSN-only
/// request is declined, upstream calls carry the tenant and fhir-service's
/// own credential (never the caller's token), and upstream failure is FHIR 502.
/// </summary>
public class ChoNativePayerToPayerEndpointTests : IClassFixture<ChoNativePayerToPayerEndpointTests.Host>
{
    private const string ExportPath = "/fhir/r4/PayerToPayer/$member-data-export";
    private const string MatchPath = "/fhir/r4/Patient/$member-match";
    private const string Payer = "PAYER-A";

    public sealed class Upstream : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<(string PathAndQuery, string? Tenant, string? Authorization)> Calls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add((Uri.UnescapeDataString(request.RequestUri!.PathAndQuery),
                    request.Headers.TryGetValues("X-Tenant-ID", out var t) ? t.Single() : null,
                    request.Headers.Authorization?.ToString()));
            }
            return Task.FromResult(EchoAnyForm(request, Override?.Invoke(request) ?? Default(request)));
        }

        // A current member-service echoes an any-form request (ADR 020).
        private static HttpResponseMessage EchoAnyForm(HttpRequestMessage request, HttpResponseMessage response)
        {
            if (!request.RequestUri!.Query.Contains("anyIdentifierForm=true") || !response.IsSuccessStatusCode) return response;
            if (System.Text.Json.Nodes.JsonNode.Parse(response.Content.ReadAsStringAsync().Result) is not System.Text.Json.Nodes.JsonObject page)
                return response;
            page["anyIdentifierForm"] = true;
            response.Content = new StringContent(page.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            return response;
        }

        private static HttpResponseMessage Default(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);
            return uri.AbsolutePath switch
            {
                "/api/v1/members/fhir/Patient" when query.Contains("memberId=pat-002") =>
                    Json(Page(Member("pat-002", "Jones", "1962-03-22"))),
                "/api/v1/members/fhir/Patient" when query.Contains("memberId=") => Json(Page()),
                "/api/v1/members/fhir/Patient" when query.Contains("subscriberMemberId=SUB-9") =>
                    Json(Page(Member("dep-1", "Kim", "2012-06-01", subscriber: "SUB-9"))),
                "/api/v1/members/fhir/Patient" when query.Contains("subscriberMemberId=") => Json(Page()),
                "/api/v1/members/fhir/Patient" when query.Contains("birthDateFrom=1962-03-22&birthDateTo=1962-03-23") =>
                    Json(Page(Member("pat-002", "Jones", "1962-03-22"), Member("pat-777", "Rivera", "1962-03-22"))),
                "/api/v1/members/fhir/Patient" => Json(Page()),
                "/api/v1/coverage/fhir/Coverage" when query.Contains("memberId=pat-002") =>
                    Json(Page(Coverage("cov-2", "pat-002", "2024-01-01"))),
                "/api/v1/coverage/fhir/Coverage" when query.Contains("memberId=dep-1") =>
                    Json(Page(Coverage("cov-9", "dep-1", "2024-01-01"))),
                "/api/v1/coverage/fhir/Coverage" => Json(Page()),
                "/fhir/ExplanationOfBenefit" when query.Contains("patient=pat-002") =>
                    Json(Bundle(Eob("eob-new", "pat-002", "2026-01-15"), Eob("eob-old", "pat-002", "2015-01-15"))),
                "/fhir/ExplanationOfBenefit" => Json(Bundle()),
                // authorization-service's prior-auth projection (G-31): the
                // second page is empty, which is how the directory learns the
                // first was the last.
                "/fhir/ExplanationOfBenefit/prior-authorization" when query.Contains("patient=pat-002") && query.Contains("_page=1") =>
                    Json(Bundle(PriorAuth("pa-PA-1", "pat-002", "2026-02-01"))),
                "/fhir/ExplanationOfBenefit/prior-authorization" => Json(Bundle()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        public static string Member(string id, string family, string dob, string? subscriber = null) =>
            "{\"memberId\":\"" + id + "\"," + (subscriber is null ? "" : "\"subscriberMemberId\":\"" + subscriber + "\",") +
            "\"resource\":{\"resourceType\":\"Patient\",\"id\":\"" + id + "\",\"name\":[{\"family\":\"" + family + "\",\"given\":[\"Pat\"]}],\"gender\":\"female\",\"birthDate\":\"" + dob + "\",\"address\":[{\"postalCode\":\"75201\"}]}}";

        public static string Coverage(string id, string member, string start) =>
            "{\"resource\":{\"resourceType\":\"Coverage\",\"id\":\"" + id + "\",\"status\":\"active\",\"beneficiary\":{\"reference\":\"Patient/" + member + "\"},\"period\":{\"start\":\"" + start + "\"},\"payor\":[{\"display\":\"Plan\"}]}}";

        public static string PriorAuth(string id, string member, string start) =>
            "{\"resource\":{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"" + id + "\",\"status\":\"active\",\"use\":\"preauthorization\",\"patient\":{\"reference\":\"Patient/" + member + "\"},\"benefitPeriod\":{\"start\":\"" + start + "\"},\"outcome\":\"complete\",\"disposition\":\"Approved\",\"payment\":{\"amount\":{\"value\":100}}}}";

        public static string Eob(string id, string member, string start) =>
            "{\"resource\":{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"" + id + "\",\"status\":\"active\",\"patient\":{\"reference\":\"Patient/" + member + "\"},\"billablePeriod\":{\"start\":\"" + start + "\"},\"outcome\":\"complete\"}}";

        private static string Page(params string[] items) =>
            "{\"total\":" + items.Length + ",\"items\":[" + string.Join(",", items) + "]}";

        private static string Bundle(params string[] entries) =>
            "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":" + entries.Length + ",\"entry\":[" + string.Join(",", entries) + "]}";

        public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public sealed class Host : PayerToPayerWebAppFactory
    {
        public Upstream Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FhirAdapters:Sources:PayerToPayer", "ChoNative");
            builder.UseSetting("Services:MemberServiceUrl", "http://member-service/api");
            builder.UseSetting("Services:CoverageServiceUrl", "http://coverage-service/api");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(ChoNativeFhirDataAdapter.MemberServiceClient).ConfigurePrimaryHttpMessageHandler(() => Upstream);
                services.AddHttpClient(ChoNativeFhirDataAdapter.CoverageServiceClient).ConfigurePrimaryHttpMessageHandler(() => Upstream);
                services.AddHttpClient(ExplanationOfBenefitController.ClaimsServiceClientName).ConfigurePrimaryHttpMessageHandler(() => Upstream);
                services.AddHttpClient(global::FhirService.Services.UpstreamClientNames.AuthorizationService).ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
        }
    }

    private readonly Host _host;

    public ChoNativePayerToPayerEndpointTests(Host host)
    {
        _host = host;
        lock (host.Upstream) host.Upstream.Calls.Clear();
        host.Upstream.Override = null;
    }

    private string Token(string clientId) => _host.IssueToken("system/PayerToPayer.read", patientId: null,
        extraClaims: [new SecurityClaim("client_id", clientId), new SecurityClaim("payer_id", Payer)]);

    private async Task<HttpResponseMessage> Post(string path, object body, string clientId = "native-p2p")
    {
        var token = Token(clientId);
        using var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(CloudHealthOffice.FhirService.Tests.PayerToPayer.P2PTestConsent.Serialize(body, token), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        _lastToken = token;
        return response;
    }

    private string? _lastToken;

    private static async Task<List<JsonElement>> Resources(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource").Clone()).ToList();
    }

    // ── Export (P2P-01) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Export_sends_the_members_records_as_CHO_services_project_them()
    {
        var response = await Post(ExportPath, new { memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var resources = await Resources(response);
        resources.Select(r => r.GetProperty("resourceType").GetString() + "/" + r.GetProperty("id").GetString())
            .Should().Equal("Patient/pat-002", "Coverage/cov-2", "ExplanationOfBenefit/eob-new",
                "ExplanationOfBenefit/pa-PA-1");
        resources[0].GetProperty("birthDate").GetString().Should().Be("1962-03-22");
        resources[0].GetProperty("name")[0].GetProperty("family").GetString().Should().Be("Jones");
        resources[1].GetProperty("beneficiary").GetProperty("reference").GetString().Should().Be("Patient/pat-002");
        resources[2].GetProperty("outcome").GetString().Should().Be("complete");
    }

    [Fact]
    public async Task Export_sends_the_members_prior_authorizations_as_authorization_service_projects_them()
    {
        // CMS-0057-F's Payer-to-Payer PA data (G-35), end to end: the receiving
        // payer gets the decision and the approved period so it can honour what
        // is already authorized, and nothing monetary goes with it.
        var response = await Post(ExportPath, new { memberId = "pat-002" });

        var priorAuth = (await Resources(response))
            .Single(r => r.TryGetProperty("use", out var use) && use.GetString() == "preauthorization");

        priorAuth.GetProperty("id").GetString().Should().Be("pa-PA-1");
        priorAuth.GetProperty("outcome").GetString().Should().Be("complete");
        priorAuth.GetProperty("disposition").GetString().Should().Be("Approved");
        priorAuth.GetProperty("benefitPeriod").GetProperty("start").GetString().Should().Be("2026-02-01");
        priorAuth.TryGetProperty("payment", out _).Should().BeFalse("the PA allowlist carries no money");
    }

    [Fact]
    public async Task Export_reads_authorization_service_for_the_members_prior_authorizations()
    {
        await Post(ExportPath, new { memberId = "pat-002" });

        List<(string PathAndQuery, string? Tenant, string? Authorization)> calls;
        lock (_host.Upstream) calls = _host.Upstream.Calls.ToList();
        calls.Should().Contain(c =>
            c.PathAndQuery.StartsWith("/fhir/ExplanationOfBenefit/prior-authorization?patient=pat-002"));
    }

    [Fact]
    public async Task Export_answers_502_when_the_prior_authorization_read_fails_and_sends_nothing()
    {
        // A partial record set is worse than none: a receiving payer that is
        // told nothing is pending would make the member start again.
        _host.Upstream.Override = r =>
            r.RequestUri!.AbsolutePath == "/fhir/ExplanationOfBenefit/prior-authorization"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : null;

        var response = await Post(ExportPath, new { memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Jones");
    }

    [Fact]
    public async Task Export_reads_upstream_in_the_tenant_with_fhir_services_own_credential()
    {
        await Post(ExportPath, new { memberId = "pat-002" });

        List<(string PathAndQuery, string? Tenant, string? Authorization)> calls;
        lock (_host.Upstream) calls = _host.Upstream.Calls.ToList();
        calls.Should().NotBeEmpty();
        calls.Should().OnlyContain(c => c.Tenant == "test-tenant");
        calls.Should().OnlyContain(c => c.Authorization != "Bearer " + _lastToken, "the caller's token never leaves fhir-service");
        calls.Should().Contain(c => c.PathAndQuery.StartsWith("/fhir/ExplanationOfBenefit?patient=pat-002"));
    }

    [Fact]
    public async Task Export_answers_502_when_a_CHO_service_fails_and_sends_nothing()
    {
        _host.Upstream.Override = r => r.RequestUri!.AbsolutePath == "/fhir/ExplanationOfBenefit"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : null;

        var response = await Post(ExportPath, new { memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Jones");
    }

    [Fact]
    public async Task Export_refuses_claims_that_belong_to_another_member()
    {
        _host.Upstream.Override = r => r.RequestUri!.AbsolutePath == "/fhir/ExplanationOfBenefit"
            ? Upstream.Json("{\"resourceType\":\"Bundle\",\"total\":1,\"entry\":[" + Upstream.Eob("x", "pat-777", "2026-01-01") + "]}")
            : null;

        var response = await Post(ExportPath, new { memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // ── Member match (P2P-04) ─────────────────────────────────────────────────

    [Fact]
    public async Task Match_by_family_and_birth_date_reads_only_that_birth_date()
    {
        var response = await Post(MatchPath, new { familyName = "Jones", birthDate = "1962-03-22" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("pat-002");
        lock (_host.Upstream)
            _host.Upstream.Calls.Should().Contain(c => c.PathAndQuery.Contains("birthDateFrom=1962-03-22&birthDateTo=1962-03-23"));
    }

    [Fact]
    public async Task Match_by_a_subscriber_id_finds_the_dependent_carrying_it()
    {
        var response = await Post(MatchPath, new { memberId = "SUB-9", birthDate = "2012-06-01", familyName = "Kim" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("dep-1");
    }

    [Fact]
    public async Task Match_on_SSN_alone_is_declined_as_insufficient_and_nothing_is_read()
    {
        var response = await Post(MatchPath, new { ssn = "123-45-6789", familyName = "Jones" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Insufficient");
        lock (_host.Upstream) _host.Upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Match_with_a_contradicting_candidate_and_one_agreeing_returns_the_agreeing_member()
    {
        // Both pat-002 and pat-777 were born 1962-03-22; only pat-002 is a Jones.
        var response = await Post(MatchPath, new { familyName = "Jones", birthDate = "1962-03-22", postalCode = "75201" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("pat-002").And.NotContain("pat-777");
    }

    // ── Member match, HRex form (PDex $member-match) ─────────────────────────

    private static System.Text.Json.Nodes.JsonObject HrexMatch(
        string family, string birthDate, bool withConsent = true, bool withCoverage = true,
        bool consentNamesMemberPatient = false)
    {
        var parameters = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "MemberPatient",
                ["resource"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["resourceType"] = "Patient",
                    ["id"] = "1",
                    ["name"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["use"] = "official",
                            ["family"] = family,
                            ["given"] = new System.Text.Json.Nodes.JsonArray { "Pat" },
                        },
                    },
                    ["birthDate"] = birthDate,
                },
            },
        };
        if (withCoverage)
        {
            parameters.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "CoverageToMatch",
                ["resource"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["resourceType"] = "Coverage",
                    ["id"] = "9876B1",
                    ["status"] = "active",
                    ["beneficiary"] = new System.Text.Json.Nodes.JsonObject { ["reference"] = "Patient/1" },
                    ["payor"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["display"] = "Old Health Plan" },
                    },
                },
            });
        }
        if (withConsent)
        {
            parameters.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "Consent",
                ["resource"] = consentNamesMemberPatient
                    ? NamingMemberPatient(CloudHealthOffice.FhirService.Tests.PayerToPayer.P2PTestConsent.For(Payer, memberId: null))
                    : CloudHealthOffice.FhirService.Tests.PayerToPayer.P2PTestConsent.For(
                        Payer, memberId: null, family, birthDate),
            });
        }
        return new System.Text.Json.Nodes.JsonObject { ["resourceType"] = "Parameters", ["parameter"] = parameters };
    }

    // As HRex sends it: the consent's patient is the MemberPatient parameter, by local reference.
    private static System.Text.Json.Nodes.JsonObject NamingMemberPatient(System.Text.Json.Nodes.JsonObject consent)
    {
        consent.Remove("contained");
        consent["patient"] = new System.Text.Json.Nodes.JsonObject { ["reference"] = "Patient/1" };
        return consent;
    }

    private async Task<HttpResponseMessage> PostFhir(string path, System.Text.Json.Nodes.JsonObject body)
    {
        var token = Token("native-p2p");
        using var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/fhir+json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Hrex_member_match_answers_with_the_member_identifier_parameters()
    {
        var response = await PostFhir(MatchPath, HrexMatch("Jones", "1962-03-22"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("resourceType").GetString().Should().Be("Parameters");
        var parameter = root.GetProperty("parameter").EnumerateArray().Should().ContainSingle().Subject;
        parameter.GetProperty("name").GetString().Should().Be("MemberIdentifier");
        var identifier = parameter.GetProperty("valueIdentifier");
        identifier.GetProperty("value").GetString().Should().Be("pat-002");
        identifier.GetProperty("system").GetString().Should().Be(ChoNativeFhirDataAdapter.MemberIdSystem);
        var coding = identifier.GetProperty("type").GetProperty("coding")[0];
        coding.GetProperty("system").GetString().Should().Be("http://terminology.hl7.org/CodeSystem/v2-0203");
        coding.GetProperty("code").GetString().Should().Be("MB");
    }

    [Fact]
    public async Task Hrex_member_match_accepts_a_consent_naming_the_member_patient_parameter()
    {
        var response = await PostFhir(MatchPath, HrexMatch("Jones", "1962-03-22", consentNamesMemberPatient: true));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("pat-002");
    }

    [Fact]
    public async Task Hrex_member_match_with_a_consent_naming_someone_else_is_422()
    {
        var body = HrexMatch("Jones", "1962-03-22", consentNamesMemberPatient: true);
        var consent = body["parameter"]!.AsArray().Single(p => (string?)p!["name"] == "Consent")!["resource"]!;
        consent["patient"] = new System.Text.Json.Nodes.JsonObject { ["reference"] = "Patient/someone-else" };

        var response = await PostFhir(MatchPath, body);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Hrex_member_match_without_a_consent_is_422_and_nothing_is_read()
    {
        var response = await PostFhir(MatchPath, HrexMatch("Jones", "1962-03-22", withConsent: false));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        lock (_host.Upstream) _host.Upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Hrex_member_match_with_no_matching_member_is_422()
    {
        var response = await PostFhir(MatchPath, HrexMatch("Nobody", "1990-01-01"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Hrex_member_match_with_two_candidates_is_422()
    {
        // pat-002 and pat-777 share the birth date; a name neither carries
        // leaves both standing, which HRex answers with 422.
        var response = await PostFhir(MatchPath, HrexMatch("Smith", "1962-03-22"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Hrex_member_match_without_a_coverage_to_match_is_400()
    {
        var response = await PostFhir(MatchPath, HrexMatch("Jones", "1962-03-22", withCoverage: false));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("CoverageToMatch");
        lock (_host.Upstream) _host.Upstream.Calls.Should().BeEmpty();
    }

    private sealed class MissingCoverageUrlHost : FhirTestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FhirAdapters:Sources:PayerToPayer", "ChoNative");
            builder.UseSetting("Services:MemberServiceUrl", "http://member-service/api");
            builder.UseSetting("Services:CoverageServiceUrl", "");
        }
    }

    [Fact]
    public void Payer_to_payer_on_cho_native_without_a_service_url_does_not_start()
    {
        using var host = new MissingCoverageUrlHost();

        var act = () => host.CreateClient();

        act.Should().Throw<Exception>().Where(e => Flatten(e).Contains("PayerToPayer=ChoNative needs Services:CoverageServiceUrl"));
    }

    private static string Flatten(Exception e) => e.InnerException is null ? e.Message : e.Message + " | " + Flatten(e.InnerException);
}
