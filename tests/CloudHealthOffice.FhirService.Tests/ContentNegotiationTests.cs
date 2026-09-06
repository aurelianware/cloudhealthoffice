using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests;

/// <summary>
/// Integration tests that verify content negotiation behaviour:
/// both application/fhir+json and application/json are accepted,
/// and responses always carry application/fhir+json as the content-type.
/// Uses WebApplicationFactory to spin up the real ASP.NET Core pipeline.
/// </summary>
public class ContentNegotiationTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;
    private static readonly JsonSerializerOptions FhirOptions =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    public ContentNegotiationTests(FhirTestWebAppFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>Creates a client with a user/*.read Bearer token for authenticated FHIR requests.</summary>
    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.IssueToken("user/*.read");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// A patient-scoped (Patient Access) client — the member reading their own
    /// record. Not governed by Provider Access consent, which is a control on
    /// disclosure to a THIRD party.
    /// </summary>
    private HttpClient CreatePatientScopedClient(string patientId = "pat-001")
    {
        var client = _factory.CreateClient();
        var token = _factory.IssueToken("patient/*.read", patientId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── /fhir/r4/metadata ────────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_Returns200WithCapabilityStatement()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/fhir/r4/metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var cs = JsonSerializer.Deserialize<CapabilityStatement>(body, FhirOptions);
        cs.Should().NotBeNull();
        cs!.TypeName.Should().Be("CapabilityStatement");
    }

    [Fact]
    public async Task Metadata_ContentType_IsFhirJson()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/fhir/r4/metadata");

        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/fhir+json");
    }

    // ── Accept header negotiation ─────────────────────────────────────────────

    [Theory]
    [InlineData("application/fhir+json")]
    [InlineData("application/json")]
    [InlineData("*/*")]
    public async Task Metadata_AcceptsVariousMediaTypes(string accept)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            MediaTypeWithQualityHeaderValue.Parse(accept));

        var response = await client.GetAsync("/fhir/r4/metadata");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Patient read ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PatientRead_KnownId_Returns200WithPatientResource()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/fhir/r4/Patient/pat-001");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var patient = JsonSerializer.Deserialize<Patient>(body, FhirOptions);
        patient.Should().NotBeNull();
        patient!.Id.Should().Be("pat-001");
        patient.TypeName.Should().Be("Patient");
    }

    [Fact]
    public async Task PatientRead_UnknownMember_ReturnsUniformForbiddenOperationOutcome()
    {
        var client = CreateAuthenticatedClient();

        // CONSENT-01: a provider-shaped caller asking for a member outside its
        // panel gets the same refusal whether or not the member exists — a 404
        // here would confirm non-membership and let a caller enumerate. The
        // response is still a FHIR OperationOutcome in fhir+json, which is what
        // this suite is about.
        var response = await client.GetAsync("/fhir/r4/Patient/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        var outcome = JsonSerializer.Deserialize<OperationOutcome>(body, FhirOptions);
        outcome.Should().NotBeNull();
        outcome!.TypeName.Should().Be("OperationOutcome");
        outcome.Issue.Should().NotBeEmpty();
    }

    // ── Patient search ────────────────────────────────────────────────────────

    [Fact]
    public async Task PatientSearch_ReturnsSearchsetBundle()
    {
        var client = CreatePatientScopedClient();

        // Driven with a patient-scoped token: a provider-shaped token searching
        // the membership by name has no member context to authorize against and
        // is refused (see SmartScopeEnforcementTests). Patient Access — the
        // member reading their own record — is not governed by Provider Access
        // consent, and still exercises the searchset content negotiation this
        // test is for.
        var response = await client.GetAsync("/fhir/r4/Patient?name=Smith");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var bundle = JsonSerializer.Deserialize<Bundle>(body, FhirOptions);
        bundle.Should().NotBeNull();
        bundle!.Type.Should().Be(Bundle.BundleType.Searchset);
        bundle.Entry.Should().NotBeEmpty();
    }

    // ── EOB search ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EobSearch_ByPatient_Returns200Bundle()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/fhir/r4/ExplanationOfBenefit?patient=pat-001");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var bundle = JsonSerializer.Deserialize<Bundle>(body, FhirOptions);
        bundle!.Entry.Should().HaveCount(2); // eob-001, eob-002
    }

    [Fact]
    public async Task EobSearch_WithoutMemberContext_IsRefused()
    {
        var client = CreateAuthenticatedClient();

        // CONSENT-01: with no member named, there is no consent to evaluate, so
        // the authorization layer refuses before the controller's own 400 guard
        // is reached. Failing closed outranks the more descriptive error.
        var response = await client.GetAsync("/fhir/r4/ExplanationOfBenefit");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        var outcome = JsonSerializer.Deserialize<OperationOutcome>(body, FhirOptions);
        outcome!.Issue.Should().NotBeEmpty();
    }

    // ── Coverage search ───────────────────────────────────────────────────────

    [Fact]
    public async Task CoverageSearch_ByPatient_ReturnsMatchingCoverage()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/fhir/r4/Coverage?patient=Patient/pat-001");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var bundle = JsonSerializer.Deserialize<Bundle>(body, FhirOptions);
        bundle!.Entry.Should().HaveCount(1);
    }

    // ── No tenant header on non-metadata endpoints ────────────────────────────

    [Fact]
    public async Task PatientRead_WithoutTenantHeader_Returns401()
    {
        var client = CreateClient(); // no X-Dev-Tenant-ID header
        var response = await client.GetAsync("/fhir/r4/Patient/pat-001");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Metadata_WithoutTenantHeader_Returns200()
    {
        var client = CreateClient(); // metadata is a passthrough
        var response = await client.GetAsync("/fhir/r4/metadata");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
