using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FhirService.Services;
using FhirService.Services.Identity;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// The SMART gaps left after the CHO migration: QuestionnaireResponse patient
/// binding, the CRD hook outside /fhir/r4, tokens holding both patient/ and
/// user/*.read scopes, and the tenant of the bundled smart-auth-service's
/// tokens.
/// </summary>
public class SmartGapTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "test-tenant";
    private const string Questionnaire = "Questionnaire/q-imaging-mri";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Hl7.Fhir.Model.Claim).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public SmartGapTests(FhirTestWebAppFactory factory) => _factory = factory;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>Stores a completed response for the member straight through the service.</summary>
    private async Task<string> SeedResponseAsync(string memberId)
    {
        var dtr = _factory.Services.GetRequiredService<IDtrService>();
        var stored = await dtr.SubmitResponseAsync(new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = Questionnaire,
            Subject = new ResourceReference($"Patient/{memberId}"),
        }, Tenant);
        return stored.Id;
    }

    private string Patient(string id, string scopes = "patient/*.read") => _factory.IssueToken(scopes, id);

    // ── 1. QuestionnaireResponse ──────────────────────────────────────────────

    [Fact]
    public async Task PatientToken_ReadsItsOwnQuestionnaireResponse()
    {
        var own = await SeedResponseAsync("pat-001");
        (await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse/{own}", Patient("pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PatientToken_CannotReadAnotherPatientsQuestionnaireResponse()
    {
        var other = await SeedResponseAsync("pat-002");
        (await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse/{other}", Patient("pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatientToken_QuestionnaireResponseSearch_IsConstrainedToItsPatient()
    {
        await SeedResponseAsync("pat-001");
        await SeedResponseAsync("pat-002");

        var response = await SendAsync(HttpMethod.Get, "/fhir/r4/QuestionnaireResponse", Patient("pat-001"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = JsonSerializer.Deserialize<Bundle>(await response.Content.ReadAsStringAsync(), FhirJson)!;
        bundle.Entry.Should().NotBeEmpty();
        bundle.Entry.Select(e => ((QuestionnaireResponse)e.Resource).Subject.Reference)
            .Should().OnlyContain(r => r == "Patient/pat-001");
    }

    [Fact]
    public async Task PatientToken_CannotSearchQuestionnaireResponsesForAnotherPatient()
        => (await SendAsync(HttpMethod.Get, "/fhir/r4/QuestionnaireResponse?patient=Patient/pat-002", Patient("pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task PatientToken_CannotSubmitAQuestionnaireResponseForAnotherPatient()
    {
        var body = JsonSerializer.Serialize(new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = Questionnaire,
            Subject = new ResourceReference("Patient/pat-002"),
        }, FhirJson);

        var response = await SendAsync(HttpMethod.Post, "/fhir/r4/QuestionnaireResponse",
            Patient("pat-001", "patient/QuestionnaireResponse.write"),
            new StringContent(body, Encoding.UTF8, "application/fhir+json"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── 3. patient/ + user/*.read ─────────────────────────────────────────────

    [Fact]
    public async Task MixedToken_IsBoundOutsideProviderAccess_QuestionnaireResponse()
    {
        // Before: user/*.read switched the patient binding off everywhere, and
        // QuestionnaireResponse is not a Provider Access resource, so nothing
        // checked the member at all.
        var other = await SeedResponseAsync("pat-002");
        var token = Patient("pat-001", "patient/*.read user/*.read");

        (await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse/{other}", token))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MixedToken_ProviderAccessRead_IsLeftToAttributionAndConsent()
    {
        // A Provider Access read stays unbound: the filter decides it. pat-002 is
        // on test-user's panel with an active ProviderAccess consent.
        var token = Patient("pat-001", "patient/*.read user/*.read");
        (await SendAsync(HttpMethod.Get, "/fhir/r4/Coverage?patient=Patient/pat-002", token))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // ...and the filter refuses a member who is not on the panel.
        (await SendAsync(HttpMethod.Get, "/fhir/r4/Coverage?patient=Patient/pat-999", token))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── 2. CRD hook ───────────────────────────────────────────────────────────

    private static StringContent Hook(string patientId) => new(
        "{\"hookInstance\":\"h-1\",\"hook\":\"order-sign\",\"context\":{\"userId\":\"Practitioner/p1\","
        + "\"patientId\":\"" + patientId + "\",\"draftOrders\":{\"resourceType\":\"Bundle\",\"entry\":[]}}}",
        Encoding.UTF8, "application/json");

    [Fact]
    public async Task CrdHook_PatientToken_ForItsOwnPatient_IsServed()
        => (await SendAsync(HttpMethod.Post, "/cds-services/cho-order-sign",
                Patient("pat-001", "patient/Coverage.read"), Hook("pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task CrdHook_PatientToken_ForAnotherPatient_Is403()
        => (await SendAsync(HttpMethod.Post, "/cds-services/cho-order-sign",
                Patient("pat-001", "patient/Coverage.read"), Hook("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task CrdHook_MixedToken_ForAnotherPatient_Is403()
        => (await SendAsync(HttpMethod.Post, "/cds-services/cho-order-sign",
                Patient("pat-001", "patient/*.read user/*.read"), Hook("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task CrdHook_TokenWithoutACoverageScope_Is403()
        => (await SendAsync(HttpMethod.Post, "/cds-services/cho-order-sign",
                _factory.IssueToken("user/Practitioner.read"), Hook("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task CrdHook_ProviderToken_WithCoverageRead_IsServed()
        => (await SendAsync(HttpMethod.Post, "/cds-services/cho-order-sign",
                _factory.IssueToken("user/Coverage.read"), Hook("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task CrdConfig_RefusesEverySmartToken()
        => (await SendAsync(HttpMethod.Get, "/api/v1/crd/code-classification",
                _factory.IssueToken("system/*.* user/*.*")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    // ── 4. smart-auth-service tokens and their tenant ─────────────────────────

    [Fact]
    public void DemoIssuer_ConfinedToOneTenant_GivesItsTokensThatTenant()
    {
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.Demo,
            Issuer = "http://smart-auth-service:8080",
            Tenants = ["demo-tenant"],
        };
        var registry = new TrustedIssuerRegistry(options);
        var caller = new AuthenticatedCaller
        {
            Issuer = "http://smart-auth-service:8080",
            CallerType = SmartCallerType.Patient,
            Scopes = new HashSet<string>(),
        };

        // A smart-auth-service token carries no tenant claim.
        var tenant = SmartTenant.Resolve(new ClaimsPrincipal(new ClaimsIdentity([], "Smart")), caller, registry);

        tenant.Tenant.Should().Be("demo-tenant");
        tenant.ClaimToAdd.Should().Be("demo-tenant");
    }

    [Fact]
    public void DevelopmentConfig_ConfinesTheDemoIssuerToOneTenant()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindServiceDirectory(), "appsettings.json"))
            .AddJsonFile(Path.Combine(FindServiceDirectory(), "appsettings.Development.json"))
            .Build();
        var options = new SmartTrustOptions();
        config.GetSection(SmartTrustOptions.SectionName).Bind(options);

        options.Mode.Should().Be(SmartTrustMode.Demo);
        options.NormalizedIssuers().Should().ContainSingle()
            .Which.Tenants.Should().ContainSingle();
    }

    private static string FindServiceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "services", "fhir-service")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "services", "fhir-service");
    }
}
