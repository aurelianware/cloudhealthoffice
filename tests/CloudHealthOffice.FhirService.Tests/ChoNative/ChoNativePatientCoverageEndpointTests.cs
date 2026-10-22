using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using FhirService.Services.ChoNative;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.ChoNative;

/// <summary>
/// fhir-service with Patient and Coverage bound to CHO's own services (ADR 018),
/// through the real pipeline: SMART scopes and patient binding still decide what
/// a caller may see, the SMART token never leaves fhir-service, upstream failures
/// and unsupported searches answer as FHIR, and adapter-status reports the binding.
/// </summary>
public class ChoNativePatientCoverageEndpointTests : IClassFixture<ChoNativePatientCoverageEndpointTests.Host>
{
    public sealed class Upstream : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<(string PathAndQuery, string? Tenant, string? Authorization)> Calls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Override { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add((
                    Uri.UnescapeDataString(request.RequestUri!.PathAndQuery),
                    request.Headers.TryGetValues("X-Tenant-ID", out var t) ? t.Single() : null,
                    request.Headers.Authorization?.ToString()));
            }
            return Task.FromResult(Override?.Invoke(request) ?? Default(request));
        }

        private static HttpResponseMessage Default(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/v1/members/pat-001/fhir" => Json(PatientJson("pat-001", "Smith")),
                "/api/v1/members/pat-002/fhir" => Json(PatientJson("pat-002", "Jones")),
                "/api/v1/members/fhir/Patient" => Json(Page(1, PatientJson("pat-001", "Smith"))),
                "/api/v1/coverage/cov-001/fhir" => Json(CoverageJson("cov-001", "pat-001")),
                "/api/v1/coverage/cov-002/fhir" => Json(CoverageJson("cov-002", "pat-002")),
                "/api/v1/coverage/fhir/Coverage" => Json(Page(1, CoverageJson("cov-001", "pat-001"))),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        public static string PatientJson(string id, string family) =>
            $$"""{"resourceType":"Patient","id":"{{id}}","name":[{"family":"{{family}}","given":["Pat"]}],"gender":"female"}""";

        public static string CoverageJson(string id, string member) =>
            $$"""{"resourceType":"Coverage","id":"{{id}}","status":"active","beneficiary":{"reference":"Patient/{{member}}"},"payor":[{"display":"Plan"}]}""";

        private static string Page(long total, params string[] resources) =>
            "{\"total\":" + total + ",\"items\":[" + string.Join(",", resources.Select(r => "{\"resource\":" + r + "}")) + "]}";

        public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public sealed class Host : FhirTestWebAppFactory
    {
        public Upstream Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Host settings: Program.cs reads the bindings while building.
            builder.UseSetting("FhirAdapters:Sources:Patient", "ChoNative");
            builder.UseSetting("FhirAdapters:Sources:Coverage", "ChoNative");
            builder.UseSetting("Services:MemberServiceUrl", "http://member-service/api");
            builder.UseSetting("Services:CoverageServiceUrl", "http://coverage-service/api");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(ChoNativeFhirDataAdapter.MemberServiceClient)
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream);
                services.AddHttpClient(ChoNativeFhirDataAdapter.CoverageServiceClient)
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
        }
    }

    private readonly Host _host;

    public ChoNativePatientCoverageEndpointTests(Host host)
    {
        _host = host;
        lock (host.Upstream) host.Upstream.Calls.Clear();
        host.Upstream.Override = null;
    }

    private async Task<HttpResponseMessage> Get(string path, string token)
    {
        var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/fhir+json");
        return await client.SendAsync(request);
    }

    private string PatientToken(string patient) => _host.IssueToken("patient/*.read", patient);

    private string SystemToken() => _host.IssueToken("system/*.read");

    private static async Task<JsonObject> Body(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    [Fact]
    public async Task A_patient_reads_their_record_from_member_service_and_the_smart_token_stays_inside()
    {
        var token = PatientToken("pat-001");

        var response = await Get("/fhir/r4/Patient/pat-001", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(response))["name"]![0]!["family"]!.ToString().Should().Be("Smith");
        var call = _host.Upstream.Calls.Should().ContainSingle().Subject;
        call.PathAndQuery.Should().Be("/api/v1/members/pat-001/fhir");
        call.Tenant.Should().Be("test-tenant");
        call.Authorization.Should().StartWith("Bearer ", "the call carries fhir-service's own service token");
        call.Authorization.Should().NotContain(token, "a SMART token is never forwarded to CHO services");
    }

    [Fact]
    public async Task A_patient_cannot_read_another_patients_coverage()
    {
        var response = await Get("/fhir/r4/Coverage/cov-002", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_patients_coverage_search_is_pinned_to_their_own_member_id()
    {
        var response = await Get("/fhir/r4/Coverage?status=active", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(response))["total"]!.GetValue<int>().Should().Be(1);
        _host.Upstream.Calls.Should().ContainSingle()
            .Which.PathAndQuery.Should().StartWith("/api/v1/coverage/fhir/Coverage?memberId=pat-001&status=active");
    }

    [Fact]
    public async Task A_patients_patient_search_is_pinned_to_their_own_member_id()
    {
        var response = await Get("/fhir/r4/Patient?family=Jo", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _host.Upstream.Calls.Should().ContainSingle()
            .Which.PathAndQuery.Should().StartWith("/api/v1/members/fhir/Patient?memberId=pat-001&family=Jo");
    }

    [Fact]
    public async Task A_search_bundle_carries_the_upstream_total()
    {
        var response = await Get("/fhir/r4/Patient?birthdate=ge1980&birthdate=lt1990", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = await Body(response);
        bundle["resourceType"]!.ToString().Should().Be("Bundle");
        bundle["total"]!.GetValue<int>().Should().Be(1);
        _host.Upstream.Calls.Should().ContainSingle()
            .Which.PathAndQuery.Should().Contain("birthDateFrom=1980-01-01&birthDateTo=1990-01-01");
    }

    [Fact]
    public async Task An_upstream_outage_answers_502_with_an_operation_outcome()
    {
        _host.Upstream.Override = _ => Upstream.Json("{\"detail\":\"stack trace\"}", HttpStatusCode.ServiceUnavailable);

        var response = await Get("/fhir/r4/Patient/pat-001", SystemToken());

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var outcome = await Body(response);
        outcome["resourceType"]!.ToString().Should().Be("OperationOutcome");
        outcome["issue"]![0]!["code"]!.ToString().Should().Be("transient");
        outcome.ToJsonString().Should().NotContain("stack trace");
    }

    [Fact]
    public async Task An_unsupported_search_answers_400_and_never_reaches_the_service()
    {
        var response = await Get("/fhir/r4/Patient?identifier=http://hl7.org/fhir/sid/us-mbi|1EG4", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var outcome = await Body(response);
        outcome["issue"]![0]!["code"]!.ToString().Should().Be("not-supported");
        _host.Upstream.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/fhir/r4/Patient?_id=pat-002")]
    [InlineData("/fhir/r4/Patient?identifier=urn:cho:member-id|pat-002")]
    public async Task A_patient_searching_for_someone_else_gets_an_empty_bundle_without_any_upstream_call(string path)
    {
        var response = await Get(path, PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(response))["total"]!.GetValue<int>().Should().Be(0);
        _host.Upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_patient_naming_another_beneficiary_is_refused()
    {
        var response = await Get("/fhir/r4/Coverage?beneficiary=Patient/pat-002", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _host.Upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_upstream_that_ignores_the_member_filter_is_withheld_not_returned()
    {
        // Defence in depth: if a deployed member-service ever stopped applying
        // memberId (renamed parameter, version skew), a patient-bound search
        // must not hand back other members.
        _host.Upstream.Override = r => r.RequestUri!.AbsolutePath == "/api/v1/members/fhir/Patient"
            ? Upstream.Json("{\"total\":2,\"items\":[{\"resource\":" + Upstream.PatientJson("pat-001", "Smith")
                + "},{\"resource\":" + Upstream.PatientJson("pat-002", "Jones") + "}]}")
            : null!;

        var response = await Get("/fhir/r4/Patient", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Jones");
    }

    [Fact]
    public async Task A_read_that_returns_a_different_member_is_not_found()
    {
        _host.Upstream.Override = r => r.RequestUri!.AbsolutePath == "/api/v1/members/pat-001/fhir"
            ? Upstream.Json(Upstream.PatientJson("pat-002", "Jones"))
            : null!;

        var response = await Get("/fhir/r4/Patient/pat-001", PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/fhir/r4/Patient?family:exact=Smith")]
    [InlineData("/fhir/r4/Patient?_include=Patient:general-practitioner")]
    [InlineData("/fhir/r4/Patient?address=Main")]
    [InlineData("/fhir/r4/Coverage?_id=cov-001,cov-002")]
    [InlineData("/fhir/r4/Coverage?_sort=-period")]
    public async Task Parameters_the_server_would_not_apply_are_refused(string path)
    {
        var response = await Get(path, PatientToken("pat-001"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Body(response))["issue"]![0]!["code"]!.ToString().Should().Be("not-supported");
        _host.Upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Adapter_status_reports_the_cho_native_bindings()
    {
        var response = await _host.CreateClient().GetAsync("/fhir/r4/adapter-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var resources = (await Body(response))["resources"]!.AsArray();
        foreach (var name in new[] { "Patient", "Coverage" })
            resources.Single(r => r!["resource"]!.ToString() == name)!["mode"]!.ToString().Should().Be("Hybrid");
        resources.Single(r => r!["resource"]!.ToString() == "Encounter")!["mode"]!.ToString().Should().Be("Demo");
    }
}

/// <summary>A CHO-native binding without the service URL stops start-up (ADR 018).</summary>
public class ChoNativeStartupTests
{
    private sealed class MissingUrlHost : FhirTestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FhirAdapters:Sources:Patient", "ChoNative");
            builder.UseSetting("Services:MemberServiceUrl", "");
        }
    }

    private sealed class NativeEncounterHost : FhirTestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FhirAdapters:Sources:Encounter", "ChoNative");
        }
    }

    [Fact]
    public void Cho_native_without_the_service_url_does_not_start()
    {
        using var host = new MissingUrlHost();

        var act = () => host.CreateClient();

        act.Should().Throw<Exception>().Where(e => Flatten(e).Contains("Services:MemberServiceUrl"));
    }

    [Fact]
    public void Cho_native_encounter_without_the_service_url_does_not_start()
    {
        // Encounter gained a CHO-native source in ADR 024, so binding it no
        // longer stops start-up for want of an implementation — it stops for
        // want of the upstream URL, the same guard Patient and Coverage have.
        using var host = new NativeEncounterHost();

        var act = () => host.CreateClient();

        act.Should().Throw<Exception>().Where(e => Flatten(e).Contains("Services:EncounterServiceUrl"));
    }

    private static string Flatten(Exception e) => e.InnerException is null ? e.Message : e.Message + " | " + Flatten(e.InnerException);
}
