using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Middleware;
using FhirService.Services.ApiUsage;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.ApiUsage;

/// <summary>
/// Q-20: the Patient Access usage figures a plan files with CMS come from what
/// fhir-service actually served to members' apps.
/// </summary>
public class ApiDisclosureLedgerTests : IClassFixture<FhirTestWebAppFactory>
{
    private static readonly DateTime Now = new(2026, 10, 28, 15, 0, 0, DateTimeKind.Utc);
    private readonly FhirTestWebAppFactory _factory;

    public ApiDisclosureLedgerTests(FhirTestWebAppFactory factory) => _factory = factory;

    // ── Which requests are disclosures ────────────────────────────────────────

    private static HttpContext Request(
        string method = "GET", string path = "/fhir/r4/ExplanationOfBenefit", int status = 200,
        SmartCallerType type = SmartCallerType.Patient, string? patient = "pat-1", string? client = "app-1",
        string? tenant = "t-1", bool smart = true)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Response.StatusCode = status;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Smart"));
        if (smart) FhirCallerSchemes.Mark(http.User, FhirCallerSchemes.SmartMarker);
        http.Items[AuthenticatedCaller.HttpContextItemKey] = new AuthenticatedCaller
        {
            Issuer = "https://auth.test.local", CallerType = type, Scopes = new HashSet<string>(),
            PatientId = patient, ClientId = client,
        };
        if (tenant != null) http.Items["TenantId"] = tenant;
        if (patient != null) http.Items["SmartPatientId"] = patient;
        return http;
    }

    [Fact]
    public void A_members_app_reading_its_data_is_a_patient_access_disclosure()
        => ApiDisclosureRecordingMiddleware.ToDisclosure(Request(), Now).Should().Be(
            new ApiDisclosure("t-1", InteroperabilityApi.PatientAccess, "pat-1", "app-1", new DateOnly(2026, 10, 28)));

    [Fact]
    public void A_post_search_counts()
        => ApiDisclosureRecordingMiddleware.ToDisclosure(Request("POST", "/fhir/r4/Coverage/_search"), Now).Should().NotBeNull();

    [Theory]
    [InlineData("GET", "/fhir/r4/metadata", 200)]
    [InlineData("GET", "/fhir/r4/.well-known/smart-configuration", 200)]
    [InlineData("GET", "/fhir/r4/directory/acme/Practitioner", 200)]
    [InlineData("GET", "/api/v1/interoperability/usage", 200)]
    [InlineData("GET", "/fhir/r4/Patient/pat-2", 404)]
    [InlineData("GET", "/fhir/r4/Patient/pat-2", 403)]
    [InlineData("POST", "/fhir/r4/Task", 201)]
    public void Requests_that_disclose_no_member_data_are_not_counted(string method, string path, int status)
        => ApiDisclosureRecordingMiddleware.ToDisclosure(Request(method, path, status), Now).Should().BeNull();

    [Theory]
    [InlineData(SmartCallerType.User)]
    [InlineData(SmartCallerType.System)]
    [InlineData(SmartCallerType.Unknown)]
    public void Provider_and_backend_callers_are_not_patient_access(SmartCallerType type)
        => ApiDisclosureRecordingMiddleware.ToDisclosure(Request(type: type), Now).Should().BeNull();

    [Fact]
    public void A_patient_token_without_a_patient_or_tenant_is_not_counted()
    {
        ApiDisclosureRecordingMiddleware.ToDisclosure(Request(patient: null), Now).Should().BeNull();
        ApiDisclosureRecordingMiddleware.ToDisclosure(Request(tenant: null), Now).Should().BeNull();
        ApiDisclosureRecordingMiddleware.ToDisclosure(Request(smart: false), Now).Should().BeNull();
    }

    [Fact]
    public void Without_a_resolved_caller_the_tokens_client_id_is_used()
    {
        var http = Request();
        http.Items.Remove(AuthenticatedCaller.HttpContextItemKey);
        ((ClaimsIdentity)http.User.Identity!).AddClaim(new Claim("client_id", "token-app"));

        ApiDisclosureRecordingMiddleware.ToDisclosure(http, Now)!.ClientId.Should().Be("token-app");
    }

    [Fact]
    public void An_app_without_a_client_id_is_still_counted()
        => ApiDisclosureRecordingMiddleware.ToDisclosure(Request(client: null), Now)!.ClientId.Should().Be("(unknown)");

    // ── Counting ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_app_on_one_day_is_one_transfer_however_many_requests()
    {
        var ledger = new InMemoryApiDisclosureLedger();
        var day = new DateOnly(2026, 3, 1);
        for (var i = 0; i < 5; i++)
            await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.PatientAccess, "p1", "app", day));
        await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.PatientAccess, "p2", "app", day));
        await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.PatientAccess, "p2", "app", day.AddDays(1)));
        await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.PatientAccess, "p3", "app", day));
        await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.PatientAccess, "p3", "other-app", day));

        var counts = await ledger.CountAsync("t", InteroperabilityApi.PatientAccess, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));

        counts.Should().Be(new ApiUsageCounts(UniquePatients: 3, PatientsMoreThanOnce: 2));
    }

    [Fact]
    public async Task Counts_keep_to_the_tenant_api_and_period()
    {
        var ledger = new InMemoryApiDisclosureLedger();
        await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.PatientAccess, "p1", "app", new DateOnly(2026, 12, 31)));
        await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.PatientAccess, "p2", "app", new DateOnly(2027, 1, 1)));
        await ledger.RecordAsync(new ApiDisclosure("other", InteroperabilityApi.PatientAccess, "p3", "app", new DateOnly(2026, 6, 1)));
        await ledger.RecordAsync(new ApiDisclosure("t", InteroperabilityApi.ProviderAccess, "p4", "app", new DateOnly(2026, 6, 1)));

        (await ledger.CountAsync("t", InteroperabilityApi.PatientAccess, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)))
            .Should().Be(new ApiUsageCounts(1, 0));
        (await ledger.EarliestDayAsync("t", InteroperabilityApi.PatientAccess)).Should().Be(new DateOnly(2026, 12, 31));
    }

    // ── Through the pipeline ──────────────────────────────────────────────────

    [Fact]
    public async Task A_patient_read_through_the_api_is_recorded_for_the_tokens_tenant_and_app()
    {
        var ledger = (InMemoryApiDisclosureLedger)_factory.Services.GetRequiredService<IApiDisclosureLedger>();
        var token = _factory.IssueToken("patient/*.read", "pat-001", extraClaims: [new Claim("client_id", "member-app-77")]);
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/Patient/pat-001");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // The disclosure is written once the response has gone, so the client
        // can see the response a moment before the ledger has the row.
        for (var i = 0; i < 50 && !ledger.Records.Keys.Any(d => d.PatientId == "pat-001"); i++)
            await Task.Delay(20);
        ledger.Records.Keys.Should().Contain(d =>
            d.TenantId == "test-tenant" && d.PatientId == "pat-001" && d.ClientId == "member-app-77"
            && d.Api == InteroperabilityApi.PatientAccess);
    }

    [Fact]
    public async Task The_usage_report_gives_the_years_figures_for_the_callers_tenant()
    {
        var ledger = (InMemoryApiDisclosureLedger)_factory.Services.GetRequiredService<IApiDisclosureLedger>();
        await ledger.RecordAsync(new ApiDisclosure("tenant-report", InteroperabilityApi.PatientAccess, "p1", "app", new DateOnly(2026, 2, 1)));
        await ledger.RecordAsync(new ApiDisclosure("tenant-report", InteroperabilityApi.PatientAccess, "p1", "app", new DateOnly(2026, 2, 2)));
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.UserToken("tenant-report", ChoRolePermissions.ComplianceOfficer));

        var report = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/api/v1/interoperability/usage?year=2026");

        report!["uniquePatients"]!.GetValue<long>().Should().Be(1);
        report["patientsMoreThanOnce"]!.GetValue<long>().Should().Be(1);
        report["api"]!.GetValue<string>().Should().Be("PatientAccess");
        report["complete"]!.GetValue<bool>().Should().BeFalse("the ledger starts on 1 February and 2026 has not ended");
        report["recordedFrom"]!.GetValue<string>().Should().Be("2026-02-01");
    }

    [Fact]
    public async Task The_usage_report_needs_compliance_read()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.UserToken("tenant-report", ChoRolePermissions.UMCoordinator));

        using var response = await client.GetAsync("/api/v1/interoperability/usage?year=2026");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_smart_token_cannot_read_the_usage_report()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.IssueToken("system/*.read", tenantId: "tenant-report"));

        using var response = await client.GetAsync("/api/v1/interoperability/usage?year=2026");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
