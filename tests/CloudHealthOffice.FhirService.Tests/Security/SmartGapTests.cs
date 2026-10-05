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
    public async Task MixedToken_CannotReadAnotherPatientsQuestionnaireResponse()
    {
        // Originally user/*.read switched the patient binding off everywhere and
        // nothing checked the member at all. QuestionnaireResponse is now a
        // Provider Access resource, so for a token holding user/*.read the filter
        // decides: a read naming no member is refused (403), as for every other
        // member-scoped resource.
        var other = await SeedResponseAsync("pat-002");
        var token = Patient("pat-001", "patient/*.read user/*.read");

        (await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse/{other}", token))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── 1b. QuestionnaireResponse under Provider Access (user/ and system/) ───
    //
    // test-user is attributed to pat-001..pat-003, each with an active
    // ProviderAccess consent; pat-999 is on nobody's panel.

    private string Provider(string scopes = "user/QuestionnaireResponse.read") => _factory.IssueToken(scopes);

    [Theory]
    [InlineData("user/QuestionnaireResponse.read")]
    [InlineData("system/QuestionnaireResponse.read")]
    public async Task ProviderToken_UnattributedMembersQuestionnaireResponse_Is403(string scopes)
    {
        var id = await SeedResponseAsync("pat-999");

        (await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse/{id}?patient=Patient/pat-999", Provider(scopes)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/fhir/r4/QuestionnaireResponse?patient=Patient/pat-999", Provider(scopes)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProviderToken_AttributedMemberWithConsent_ReadsTheirQuestionnaireResponse()
    {
        var id = await SeedResponseAsync("pat-002");

        var response = await SendAsync(HttpMethod.Get,
            $"/fhir/r4/QuestionnaireResponse/{id}?patient=Patient/pat-002", Provider());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var qr = JsonSerializer.Deserialize<QuestionnaireResponse>(await response.Content.ReadAsStringAsync(), FhirJson)!;
        qr.Subject.Reference.Should().Be("Patient/pat-002");
    }

    [Theory]
    [InlineData("/fhir/r4/QuestionnaireResponse/{id}")]
    [InlineData("/fhir/r4/QuestionnaireResponse")]
    [InlineData("/fhir/r4/QuestionnaireResponse?status=completed")]
    public async Task ProviderToken_NamingNoMember_Is403(string path)
    {
        // Before: QuestionnaireResponse was outside the Provider Access filter,
        // so this read (or this search, which returned every member's) was served.
        var id = await SeedResponseAsync("pat-999");

        (await SendAsync(HttpMethod.Get, path.Replace("{id}", id), Provider()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProviderToken_CannotUseOneMembersAuthorizationToReadAnothersQuestionnaireResponse()
    {
        // Authorized for pat-002 (attributed + consent), asking for pat-999's id.
        var foreign = await SeedResponseAsync("pat-999");

        (await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse/{foreign}?patient=Patient/pat-002", Provider()))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("patient=Patient/pat-002")]
    [InlineData("subject=Patient/pat-002")] // the filter authorizes on subject; the search must honour it too
    public async Task ProviderToken_QuestionnaireResponseSearch_ReturnsOnlyTheAuthorizedMember(string query)
    {
        await SeedResponseAsync("pat-002");
        await SeedResponseAsync("pat-999");

        var response = await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse?{query}", Provider());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = JsonSerializer.Deserialize<Bundle>(await response.Content.ReadAsStringAsync(), FhirJson)!;
        bundle.Entry.Should().NotBeEmpty();
        bundle.Entry.Select(e => ((QuestionnaireResponse)e.Resource).Subject.Reference)
            .Should().OnlyContain(r => r == "Patient/pat-002");
    }

    [Fact]
    public async Task ChoCaller_QuestionnaireResponseRead_IsUnchanged()
    {
        // CHO staff are governed by their permission (authorizations:read), not
        // by Provider Access.
        var id = await SeedResponseAsync("pat-999");
        var token = CloudHealthOffice.Infrastructure.Security.ChoDevelopmentAuth.UserToken(
            Tenant, CloudHealthOffice.Infrastructure.Security.ChoRolePermissions.UMCoordinator);

        (await SendAsync(HttpMethod.Get, $"/fhir/r4/QuestionnaireResponse/{id}", token))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 1c. Every governed resource is pinned to the member Provider Access authorized ──
    //
    // Authorization for one member must not return another's resource: by id
    // (cov-002, clm-003, enc-003 belong to pat-002) or through a member search
    // parameter the filter reads but the controller does not.

    [Theory]
    [InlineData("/fhir/r4/Coverage/cov-002?patient=Patient/pat-001", "user/Coverage.read")]
    [InlineData("/fhir/r4/Claim/clm-003?patient=Patient/pat-001", "user/Claim.read")]
    [InlineData("/fhir/r4/Encounter/enc-003?patient=Patient/pat-001", "user/Encounter.read")]
    [InlineData("/fhir/r4/ExplanationOfBenefit/eob-003?patient=Patient/pat-001", "user/ExplanationOfBenefit.read")]
    public async Task ProviderToken_CannotReadAnotherMembersResourceById_UnderOneMembersAuthorization(string path, string scopes)
        => (await SendAsync(HttpMethod.Get, path, _factory.IssueToken(scopes)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Theory]
    [InlineData("/fhir/r4/Coverage?subject=Patient/pat-001", "user/Coverage.read")]
    [InlineData("/fhir/r4/Claim?subject=Patient/pat-001", "user/Claim.read")]
    [InlineData("/fhir/r4/Encounter?beneficiary=Patient/pat-001", "user/Encounter.read")]
    [InlineData("/fhir/r4/Patient?patient=Patient/pat-001", "user/Patient.read")]
    public async Task ProviderToken_Search_IsPinnedToTheAuthorizedMember(string path, string scopes)
    {
        var response = await SendAsync(HttpMethod.Get, path, _factory.IssueToken(scopes));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = JsonSerializer.Deserialize<Bundle>(await response.Content.ReadAsStringAsync(), FhirJson)!;
        bundle.Entry.Should().NotBeEmpty();
        bundle.Entry.Select(e => MemberOf(e.Resource)).Should().OnlyContain(m => m == "pat-001");
    }

    private static string? MemberOf(Resource resource) => (resource switch
    {
        Hl7.Fhir.Model.Patient p => p.Id,
        Coverage c => c.Beneficiary?.Reference,
        Hl7.Fhir.Model.Claim c => c.Patient?.Reference,
        Encounter e => e.Subject?.Reference,
        _ => null,
    })?.Replace("Patient/", string.Empty);

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
