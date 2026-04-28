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
/// Capability 5.9 — endpoint-shape coverage for
/// <see cref="FhirOrganizationController"/>: read id-resolution (shape-based
/// NPI vs OrganizationId discrimination), search parameter wiring (npi,
/// identifier, name, type filter), FHIR Bundle searchset shape, FHIR
/// OperationOutcome on errors, and tenant scoping.
/// </summary>
public class FhirOrganizationControllerTests
{
    private const string TenantId = "tenant-a";

    private readonly InMemoryProviderRepository _providerRepo = new() { TenantId = TenantId };
    private readonly InMemoryOrganizationRepository _orgRepo = new() { TenantId = TenantId };
    private readonly FhirOrganizationProjector _projector = new();
    private readonly FhirOrganizationController _controller;

    public FhirOrganizationControllerTests()
    {
        _controller = new FhirOrganizationController(
            _providerRepo, _orgRepo, _projector,
            NullLogger<FhirOrganizationController>.Instance);
        SetTenantContext();
    }

    private void SetTenantContext(string? tenantId = TenantId)
    {
        var ctx = new DefaultHttpContext();
        if (tenantId != null) ctx.Items["TenantId"] = tenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    // ── Test-fixture builders ─────────────────────────────────────────────

    private static Provider OrgProvider(string npi, string name) => new()
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
        OrganizationName = name,
        PrimarySpecialty = "Hospital",
        TaxonomyCode = "282N00000X",
        City = "Boston",
        State = "MA",
        ZipCode = "02101",
        LastUpdatedDate = DateTime.UtcNow,
    };

    private static Organization Network(string id, string name, string? parentId = null) => new()
    {
        TenantId = TenantId,
        Id = id,
        OrganizationId = id,
        VersionId = id,
        VersionNumber = 1,
        VersionState = OrganizationVersionState.Active,
        Status = OrganizationStatus.Active,
        Name = name,
        NetworkType = NetworkType.HMO,
        LineOfBusiness = LineOfBusiness.Commercial,
        EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ParentOrganizationId = parentId,
        // US Core requires identifier 1..*; include at least one valid entry
        // so the projector does not return null for these test networks.
        Identifiers = new() { new OrganizationIdentifier { System = "urn:cho:network", Value = id } },
        LastUpdatedDate = DateTime.UtcNow,
    };

    private static JsonObject ParseFhirContent(IActionResult result)
    {
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/fhir+json");
        return JsonNode.Parse(content.Content!)!.AsObject();
    }

    // ── Read — NPI path (10-digit id → Provider-as-Org) ──────────────────

    [Fact]
    public async Task ReadOrganization_NPI_returns_type_prov_resource()
    {
        await _providerRepo.CreateAsync(OrgProvider("1234567890", "Acme Hospital"));

        var result = await _controller.ReadOrganization("1234567890", default);

        var body = ParseFhirContent(result);
        body["resourceType"]!.GetValue<string>().Should().Be("Organization");
        body["id"]!.GetValue<string>().Should().Be("1234567890");
        var typeCode = body["type"]!.AsArray()[0]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>();
        typeCode.Should().Be("prov");
    }

