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
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        public static string Member(string id, string family, string dob, string? subscriber = null) =>
            "{\"memberId\":\"" + id + "\"," + (subscriber is null ? "" : "\"subscriberMemberId\":\"" + subscriber + "\",") +
            "\"resource\":{\"resourceType\":\"Patient\",\"id\":\"" + id + "\",\"name\":[{\"family\":\"" + family + "\",\"given\":[\"Pat\"]}],\"gender\":\"female\",\"birthDate\":\"" + dob + "\",\"address\":[{\"postalCode\":\"75201\"}]}}";

        public static string Coverage(string id, string member, string start) =>
            "{\"resource\":{\"resourceType\":\"Coverage\",\"id\":\"" + id + "\",\"status\":\"active\",\"beneficiary\":{\"reference\":\"Patient/" + member + "\"},\"period\":{\"start\":\"" + start + "\"},\"payor\":[{\"display\":\"Plan\"}]}}";

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
            .Should().Equal("Patient/pat-002", "Coverage/cov-2", "ExplanationOfBenefit/eob-new");
        resources[0].GetProperty("birthDate").GetString().Should().Be("1962-03-22");
        resources[0].GetProperty("name")[0].GetProperty("family").GetString().Should().Be("Jones");
        resources[1].GetProperty("beneficiary").GetProperty("reference").GetString().Should().Be("Patient/pat-002");
        resources[2].GetProperty("outcome").GetString().Should().Be("complete");
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
