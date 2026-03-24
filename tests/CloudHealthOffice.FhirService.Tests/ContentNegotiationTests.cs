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
    public async Task PatientRead_UnknownId_Returns404WithOperationOutcome()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/fhir/r4/Patient/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

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
        var client = CreateAuthenticatedClient();

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
    public async Task EobSearch_WithoutPatientOrId_Returns400()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/fhir/r4/ExplanationOfBenefit");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

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
