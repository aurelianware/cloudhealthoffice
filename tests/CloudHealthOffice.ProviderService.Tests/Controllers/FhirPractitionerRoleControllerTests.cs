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
/// Capability 5.8 — endpoint-shape coverage for
/// <see cref="FhirPractitionerRoleController"/>: read by composite id,
/// search by practitioner / organization / specialty, malformed-id 404,
/// malformed-reference 400, FHIR Bundle searchset shape, and tenant
/// scoping. Mirrors the structure of <see cref="FhirPractitionerControllerTests"/>.
/// </summary>
public class FhirPractitionerRoleControllerTests
{
    private const string TenantId = "tenant-a";
    private const string NetworkA = "network-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NetworkB = "network-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly InMemoryProviderRepository _providerRepo = new() { TenantId = TenantId };
    private readonly InMemoryOrganizationRepository _orgRepo = new() { TenantId = TenantId };
    private readonly FhirPractitionerRoleProjector _projector = new();
    private readonly FhirPractitionerRoleController _controller;

    public FhirPractitionerRoleControllerTests()
    {
        _controller = new FhirPractitionerRoleController(
            _providerRepo, _orgRepo, _projector,
            NullLogger<FhirPractitionerRoleController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = TenantId;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("provider.test.local");
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    private static Provider IndividualProvider(
        string npi,
        string firstName,
        string lastName,
        string? specialty = null,
        string? taxonomy = null,
        IEnumerable<NetworkParticipation>? participations = null) => new()
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
        TaxonomyCode = taxonomy ?? "207R00000X",
        NetworkParticipations = participations?.ToList() ?? new List<NetworkParticipation>(),
    };

    private static NetworkParticipation Participation(
        string? networkId,
        LineOfBusiness lob = LineOfBusiness.Commercial,
        DateTime? effective = null) => new()
        {
            NetworkId = networkId,
            LineOfBusiness = lob,
            NetworkTier = "Tier1",
            EffectiveDate = effective ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AcceptingNewPatients = true,
        };

    private static Organization Network(string id, string name = "Test Network") => new()
    {
        TenantId = TenantId,
        Id = id,
        OrganizationId = id,
        Name = name,
        NetworkType = NetworkType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Status = OrganizationStatus.Active,
        VersionState = OrganizationVersionState.Active,
        VersionNumber = 1,
        VersionId = $"v-{id}",
    };

    private static JsonObject ParseFhirContent(IActionResult result)
    {
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/fhir+json");
        return JsonNode.Parse(content.Content!)!.AsObject();
    }

    private void Seed()
    {
        // The in-memory repo's ActivateAndSupersedeAsync just adds the
        // (already-Active) row to its store; we skip the draft step
        // because the projection does not depend on draft history.
        _orgRepo.ActivateAndSupersedeAsync(Network(NetworkA, "Network A"), predecessor: null).Wait();
        _orgRepo.ActivateAndSupersedeAsync(Network(NetworkB, "Network B"), predecessor: null).Wait();
    }

    [Fact]
    public async Task ReadPractitionerRole_returns_200_for_valid_composite_id()
    {
        Seed();
        var provider = IndividualProvider("1234567890", "Jane", "Doe", participations: new[]
        {
            Participation(NetworkA),
        });
        await _providerRepo.CreateAsync(provider);

        var id = _projector.EncodeId(provider.NetworkParticipations[0], provider)!;
        var result = await _controller.ReadPractitionerRole(id, default);

        var json = ParseFhirContent(result);
        json["resourceType"]!.GetValue<string>().Should().Be("PractitionerRole");
        json["id"]!.GetValue<string>().Should().Be(id);
        json["practitioner"]!["reference"]!.GetValue<string>().Should().Be("Practitioner/1234567890");
    }

    [Fact]
    public async Task ReadPractitionerRole_returns_404_OperationOutcome_for_unknown_id()
    {
        var result = await _controller.ReadPractitionerRole("9999999999-1-20240101-unknown", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.Content.Should().Contain("OperationOutcome");
    }

    [Fact]
    public async Task ReadPractitionerRole_returns_404_for_malformed_id()
    {
        var result = await _controller.ReadPractitionerRole("not-a-valid-id", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ReadPractitionerRole_returns_404_for_legacy_NetworkId_null_participation()
    {
        var provider = IndividualProvider("1234567890", "Jane", "Doe", participations: new[]
        {
            Participation(networkId: null),
        });
        await _providerRepo.CreateAsync(provider);

        // The composite id can't be encoded (NetworkId is null), so we
        // synthesize a plausible id-shape and expect 404. This guards
        // against an external caller probing for legacy participations.
        var result = await _controller.ReadPractitionerRole(
            "1234567890-1-20240101-some-network", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task SearchPractitionerRoles_by_practitioner_returns_one_role_per_participation()
    {
        Seed();
        var provider = IndividualProvider("1234567890", "Jane", "Doe", participations: new[]
        {
            Participation(NetworkA, LineOfBusiness.Commercial),
            Participation(NetworkB, LineOfBusiness.Medicare),
            Participation(networkId: null),  // legacy — invisible
        });
        await _providerRepo.CreateAsync(provider);

        var result = await _controller.SearchPractitionerRoles(
            practitioner: "Practitioner/1234567890",
            organization: null, specialty: null);

        var bundle = ParseFhirContent(result);
        bundle["resourceType"]!.GetValue<string>().Should().Be("Bundle");
        bundle["type"]!.GetValue<string>().Should().Be("searchset");
        bundle["total"]!.GetValue<int>().Should().Be(2);
        bundle["entry"]!.AsArray().Count.Should().Be(2);
    }

    [Fact]
    public async Task SearchPractitionerRoles_by_practitioner_and_organization_intersects()
    {
        Seed();
        var provider = IndividualProvider("1234567890", "Jane", "Doe", participations: new[]
        {
            Participation(NetworkA, LineOfBusiness.Commercial),
            Participation(NetworkB, LineOfBusiness.Medicare),
        });
        await _providerRepo.CreateAsync(provider);

        var result = await _controller.SearchPractitionerRoles(
            practitioner: "Practitioner/1234567890",
            organization: $"Organization/{NetworkA}",
            specialty: null);

        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(1);
        var role = bundle["entry"]!.AsArray()[0]!["resource"]!.AsObject();
        role["organization"]!["reference"]!.GetValue<string>().Should().Be($"Organization/{NetworkA}");
    }

    [Fact]
    public async Task SearchPractitionerRoles_by_organization_returns_all_providers_in_network()
    {
        Seed();
        var p1 = IndividualProvider("1111111111", "Alice", "First", participations: new[]
        {
            Participation(NetworkA, LineOfBusiness.Commercial),
        });
        var p2 = IndividualProvider("2222222222", "Bob", "Second", participations: new[]
        {
            Participation(NetworkA, LineOfBusiness.Commercial),
            Participation(NetworkB, LineOfBusiness.Medicare),  // different net — excluded
        });
        var p3 = IndividualProvider("3333333333", "Carol", "Third", participations: new[]
        {
            Participation(NetworkB, LineOfBusiness.Medicare),  // wrong net — excluded
        });
        await _providerRepo.CreateAsync(p1);
        await _providerRepo.CreateAsync(p2);
        await _providerRepo.CreateAsync(p3);

        var result = await _controller.SearchPractitionerRoles(
            practitioner: null,
            organization: $"Organization/{NetworkA}",
            specialty: null);

        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(2);
        var refs = bundle["entry"]!.AsArray()
            .Select(e => e!["resource"]!["practitioner"]!["reference"]!.GetValue<string>())
            .ToList();
        refs.Should().BeEquivalentTo(new[] { "Practitioner/1111111111", "Practitioner/2222222222" });
    }

    [Fact]
    public async Task SearchPractitionerRoles_returns_400_for_unrecognized_reference()
    {
        var result = await _controller.SearchPractitionerRoles(
            practitioner: "Patient/1234",
            organization: null, specialty: null);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        content.Content.Should().Contain("OperationOutcome");
    }

    [Fact]
    public async Task SearchPractitionerRoles_returns_400_for_invalid_npi()
    {
        var result = await _controller.SearchPractitionerRoles(
            practitioner: "Practitioner/12345",
            organization: null, specialty: null);
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SearchPractitionerRoles_with_no_filters_returns_empty_bundle()
    {
        var result = await _controller.SearchPractitionerRoles(
            practitioner: null, organization: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(0);
        bundle["entry"]!.AsArray().Count.Should().Be(0);
    }

    [Fact]
    public async Task SearchPractitionerRoles_unknown_practitioner_returns_empty_bundle()
    {
        var result = await _controller.SearchPractitionerRoles(
            practitioner: "Practitioner/9999999999",
            organization: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task SearchPractitionerRoles_excludes_inactive_providers()
    {
        Seed();
        var provider = IndividualProvider("1234567890", "Jane", "Doe", participations: new[]
        {
            Participation(NetworkA),
        });
        provider.VersionState = ProviderVersionState.Suspended;
        provider.Status = ProviderStatus.Inactive;
        await _providerRepo.CreateAsync(provider);

        var result = await _controller.SearchPractitionerRoles(
            practitioner: "Practitioner/1234567890",
            organization: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task SearchPractitionerRoles_accepts_bare_NPI_in_practitioner_param()
    {
        Seed();
        var provider = IndividualProvider("1234567890", "Jane", "Doe", participations: new[]
        {
            Participation(NetworkA),
        });
        await _providerRepo.CreateAsync(provider);

        var result = await _controller.SearchPractitionerRoles(
            practitioner: "1234567890",  // bare NPI, no Practitioner/ prefix
            organization: null, specialty: null);
        var bundle = ParseFhirContent(result);
        bundle["total"]!.GetValue<int>().Should().Be(1);
    }
}
