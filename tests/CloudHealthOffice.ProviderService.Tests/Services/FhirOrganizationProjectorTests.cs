using System.Text.Json.Nodes;
using FluentAssertions;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Capability 5.9 — FHIR Organization projection unit tests. Covers both
/// source-entity types, US Core 6.1.0 + Plan-Net IG 1.1.0 structural
/// conformance, edge cases (Individual provider → null, non-Active version
/// → null, partOf hierarchy emission, DBA-as-alias), and a determinism
/// check.
/// </summary>
public class FhirOrganizationProjectorTests
{
    private readonly FhirOrganizationProjector _projector = new();

    // ── Test-fixture builders ─────────────────────────────────────────────

    private static Organization BuildNetwork(
        string id = "aaaa-network-id",
        string name = "Aetna Open Access HMO Florida 2025",
        OrganizationVersionState versionState = OrganizationVersionState.Active,
        string? parentId = null) => new()
        {
            TenantId = "tenant-a",
            Id = id,
            OrganizationId = id,
            VersionId = id,
            VersionNumber = 1,
            VersionState = versionState,
            Status = OrganizationStatus.Active,
            Name = name,
            NetworkType = NetworkType.HMO,
            LineOfBusiness = LineOfBusiness.Commercial,
            ParentOrganizationId = parentId,
            EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Identifiers = new()
            {
                new OrganizationIdentifier
                {
                    System = "urn:cho:network",
                    Value = "NET-001",
                    Type = "NIIP",
                    Use = "official",
                }
            },
            ContactInfo = new OrganizationContactInfo
            {
                PrimaryContactName = "Network Admin",
                Phone = "800-555-0001",
                Fax = "800-555-0002",
                Email = "admin@aetna.example.com",
                Address = "1 Aetna Place",
                City = "Hartford",
                State = "CT",
                ZipCode = "06156",
            },
            LastUpdatedDate = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
        };

    private static Provider BuildOrgProvider(
        string npi = "1234567890",
        string orgName = "Acme General Hospital",
        string? dbaName = null,
        ProviderVersionState versionState = ProviderVersionState.Active,
        ProviderStatus status = ProviderStatus.Active) => new()
        {
            TenantId = "tenant-a",
            Id = $"v-{npi}",
            ProviderId = $"p-{npi}",
            VersionId = $"v-{npi}",
            VersionNumber = 1,
            VersionState = versionState,
            Status = status,
            NPI = npi,
            ProviderType = ProviderType.Organization,
            OrganizationName = orgName,
            DBAName = dbaName,
            TaxId = "12-3456789",
            PrimarySpecialty = "Hospital",
            TaxonomyCode = "282N00000X",
            Address = "100 Hospital Drive",
            City = "Boston",
            State = "MA",
            ZipCode = "02101",
            Phone = "617-555-9000",
            Fax = "617-555-9001",
            Email = "info@acme.example.com",
            LastUpdatedDate = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
        };

    // ── Network (Organization entity → type=ins) tests ────────────────────

    [Fact]
    public void Network_Projects_minimum_required_elements()
    {
        var result = _projector.Project(BuildNetwork());

        result.Should().NotBeNull();
        result!["resourceType"]!.GetValue<string>().Should().Be("Organization");
        result["active"]!.GetValue<bool>().Should().BeTrue();

        var typeArr = result["type"]!.AsArray();
        typeArr.Should().HaveCount(1);
        var coding = typeArr[0]!["coding"]!.AsArray()[0]!;
        coding["system"]!.GetValue<string>().Should().Be("http://terminology.hl7.org/CodeSystem/organization-type");
        coding["code"]!.GetValue<string>().Should().Be("ins");

        result["name"]!.GetValue<string>().Should().Be("Aetna Open Access HMO Florida 2025");
    }

    [Fact]
    public void Network_Id_is_OrganizationId_chain_key()
    {
        var result = _projector.Project(BuildNetwork(id: "my-org-chain-key"));

        result!["id"]!.GetValue<string>().Should().Be("my-org-chain-key");
    }

