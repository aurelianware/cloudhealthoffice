using System.Text.Json.Nodes;
using CloudHealthOffice.ProviderService.Tests.Fakes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Capability 5.7 — endpoint-shape coverage for the new
/// <see cref="FhirPractitionerController"/>: read-by-NPI happy path,
/// 404s on unknown / non-Individual NPIs, 400 on malformed NPI, search
/// parameter wiring (given/family/city/state/postal-code/specialty),
/// FHIR Bundle searchset shape, identifier alias resolution, and
/// tenant scoping.
/// </summary>
public class FhirPractitionerControllerTests
{
    private const string TenantId = "tenant-a";

    private readonly InMemoryProviderRepository _repository = new() { TenantId = TenantId };
    private readonly FhirPractitionerProjector _projector = new();
    private readonly FhirPractitionerController _controller;

    public FhirPractitionerControllerTests()
    {
        _controller = new FhirPractitionerController(_repository, _projector,
            NullLogger<FhirPractitionerController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = TenantId;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("provider.test.local");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    private static Provider IndividualProvider(string npi, string firstName, string lastName, string? city = null, string? state = null, string? zip = null, string? specialty = null) => new()
    {
        TenantId = TenantId,
        Id = $"v-{npi}",
        ProviderId = $"p-{npi}",
        VersionId = $"v-{npi}",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        NPI = npi,
        ProviderType = ProviderType.Individual,
        FirstName = firstName,
        LastName = lastName,
        PrimarySpecialty = specialty ?? "Internal Medicine",
        TaxonomyCode = "207R00000X",
        City = city,
        State = state,
        ZipCode = zip,
    };

    private static Provider OrganizationProvider(string npi) => new()
    {
        TenantId = TenantId,
        Id = $"v-{npi}",
        ProviderId = $"p-{npi}",
        VersionId = $"v-{npi}",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        NPI = npi,
        ProviderType = ProviderType.Organization,
        OrganizationName = "Acme Hospital",
        PrimarySpecialty = "Hospital",
        TaxonomyCode = "282N00000X",
    };

    private static JsonObject ParseFhirContent(IActionResult result)
    {
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/fhir+json");
        return JsonNode.Parse(content.Content!)!.AsObject();
    }

    [Fact]
    public async Task ReadPractitioner_returns_projection_for_known_NPI()
    {
        await _repository.CreateAsync(IndividualProvider("1234567890", "Jane", "Doe"));
        var result = await _controller.ReadPractitioner("1234567890", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        var body = ParseFhirContent(result);
        body["resourceType"]!.GetValue<string>().Should().Be("Practitioner");
        body["id"]!.GetValue<string>().Should().Be("1234567890");
    }

    [Fact]
    public async Task ReadPractitioner_returns_404_OperationOutcome_for_unknown_NPI()
    {
        var result = await _controller.ReadPractitioner("9999999999", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        var body = ParseFhirContent(result);
        body["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
        body["issue"]!.AsArray().Single()!["code"]!.GetValue<string>().Should().Be("not-found");
    }

    [Fact]
    public async Task ReadPractitioner_returns_404_for_organization_provider()
    {
        await _repository.CreateAsync(OrganizationProvider("1111111111"));
        var result = await _controller.ReadPractitioner("1111111111", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData("not-an-npi")]
    [InlineData("12345")]
    [InlineData("12345678901")]
    [InlineData("123456789X")]
    public async Task ReadPractitioner_returns_400_for_malformed_NPI(string npi)
    {
        var result = await _controller.ReadPractitioner(npi, default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        var body = ParseFhirContent(result);
        body["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
        body["issue"]!.AsArray().Single()!["code"]!.GetValue<string>().Should().Be("invalid");
    }

    [Fact]
    public async Task SearchPractitioners_NPI_param_degrades_to_single_entry_bundle()
    {
        await _repository.CreateAsync(IndividualProvider("1234567890", "Jane", "Doe"));
        var result = await _controller.SearchPractitioners(
            npi: "1234567890", identifier: null, given: null, family: null,
            city: null, state: null, postalCode: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["resourceType"]!.GetValue<string>().Should().Be("Bundle");
        bundle["type"]!.GetValue<string>().Should().Be("searchset");
        bundle["total"]!.GetValue<int>().Should().Be(1);
        var entry = bundle["entry"]!.AsArray().Single()!.AsObject();
        entry["resource"]!["id"]!.GetValue<string>().Should().Be("1234567890");
        entry["search"]!["mode"]!.GetValue<string>().Should().Be("match");
    }

    [Fact]
    public async Task SearchPractitioners_NPI_for_organization_returns_empty_bundle()
    {
        await _repository.CreateAsync(OrganizationProvider("1111111111"));
        var result = await _controller.SearchPractitioners(
            npi: "1111111111", identifier: null, given: null, family: null,
            city: null, state: null, postalCode: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(0);
        bundle["entry"]!.AsArray().Count.Should().Be(0);
    }

    [Theory]
    [InlineData("NPI:1234567890", "1234567890")]
    [InlineData("http://hl7.org/fhir/sid/us-npi|1234567890", "1234567890")]
    [InlineData("NPI|1234567890", "1234567890")]
    [InlineData("|1234567890", "1234567890")]
    [InlineData("1234567890", "1234567890")]
    public async Task SearchPractitioners_identifier_alias_resolves_to_NPI(
        string identifier, string expectedMatchingNpi)
    {
        await _repository.CreateAsync(IndividualProvider(expectedMatchingNpi, "Jane", "Doe"));
        var result = await _controller.SearchPractitioners(
            npi: null, identifier: identifier, given: null, family: null,
            city: null, state: null, postalCode: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(1);
        bundle["entry"]!.AsArray().Single()!["resource"]!["id"]!.GetValue<string>()
            .Should().Be(expectedMatchingNpi);
    }

    [Theory]
    [InlineData("urn:other:system|1234567890")]
    [InlineData("http://other-system.example.com|1234567890")]
    public async Task SearchPractitioners_identifier_with_unrecognized_system_returns_400(
        string identifier)
    {
        // Per FHIR token semantics, an identifier supplied with a system
        // we don't index should NOT silently fall through to a broad
        // search — that would ignore a caller-supplied filter.
        await _repository.CreateAsync(IndividualProvider("1234567890", "Jane", "Doe"));
        var result = await _controller.SearchPractitioners(
            npi: null, identifier: identifier, given: null, family: null,
            city: null, state: null, postalCode: null, specialty: null);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        var body = ParseFhirContent(result);
        body["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
        body["issue"]!.AsArray().Single()!["code"]!.GetValue<string>().Should().Be("invalid");
    }

    [Fact]
    public async Task SearchPractitioners_bundle_entries_omit_fullUrl()
    {
        // Bundle.entry.fullUrl is optional in FHIR R4. provider-service
        // omits it because the response is reachable directly OR via
        // fhir-service's proxy; emitting fullUrl from HttpContext.Request
        // would leak the internal provider-service host through the
        // proxy path.
        await _repository.CreateAsync(IndividualProvider("1234567890", "Jane", "Doe"));
        var result = await _controller.SearchPractitioners(
            npi: "1234567890", identifier: null, given: null, family: null,
            city: null, state: null, postalCode: null, specialty: null);
        var bundle = ParseFhirContent(result);
        var entry = bundle["entry"]!.AsArray().Single()!.AsObject();
        entry.ContainsKey("fullUrl").Should().BeFalse();
        entry.ContainsKey("resource").Should().BeTrue();
        entry["search"]!["mode"]!.GetValue<string>().Should().Be("match");
    }

    [Fact]
    public async Task SearchPractitioners_filters_by_family_and_given()
    {
        await _repository.CreateAsync(IndividualProvider("1111111111", "Alice", "Smith"));
        await _repository.CreateAsync(IndividualProvider("2222222222", "Bob", "Smith"));
        await _repository.CreateAsync(IndividualProvider("3333333333", "Bob", "Jones"));

        var result = await _controller.SearchPractitioners(
            npi: null, identifier: null, given: "Bob", family: "Smith",
            city: null, state: null, postalCode: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(1);
        bundle["entry"]!.AsArray().Single()!["resource"]!["id"]!.GetValue<string>()
            .Should().Be("2222222222");
    }

    [Fact]
    public async Task SearchPractitioners_filters_by_city_state_postal()
    {
        await _repository.CreateAsync(IndividualProvider("1111111111", "Alice", "Boston", city: "Boston", state: "MA", zip: "02101"));
        await _repository.CreateAsync(IndividualProvider("2222222222", "Alice", "Cambridge", city: "Cambridge", state: "MA", zip: "02139"));
        await _repository.CreateAsync(IndividualProvider("3333333333", "Alice", "Albany", city: "Albany", state: "NY", zip: "12207"));

        var result = await _controller.SearchPractitioners(
            npi: null, identifier: null, given: null, family: null,
            city: "Boston", state: "MA", postalCode: "02101", specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(1);
        bundle["entry"]!.AsArray().Single()!["resource"]!["id"]!.GetValue<string>()
            .Should().Be("1111111111");
    }

    [Fact]
    public async Task SearchPractitioners_excludes_organization_providers()
    {
        await _repository.CreateAsync(IndividualProvider("1111111111", "Alice", "Smith"));
        await _repository.CreateAsync(OrganizationProvider("2222222222"));

        var result = await _controller.SearchPractitioners(
            npi: null, identifier: null, given: null, family: null,
            city: null, state: null, postalCode: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["entry"]!.AsArray()
            .Select(e => e!["resource"]!["id"]!.GetValue<string>())
            .Should().BeEquivalentTo(new[] { "1111111111" });
    }

    [Fact]
    public async Task ReadPractitioner_does_not_see_other_tenants_providers()
    {
        // Tenant-A owns the provider; controller is configured with
        // tenant-A. The fake's TenantId is set to tenant-A so a
        // tenant-B caller would not match. Simulate by writing into the
        // repo as tenant-b directly.
        var foreign = IndividualProvider("1234567890", "Jane", "Doe");
        foreign.TenantId = "tenant-b";
        await _repository.CreateAsync(foreign);

        var result = await _controller.ReadPractitioner("1234567890", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
    }
}