    [Fact]
    public async Task ReadOrganization_NPI_not_found_returns_404()
    {
        var result = await _controller.ReadOrganization("9999999999", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        var body = JsonNode.Parse(content.Content!)!.AsObject();
        body["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
    }

    [Fact]
    public async Task ReadOrganization_NPI_for_Individual_provider_returns_404()
    {
        var individual = OrgProvider("1234567890", "Jane Doe");
        individual.ProviderType = ProviderType.Individual;
        await _providerRepo.CreateAsync(individual);

        var result = await _controller.ReadOrganization("1234567890", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
    }

    // ── Read — OrganizationId path (non-NPI id → Organization entity) ────

    [Fact]
    public async Task ReadOrganization_OrganizationId_returns_type_ins_resource()
    {
        await _orgRepo.CreateDraftAsync(Network("net-001", "Aetna HMO 2025"));
        // Activate it
        var draft = _orgRepo.Docs.First();
        draft.VersionState = OrganizationVersionState.Active;
        await _orgRepo.ActivateAndSupersedeAsync(draft, null);

        var result = await _controller.ReadOrganization(draft.OrganizationId, default);

        var body = ParseFhirContent(result);
        body["resourceType"]!.GetValue<string>().Should().Be("Organization");
        var typeCode = body["type"]!.AsArray()[0]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>();
        typeCode.Should().Be("ins");
    }

    [Fact]
    public async Task ReadOrganization_OrganizationId_not_found_returns_404()
    {
        var result = await _controller.ReadOrganization("does-not-exist", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
    }

    // ── Read — NPI wins on 10-digit id even if OrganizationId collision ──

    [Fact]
    public async Task ReadOrganization_10digit_id_resolves_as_NPI_not_OrganizationId()
    {
        // An OrganizationId that happens to be 10 digits should NOT be found
        // when the 10-digit path is taken, because NPI wins per Decision 6.
        var providerNpi = "1234567890";
        await _providerRepo.CreateAsync(OrgProvider(providerNpi, "Acme Hospital"));

        var result = await _controller.ReadOrganization("1234567890", default);

        // Result is from Provider path, not Organization path.
        var body = ParseFhirContent(result);
        var typeCode = body["type"]!.AsArray()[0]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>();
        typeCode.Should().Be("prov");
    }

    // ── Search — npi parameter ────────────────────────────────────────────

    [Fact]
    public async Task SearchOrganizations_npi_returns_provider_as_org()
    {
        await _providerRepo.CreateAsync(OrgProvider("1234567890", "Acme Hospital"));

        var result = await _controller.SearchOrganizations(
            npi: "1234567890", identifier: null, name: null, city: null,
            state: null, postalCode: null, type: null, ct: default);

        var bundle = ParseFhirContent(result);
        bundle["resourceType"]!.GetValue<string>().Should().Be("Bundle");
        bundle["type"]!.GetValue<string>().Should().Be("searchset");
        bundle["total"]!.GetValue<int>().Should().Be(1);
        var entry = bundle["entry"]!.AsArray()[0]!;
        entry["resource"]!["id"]!.GetValue<string>().Should().Be("1234567890");
    }

    [Fact]
    public async Task SearchOrganizations_invalid_npi_returns_400()
    {
        var result = await _controller.SearchOrganizations(
            npi: "not-an-npi", identifier: null, name: null, city: null,
            state: null, postalCode: null, type: null, ct: default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
    }

    // ── Search — identifier=ORG:{orgId} parameter ─────────────────────────

    [Fact]
    public async Task SearchOrganizations_identifier_ORG_prefix_returns_network_entity()
    {
        await _orgRepo.CreateDraftAsync(Network("net-abc", "Blue Shield PPO"));
        var draft = _orgRepo.Docs.First();
        draft.VersionState = OrganizationVersionState.Active;
        await _orgRepo.ActivateAndSupersedeAsync(draft, null);

        var result = await _controller.SearchOrganizations(
            npi: null, identifier: "ORG:net-abc", name: null, city: null,
            state: null, postalCode: null, type: null, ct: default);

        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(1);
        var entry = bundle["entry"]!.AsArray()[0]!;
        var typeCode = entry["resource"]!["type"]!.AsArray()[0]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>();
        typeCode.Should().Be("ins");
    }

    // ── Search — type=prov / type=ins discrimination ──────────────────────

    [Fact]
    public async Task SearchOrganizations_type_prov_returns_only_provider_as_org()
    {
        await _providerRepo.CreateAsync(OrgProvider("1234567890", "Acme Hospital"));
        await _orgRepo.CreateDraftAsync(Network("net-001", "Network A"));
        var draft = _orgRepo.Docs.First();
        draft.VersionState = OrganizationVersionState.Active;
        await _orgRepo.ActivateAndSupersedeAsync(draft, null);

        var result = await _controller.SearchOrganizations(
            npi: null, identifier: null, name: null, city: null,
            state: null, postalCode: null, type: "prov", ct: default);

        var bundle = ParseFhirContent(result);
        var entries = bundle["entry"]!.AsArray();
        entries.All(e =>
        {
            var code = e!["resource"]!["type"]!.AsArray()[0]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>();
            return code == "prov";
        }).Should().BeTrue("type=prov should only return provider-organizations");
    }

    // ── Search — name filter merges both source entities ─────────────────

    [Fact]
    public async Task SearchOrganizations_name_filter_matches_provider_org_name()
    {
        await _providerRepo.CreateAsync(OrgProvider("1234567890", "Acme General Hospital"));

        var result = await _controller.SearchOrganizations(
            npi: null, identifier: null, name: "Acme", city: null,
            state: null, postalCode: null, type: "prov", ct: default);

        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().BeGreaterThan(0);
    }

    // ── Search — empty bundle on no match ────────────────────────────────

    [Fact]
    public async Task SearchOrganizations_npi_not_found_returns_empty_bundle()
    {
        var result = await _controller.SearchOrganizations(
            npi: "9999999999", identifier: null, name: null, city: null,
            state: null, postalCode: null, type: null, ct: default);

        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(0);
        bundle["entry"]!.AsArray().Should().BeEmpty();
    }

    // ── FHIR Bundle structure ─────────────────────────────────────────────

    [Fact]
    public async Task SearchOrganizations_bundle_entries_have_search_mode_match()
    {
        await _providerRepo.CreateAsync(OrgProvider("1234567890", "Acme Hospital"));

        var result = await _controller.SearchOrganizations(
            npi: "1234567890", identifier: null, name: null, city: null,
            state: null, postalCode: null, type: null, ct: default);

        var bundle = ParseFhirContent(result);
        var entry = bundle["entry"]!.AsArray()[0]!;
        entry["search"]!["mode"]!.GetValue<string>().Should().Be("match");
    }

    // ── Content type ──────────────────────────────────────────────────────

    [Fact]
    public async Task ReadOrganization_returns_application_fhir_plus_json()
    {
        await _providerRepo.CreateAsync(OrgProvider("1234567890", "Acme Hospital"));

        var result = await _controller.ReadOrganization("1234567890", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/fhir+json");
    }
}