    [Fact]
    public void Network_Emits_meta_with_both_profiles()
    {
        var result = _projector.Project(BuildNetwork());

        var profiles = result!["meta"]!["profile"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        profiles.Should().Contain("http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization");
        profiles.Should().Contain("http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Organization");
    }

    [Fact]
    public void Network_Emits_identifiers_from_Identifiers_list()
    {
        var result = _projector.Project(BuildNetwork());

        var identifiers = result!["identifier"]!.AsArray();
        identifiers.Should().HaveCount(1);
        var id = identifiers[0]!;
        id["system"]!.GetValue<string>().Should().Be("urn:cho:network");
        id["value"]!.GetValue<string>().Should().Be("NET-001");
        id["use"]!.GetValue<string>().Should().Be("official");
    }

    [Fact]
    public void Network_Emits_telecom_from_ContactInfo()
    {
        var result = _projector.Project(BuildNetwork());

        var telecom = result!["telecom"]!.AsArray();
        telecom.Should().NotBeEmpty();
        var phone = telecom.FirstOrDefault(t => t!["system"]!.GetValue<string>() == "phone");
        phone.Should().NotBeNull();
        phone!["value"]!.GetValue<string>().Should().Be("800-555-0001");
    }

    [Fact]
    public void Network_Emits_address_from_ContactInfo()
    {
        var result = _projector.Project(BuildNetwork());

        var addressArr = result!["address"]!.AsArray();
        addressArr.Should().HaveCount(1);
        var address = addressArr[0]!;
        address["city"]!.GetValue<string>().Should().Be("Hartford");
        address["state"]!.GetValue<string>().Should().Be("CT");
        address["postalCode"]!.GetValue<string>().Should().Be("06156");
    }

    [Fact]
    public void Network_Emits_contact_with_primary_contact_name()
    {
        var result = _projector.Project(BuildNetwork());

        var contactArr = result!["contact"]!.AsArray();
        contactArr.Should().HaveCount(1);
        var contact = contactArr[0]!;
        contact["name"]!["text"]!.GetValue<string>().Should().Be("Network Admin");
    }

    [Fact]
    public void Network_Emits_partOf_when_ParentOrganizationId_set()
    {
        var result = _projector.Project(BuildNetwork(parentId: "parent-org-id"));

        result!["partOf"]!["reference"]!.GetValue<string>()
            .Should().Be("Organization/parent-org-id");
    }

    [Fact]
    public void Network_No_partOf_when_ParentOrganizationId_null()
    {
        var result = _projector.Project(BuildNetwork(parentId: null));

        result!.ContainsKey("partOf").Should().BeFalse();
    }

    [Fact]
    public void Network_Returns_null_for_non_Active_version_state()
    {
        foreach (var state in new[]
        {
            OrganizationVersionState.Draft,
            OrganizationVersionState.Suspended,
            OrganizationVersionState.Superseded,
            OrganizationVersionState.Terminated,
        })
        {
            var result = _projector.Project(BuildNetwork(versionState: state));
            result.Should().BeNull($"VersionState={state} should not project");
        }
    }

    [Fact]
    public void Network_Returns_null_when_Name_is_empty()
    {
        var network = BuildNetwork(name: "");
        var result = _projector.Project(network);
        result.Should().BeNull();
    }

    [Fact]
    public void Network_Returns_null_when_Identifiers_list_empty_or_all_invalid()
    {
        // US Core Organization requires identifier (1..*). A network with no
        // projectable identifiers cannot be emitted conformantly; the projector
        // returns null so callers map this to 404 / skip-in-search.
        var network = BuildNetwork();
        network.Identifiers.Clear();
        var result = _projector.Project(network);
        result.Should().BeNull("US Core requires at least one identifier; none available → return null");
    }

    [Fact]
    public void Network_No_telecom_emitted_when_ContactInfo_null()
    {
        var network = BuildNetwork();
        network.ContactInfo = null;
        var result = _projector.Project(network);

        result!.ContainsKey("telecom").Should().BeFalse();
        result.ContainsKey("address").Should().BeFalse();
    }

    // ── Provider-as-Org (Provider with ProviderType=Organization → type=prov) ──

    [Fact]
    public void ProviderAsOrg_Projects_minimum_required_elements()
    {
        var result = _projector.Project(BuildOrgProvider());

        result.Should().NotBeNull();
        result!["resourceType"]!.GetValue<string>().Should().Be("Organization");
        result["active"]!.GetValue<bool>().Should().BeTrue();

        var typeArr = result["type"]!.AsArray();
        var coding = typeArr[0]!["coding"]!.AsArray()[0]!;
        coding["code"]!.GetValue<string>().Should().Be("prov");

        result["name"]!.GetValue<string>().Should().Be("Acme General Hospital");
    }

    [Fact]
    public void ProviderAsOrg_Id_is_NPI()
    {
        var result = _projector.Project(BuildOrgProvider(npi: "9876543210"));

        result!["id"]!.GetValue<string>().Should().Be("9876543210");
    }

    [Fact]
    public void ProviderAsOrg_Emits_NPI_and_TaxId_identifiers()
    {
        var result = _projector.Project(BuildOrgProvider());

        var identifiers = result!["identifier"]!.AsArray();
        identifiers.Should().HaveCountGreaterThanOrEqualTo(2);

        var npiId = identifiers.FirstOrDefault(i => i!["system"]!.GetValue<string>() == "http://hl7.org/fhir/sid/us-npi");
        npiId.Should().NotBeNull();
        npiId!["value"]!.GetValue<string>().Should().Be("1234567890");

        var einId = identifiers.FirstOrDefault(i => i!["system"]!.GetValue<string>() == "urn:oid:2.16.840.1.113883.4.4");
        einId.Should().NotBeNull();
        einId!["value"]!.GetValue<string>().Should().Be("12-3456789");
    }

    [Fact]
    public void ProviderAsOrg_TaxId_omitted_when_null()
    {
        var provider = BuildOrgProvider();
        provider.TaxId = null;
        var result = _projector.Project(provider);

        var identifiers = result!["identifier"]!.AsArray();
        var einId = identifiers.FirstOrDefault(i => i!["system"]!.GetValue<string>() == "urn:oid:2.16.840.1.113883.4.4");
        einId.Should().BeNull();
    }

    [Fact]
    public void ProviderAsOrg_DBAName_emitted_as_alias()
    {
        var result = _projector.Project(BuildOrgProvider(dbaName: "Acme Hospital (DBA)"));

        var alias = result!["alias"]!.AsArray();
        alias.Should().HaveCount(1);
        alias[0]!.GetValue<string>().Should().Be("Acme Hospital (DBA)");
    }

    [Fact]
    public void ProviderAsOrg_No_alias_when_DBAName_null()
    {
        var result = _projector.Project(BuildOrgProvider(dbaName: null));

        result!.ContainsKey("alias").Should().BeFalse();
    }

    [Fact]
    public void ProviderAsOrg_Emits_telecom_from_Provider_fields()
    {
        var result = _projector.Project(BuildOrgProvider());

        var telecom = result!["telecom"]!.AsArray();
        telecom.Should().NotBeEmpty();
        var phone = telecom.FirstOrDefault(t => t!["system"]!.GetValue<string>() == "phone");
        phone!["value"]!.GetValue<string>().Should().Be("617-555-9000");
    }

    [Fact]
    public void ProviderAsOrg_Emits_address_from_Provider_fields()
    {
        var result = _projector.Project(BuildOrgProvider());

        var addressArr = result!["address"]!.AsArray();
        addressArr.Should().HaveCount(1);
        var address = addressArr[0]!;
        address["city"]!.GetValue<string>().Should().Be("Boston");
        address["state"]!.GetValue<string>().Should().Be("MA");
        address["postalCode"]!.GetValue<string>().Should().Be("02101");
    }

    [Fact]
    public void ProviderAsOrg_Emits_meta_with_both_profiles()
    {
        var result = _projector.Project(BuildOrgProvider());

        var profiles = result!["meta"]!["profile"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        profiles.Should().Contain("http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization");
        profiles.Should().Contain("http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Organization");
    }

    [Fact]
    public void ProviderAsOrg_Returns_null_for_Individual_provider()
    {
        var provider = BuildOrgProvider();
        provider.ProviderType = ProviderType.Individual;
        var result = _projector.Project(provider);
        result.Should().BeNull();
    }

    [Fact]
    public void ProviderAsOrg_Returns_null_for_non_Active_version_state()
    {
        foreach (var state in new[]
        {
            ProviderVersionState.Draft,
            ProviderVersionState.Suspended,
            ProviderVersionState.Superseded,
            ProviderVersionState.Terminated,
        })
        {
            var provider = BuildOrgProvider(versionState: state);
            var result = _projector.Project(provider);
            result.Should().BeNull($"VersionState={state} should not project");
        }
    }

    [Fact]
    public void ProviderAsOrg_Returns_null_when_OrganizationName_empty()
    {
        var provider = BuildOrgProvider(orgName: "");
        var result = _projector.Project(provider);
        result.Should().BeNull();
    }

    [Fact]
    public void ProviderAsOrg_No_address_emitted_when_all_address_fields_null()
    {
        var provider = BuildOrgProvider();
        provider.Address = null;
        provider.City = null;
        provider.State = null;
        provider.ZipCode = null;
        var result = _projector.Project(provider);

        result!.ContainsKey("address").Should().BeFalse();
    }

    // ── Determinism ────────────────────────────────────────────────────────

    [Fact]
    public void Network_Projection_is_byte_deterministic()
    {
        var network = BuildNetwork();
        var r1 = _projector.Project(network)!.ToJsonString();
        var r2 = _projector.Project(network)!.ToJsonString();
        r1.Should().Be(r2);
    }

    [Fact]
    public void ProviderAsOrg_Projection_is_byte_deterministic()
    {
        var provider = BuildOrgProvider();
        var r1 = _projector.Project(provider)!.ToJsonString();
        var r2 = _projector.Project(provider)!.ToJsonString();
        r1.Should().Be(r2);
    }

    // ── US Core 6.1.0 conformance assertions ──────────────────────────────

    [Fact]
    public void Network_Conforms_to_USCore_Organization_required_elements()
    {
        var result = _projector.Project(BuildNetwork())!;

        // US Core 6.1.0 Organization: identifier (1..*), active (1..1),
        // type (1..*), name (1..1) are required.
        result["identifier"].Should().NotBeNull("identifier is required by US Core 6.1.0");
        result["active"].Should().NotBeNull("active is required by US Core 6.1.0");
        result["type"].Should().NotBeNull("type is required by US Core 6.1.0");
        result["name"].Should().NotBeNull("name is required by US Core 6.1.0");
    }

    [Fact]
    public void ProviderAsOrg_Conforms_to_USCore_Organization_required_elements()
    {
        var result = _projector.Project(BuildOrgProvider())!;

        result["identifier"].Should().NotBeNull("identifier is required by US Core 6.1.0");
        result["active"].Should().NotBeNull("active is required by US Core 6.1.0");
        result["type"].Should().NotBeNull("type is required by US Core 6.1.0");
        result["name"].Should().NotBeNull("name is required by US Core 6.1.0");
    }
}
