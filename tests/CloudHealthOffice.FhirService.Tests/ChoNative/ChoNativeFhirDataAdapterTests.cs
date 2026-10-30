using System.Net;
using System.Text;
using FluentAssertions;
using FhirService.Models;
using FhirService.Services.ChoNative;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FhirService.Tests.ChoNative;

/// <summary>
/// The CHO-native Patient/Coverage adapter against a stubbed member-service and
/// coverage-service (ADR 018): what it asks for, how it reads the answer, and how
/// it fails.
/// </summary>
public class ChoNativeFhirDataAdapterTests
{
    private const string Tenant = "tenant-a";

    private const string PatientJson =
        """{"resourceType":"Patient","id":"M-1","active":true,"name":[{"family":"Smith","given":["Ann"]}],"gender":"female","birthDate":"1980-05-17"}""";

    private const string CoverageJson =
        """{"resourceType":"Coverage","id":"cov-1","status":"active","beneficiary":{"reference":"Patient/M-1"},"payor":[{"display":"Plan"}]}""";

    private sealed class Upstream : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }

        public string LastPathAndQuery => Uri.UnescapeDataString(Requests.Last().RequestUri!.PathAndQuery);
    }

    private sealed class Clients(Upstream upstream) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(upstream, disposeHandler: false)
        {
            BaseAddress = new Uri(name == ChoNativeFhirDataAdapter.MemberServiceClient
                ? "http://member-service/api"
                : "http://coverage-service/api"),
        };
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Page(long total, params string[] resources) =>
        "{\"total\":" + total + ",\"items\":[" + string.Join(",", resources.Select(r => "{\"memberId\":\"M-1\",\"resource\":" + r + "}")) + "]}";

    private static (ChoNativeFhirDataAdapter Adapter, Upstream Upstream) Create(string? requestTenant = Tenant, string? queryString = null)
    {
        var upstream = new Upstream();
        var http = new DefaultHttpContext();
        if (requestTenant != null) http.Items["TenantId"] = requestTenant;
        if (queryString != null) http.Request.QueryString = new QueryString(queryString);
        var adapter = new ChoNativeFhirDataAdapter(
            new Clients(upstream), new HttpContextAccessor { HttpContext = http }, NullLogger<ChoNativeFhirDataAdapter>.Instance);
        return (adapter, upstream);
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patient_read_asks_member_service_by_member_id_from_the_host_root()
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => Json(PatientJson);

        var patient = await adapter.GetPatientAsync("M 1/x", Tenant, CancellationToken.None);

        patient!.Id.Should().Be("M-1");
        patient.Name[0].Family.Should().Be("Smith");
        upstream.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/members/M%201%2Fx/fhir",
            "the id is escaped and the configured base path cannot shift the route");
    }

    [Fact]
    public async Task Coverage_read_asks_coverage_service()
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => Json(CoverageJson);

        var coverage = await adapter.GetCoverageAsync("cov-1", Tenant, CancellationToken.None);

        coverage!.Beneficiary.Reference.Should().Be("Patient/M-1");
        upstream.Requests.Single().RequestUri!.AbsoluteUri.Should().Be("http://coverage-service/api/v1/coverage/cov-1/fhir");
    }

    [Fact]
    public async Task Upstream_not_found_is_null()
    {
        var (adapter, _) = Create();

        (await adapter.GetPatientAsync("nope", Tenant, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Any_other_failure_status_is_an_upstream_failure_without_its_body(HttpStatusCode status)
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => Json("{\"secret\":\"internal detail\"}", status);

        var act = () => adapter.GetPatientAsync("M-1", Tenant, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<FhirUpstreamException>();
        ex.Which.Resource.Should().Be("Patient");
        ex.Which.Message.Should().NotContain("internal detail");
    }

    [Fact]
    public async Task Transport_failure_is_an_upstream_failure()
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => throw new HttpRequestException("connection refused");

        var act = () => adapter.GetCoverageAsync("cov-1", Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirUpstreamException>().WithMessage("*unreachable*");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"resourceType\":\"Coverage\",\"id\":\"x\",\"status\":\"active\"}")]
    [InlineData("{\"resourceType\":\"Patient\",\"gender\":\"sometimes\"}")]
    public async Task A_body_that_is_not_a_valid_resource_of_the_type_asked_is_an_upstream_failure(string body)
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => Json(body);

        var act = () => adapter.GetPatientAsync("M-1", Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirUpstreamException>().WithMessage("*unreadable*");
    }

    [Fact]
    public async Task A_tenant_argument_other_than_the_requests_tenant_is_refused_before_any_call()
    {
        var (adapter, upstream) = Create(requestTenant: "tenant-b");

        var act = () => adapter.GetPatientAsync("M-1", Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        upstream.Requests.Should().BeEmpty();
    }

    // ── Patient search ───────────────────────────────────────────────────────

    [Fact]
    public async Task Patient_search_translates_fhir_parameters_into_the_structured_query()
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => Json(Page(41, PatientJson));

        var (items, total) = await adapter.SearchPatientsAsync(new PatientSearchParams
        {
            Family = "Smi",
            Given = "An",
            Name = "Sm",
            Gender = "http://hl7.org/fhir/administrative-gender|female",
            BirthDate = "1980-05-17",
            LastUpdated = "ge2026-03-01",
            Identifier = "urn:cho:member-id|M-1",
            Page = 3,
            Count = 10,
        }, Tenant, CancellationToken.None);

        total.Should().Be(41);
        items.Should().ContainSingle().Which.Id.Should().Be("M-1");
        upstream.LastPathAndQuery.Should().Be(
            "/api/v1/members/fhir/Patient?memberId=M-1&family=Smi&given=An&name=Sm&gender=female"
            + "&birthDateFrom=1980-05-17&birthDateTo=1980-05-18"
            + "&lastUpdatedFrom=2026-03-01T00:00:00.0000000Z&offset=20&limit=10");
    }

    [Fact]
    public async Task Repeated_birthdate_parameters_in_the_request_are_anded()
    {
        var (adapter, upstream) = Create(queryString: "?birthdate=ge1980&birthdate=lt1990");
        upstream.Respond = _ => Json(Page(0));

        await adapter.SearchPatientsAsync(new PatientSearchParams { BirthDate = "ge1980" }, Tenant, CancellationToken.None);

        upstream.LastPathAndQuery.Should().Contain("birthDateFrom=1980-01-01&birthDateTo=1990-01-01");
    }

    [Fact]
    public async Task An_id_and_a_member_identifier_that_disagree_match_nothing_without_a_call()
    {
        var (adapter, upstream) = Create();

        var (items, total) = await adapter.SearchPatientsAsync(
            new PatientSearchParams { Id = "M-1", Identifier = "M-2" }, Tenant, CancellationToken.None);

        items.Should().BeEmpty();
        total.Should().Be(0);
        upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_birthdate_range_matches_nothing_without_a_call()
    {
        var (adapter, upstream) = Create(queryString: "?birthdate=ge1990&birthdate=lt1980");

        (await adapter.SearchPatientsAsync(new PatientSearchParams(), Tenant, CancellationToken.None)).Total.Should().Be(0);
        upstream.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("identifier", "http://hl7.org/fhir/sid/us-mbi|1EG4")]
    [InlineData("gender", "nonbinary")]
    [InlineData("birthdate", "ap1980")]
    public async Task Unsupported_patient_search_input_is_refused(string parameter, string value)
    {
        var (adapter, upstream) = Create();
        var search = new PatientSearchParams();
        switch (parameter)
        {
            case "identifier": search.Identifier = value; break;
            case "gender": search.Gender = value; break;
            case "birthdate": search.BirthDate = value; break;
        }

        var act = () => adapter.SearchPatientsAsync(search, Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>();
        upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Pages_beyond_the_upstream_limit_are_refused()
    {
        var (adapter, _) = Create();

        var act = () => adapter.SearchPatientsAsync(new PatientSearchParams { Page = 1002, Count = 10 }, Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>().WithMessage("*deeper*");
    }

    [Theory]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"total\":1}")]
    [InlineData("{\"total\":1,\"items\":[{\"memberId\":\"M-1\"}]}")]
    public async Task An_unreadable_search_page_is_an_upstream_failure(string body)
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => Json(body);

        var act = () => adapter.SearchPatientsAsync(new PatientSearchParams(), Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirUpstreamException>();
    }

    // ── Coverage search ──────────────────────────────────────────────────────

    [Fact]
    public async Task Coverage_search_translates_patient_status_type_and_paging()
    {
        var (adapter, upstream) = Create();
        upstream.Respond = _ => Json(Page(2, CoverageJson));

        var (items, total) = await adapter.SearchCoverageAsync(new CoverageSearchParams
        {
            Patient = "Patient/M-1",
            Status = "active,cancelled",
            Type = ChoNativeFhirDataAdapter.LineOfBusinessSystem + "|medicaid",
            Count = 5,
        }, Tenant, CancellationToken.None);

        total.Should().Be(2);
        items.Should().ContainSingle().Which.Id.Should().Be("cov-1");
        upstream.LastPathAndQuery.Should().Be(
            "/api/v1/coverage/fhir/Coverage?memberId=M-1&status=active&status=cancelled&lineOfBusiness=Medicaid&offset=0&limit=5");
    }

    [Fact]
    public async Task Patient_and_beneficiary_that_disagree_match_nothing_without_a_call()
    {
        var (adapter, upstream) = Create();

        var (items, _) = await adapter.SearchCoverageAsync(
            new CoverageSearchParams { Patient = "M-1", Beneficiary = "Patient/M-2" }, Tenant, CancellationToken.None);

        items.Should().BeEmpty();
        upstream.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Dental")]
    [InlineData("http://terminology.hl7.org/CodeSystem/v3-ActCode|HIP")]
    public async Task An_unknown_coverage_type_is_refused(string type)
    {
        var (adapter, _) = Create();

        var act = () => adapter.SearchCoverageAsync(new CoverageSearchParams { Type = type }, Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>();
    }

    // ── US Core searches (Inferno run 40979709678) ───────────────────────────

    private const string EncounterJson =
        """{"resourceType":"Encounter","id":"enc-1","status":"finished","class":{"system":"http://terminology.hl7.org/CodeSystem/v3-ActCode","code":"AMB"},"subject":{"reference":"Patient/M-1"}}""";

    [Fact]
    public async Task Encounter_identifier_and_class_go_upstream_as_control_number_and_class()
    {
        var (adapter, upstream) = Create(queryString:
            "?identifier=urn:cho:encounter-control-number|ENC-9&class=http://terminology.hl7.org/CodeSystem/v3-ActCode|AMB");
        upstream.Respond = _ => Json(Page(1, EncounterJson));

        var (items, _) = await adapter.SearchEncountersAsync(new EncounterSearchParams(), Tenant, CancellationToken.None);

        items.Should().ContainSingle();
        upstream.LastPathAndQuery.Should().Be(
            "/api/encounters/fhir/Encounter?controlNumber=ENC-9&encounterClass=AMB&offset=0&limit=20");
    }

    [Fact]
    public async Task An_encounter_identifier_in_another_system_is_refused()
    {
        var (adapter, _) = Create(queryString: "?identifier=urn:other|ENC-9");

        var act = () => adapter.SearchEncountersAsync(new EncounterSearchParams(), Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>();
    }

    [Fact]
    public async Task A_provenance_revinclude_on_patient_is_accepted_and_changes_nothing_upstream()
    {
        var (adapter, upstream) = Create(queryString: "?_id=M-1&_revinclude=Provenance:target");
        upstream.Respond = _ => Json(Page(1, PatientJson));

        var (items, _) = await adapter.SearchPatientsAsync(new PatientSearchParams { Id = "M-1" }, Tenant, CancellationToken.None);

        items.Should().ContainSingle();
        upstream.LastPathAndQuery.Should().NotContain("revinclude").And.NotContain("Provenance");
    }

    [Fact]
    public async Task A_provenance_revinclude_on_encounter_is_accepted_and_changes_nothing_upstream()
    {
        var (adapter, upstream) = Create(queryString: "?patient=M-1&_revinclude=Provenance:target");
        upstream.Respond = _ => Json(Page(1, EncounterJson));

        var (items, _) = await adapter.SearchEncountersAsync(new EncounterSearchParams { Patient = "M-1" }, Tenant, CancellationToken.None);

        items.Should().ContainSingle();
        upstream.LastPathAndQuery.Should().NotContain("revinclude").And.NotContain("Provenance");
    }

    [Fact]
    public async Task Any_other_revinclude_is_refused()
    {
        var (adapter, _) = Create(queryString: "?_revinclude=Encounter:subject");

        var act = () => adapter.SearchPatientsAsync(new PatientSearchParams(), Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>().WithMessage("*Provenance:target*");
    }

    // ── Coverage _include (CARIN BB nightly 2026-10-30) ──────────────────────

    [Fact]
    public async Task A_payor_include_on_coverage_is_accepted_when_the_payor_is_display_only()
    {
        var (adapter, upstream) = Create(queryString: "?_id=cov-1&_include=Coverage:payor");
        upstream.Respond = _ => Json(Page(1, CoverageJson));

        var (items, _) = await adapter.SearchCoverageAsync(new CoverageSearchParams { Id = "cov-1" }, Tenant, CancellationToken.None);

        items.Should().ContainSingle();
        upstream.LastPathAndQuery.Should().NotContain("include").And.NotContain("payor");
    }

    [Fact]
    public async Task A_payor_include_is_refused_when_a_payor_is_a_resource_it_would_have_to_include()
    {
        var (adapter, upstream) = Create(queryString: "?_id=cov-1&_include=Coverage:payor");
        upstream.Respond = _ => Json(Page(1,
            """{"resourceType":"Coverage","id":"cov-1","status":"active","beneficiary":{"reference":"Patient/M-1"},"payor":[{"reference":"Organization/plan-1"}]}"""));

        var act = () => adapter.SearchCoverageAsync(new CoverageSearchParams { Id = "cov-1" }, Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>().WithMessage("*Coverage:payor*");
    }

    [Fact]
    public async Task Any_other_coverage_include_is_refused()
    {
        var (adapter, _) = Create(queryString: "?_include=Coverage:beneficiary");

        var act = () => adapter.SearchCoverageAsync(new CoverageSearchParams(), Tenant, CancellationToken.None);

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>().WithMessage("*Coverage:payor*");
    }
}
