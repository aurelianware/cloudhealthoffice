using System.Text.Json.Nodes;
using FluentAssertions;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// G-36 — the Da Vinci PDex Plan-Net 1.2.0 projection behind the public
/// Provider Directory: networks as plannet-Network, individuals as
/// Practitioner + Location + PractitionerRole, organizations as Organization +
/// Location + HealthcareService + OrganizationAffiliation, and the places
/// Plan-Net's required bindings and fixed values differ from the US Core
/// projections.
/// </summary>
public class PlanNetDirectoryProjectorTests
{
    private const string NetworkId = "net-gold";
    private const string PlanNet = "http://hl7.org/fhir/us/davinci-pdex-plan-net/";
    private static readonly DateTime AsOf = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);

    private readonly PlanNetDirectoryProjector _projector = new(
        new FhirPractitionerProjector(), new FhirPractitionerRoleProjector(), new FhirOrganizationProjector());

    private static Organization Network(string id = NetworkId, string? parent = null) => new()
    {
        TenantId = "tenant-a",
        Id = id,
        OrganizationId = id,
        VersionId = id,
        VersionNumber = 1,
        VersionState = OrganizationVersionState.Active,
        Status = OrganizationStatus.Active,
        Name = "Acme Gold Network",
        NetworkType = NetworkType.HMO,
        LineOfBusiness = LineOfBusiness.Commercial,
        ParentOrganizationId = parent,
        EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Identifiers = [new OrganizationIdentifier { System = "urn:cho:network", Value = "NET-001" }],
        ContactInfo = new OrganizationContactInfo { PrimaryContactName = "Network Desk", Phone = "800-555-0001" },
        LastUpdatedDate = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
    };

    private static IReadOnlyDictionary<string, Organization> Networks(params Organization[] networks)
        => networks.ToDictionary(n => n.OrganizationId);

    private static NetworkParticipation Participation(
        string networkId = NetworkId,
        DateTime? effective = null,
        DateTime? termination = null,
        bool accepting = true,
        bool? panelAccepted = null) => new()
    {
        NetworkId = networkId,
        LineOfBusiness = LineOfBusiness.Commercial,
        NetworkTier = "Tier1",
        EffectiveDate = effective ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        TerminationDate = termination,
        AcceptingNewPatients = accepting,
        PanelAccepted = panelAccepted,
    };

    private static Provider Individual(string taxonomy = "207R00000X", params NetworkParticipation[] participations) => new()
    {
        TenantId = "tenant-a",
        Id = "v-1234567893",
        ProviderId = "p-1234567893",
        VersionId = "v-1234567893",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        NPI = "1234567893",
        ProviderType = ProviderType.Individual,
        FirstName = "John",
        LastName = "Smith",
        Credentials = "MD",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = taxonomy,
        SecondarySpecialties = ["Geriatrics"],
        Address = "5 Elm St",
        City = "Hartford",
        State = "CT",
        ZipCode = "06103",
        Phone = "860-555-0100",
        HandicapAccessible = true,
        NetworkParticipations = participations.Length > 0 ? participations.ToList() : [Participation()],
        LastUpdatedDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Provider Hospital(string taxonomy = "282N00000X") => new()
    {
        TenantId = "tenant-a",
        Id = "v-1999999992",
        ProviderId = "p-1999999992",
        VersionId = "v-1999999992",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        NPI = "1999999992",
        ProviderType = ProviderType.Organization,
        OrganizationName = "Hartford General Hospital",
        TaxId = "12-3456789",
        PrimarySpecialty = "Hospital",
        TaxonomyCode = taxonomy,
        Address = "1 Main St",
        City = "Hartford",
        State = "CT",
        ZipCode = "06101",
        Phone = "860-555-0200",
        AcceptingNewPatients = false,
        NetworkParticipations = [Participation()],
        LastUpdatedDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static JsonObject Single(IReadOnlyList<JsonObject> resources, string type)
        => resources.Should().ContainSingle(r => r["resourceType"]!.GetValue<string>() == type).Subject;

    private static string Profile(JsonObject resource) => resource["meta"]!["profile"]!.AsArray().Single()!.GetValue<string>();

    private static JsonObject? Extension(JsonObject resource, string url)
        => (resource["extension"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(e => e["url"]!.GetValue<string>() == url);

    // ── networks ──────────────────────────────────────────────────────────

    [Fact]
    public void A_network_is_a_plan_net_network_managed_by_the_payer()
    {
        var network = _projector.ProjectNetwork(Network(parent: "net-parent"), "payer")!;

        Profile(network).Should().Be(PlanNet + "StructureDefinition/plannet-Network");
        network["type"]![0]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be(PlanNet + "CodeSystem/OrgTypeCS");
        network["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("ntwk");
        network["partOf"]!["reference"]!.GetValue<string>().Should().Be("Organization/payer");
        network.ContainsKey("telecom").Should().BeFalse("Plan-Net Network.telecom is 0..0");
        network["contact"]![0]!["telecom"]!.AsArray().Should().NotBeEmpty("the numbers stay reachable through contact");
        network["active"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void An_inactive_network_is_not_published()
    {
        var network = Network();
        network.Status = OrganizationStatus.Terminated;

        _projector.ProjectNetwork(network, "payer").Should().BeNull();
    }

    // ── individuals ───────────────────────────────────────────────────────

    [Fact]
    public void An_individual_contributes_a_practitioner_a_location_and_a_role_per_network()
    {
        var resources = _projector.ProjectProvider(Individual(), Networks(Network()), AsOf);

        resources.Select(r => r["resourceType"]!.GetValue<string>())
            .Should().BeEquivalentTo("Practitioner", "Location", "PractitionerRole");
        Profile(Single(resources, "Practitioner")).Should().Be(PlanNet + "StructureDefinition/plannet-Practitioner");
    }

    [Fact]
    public void A_role_names_its_network_in_network_reference_not_organization()
    {
        var role = Single(_projector.ProjectProvider(Individual(), Networks(Network()), AsOf), "PractitionerRole");

        role.ContainsKey("organization").Should().BeFalse();
        Extension(role, PlanNet + "StructureDefinition/network-reference")!["valueReference"]!["reference"]!
            .GetValue<string>().Should().Be($"Organization/{NetworkId}");
        role["location"]![0]!["reference"]!.GetValue<string>().Should().Be("Location/1234567893-loc");
        role["active"]!.GetValue<bool>().Should().BeTrue();
        Profile(role).Should().Be(PlanNet + "StructureDefinition/plannet-PractitionerRole");
    }

    [Fact]
    public void A_role_carries_plan_net_codes_only()
    {
        var role = Single(_projector.ProjectProvider(Individual(), Networks(Network()), AsOf), "PractitionerRole");

        role["code"]![0]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be(PlanNet + "CodeSystem/ProviderRoleCS");
        role["code"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("ph");
        role.ToJsonString().Should().NotContain("Tier1", "the network tier has no Plan-Net code");

        var specialty = role["specialty"]!.AsArray().Should().ContainSingle().Subject!;
        specialty["coding"]![0]!["code"]!.GetValue<string>().Should().Be("207R00000X");
        specialty["coding"]![0]!["display"]!.GetValue<string>().Should().Be(PlanNetSpecialties.Display("207R00000X"));
        role.ToJsonString().Should().NotContain("Geriatrics", "a text-only specialty fails Plan-Net's required binding");
    }

    [Fact]
    public void A_taxonomy_outside_the_plan_net_value_set_emits_no_specialty()
    {
        var role = Single(_projector.ProjectProvider(Individual(taxonomy: "999X00000X"), Networks(Network()), AsOf), "PractitionerRole");

        role.ContainsKey("specialty").Should().BeFalse();
        role.ContainsKey("code").Should().BeFalse();
    }

    [Theory]
    [InlineData(true, null, "newpt")]
    [InlineData(false, null, "nopt")]
    [InlineData(true, false, "nopt")]
    [InlineData(false, true, "newpt")]
    public void A_role_says_whether_the_panel_is_open(bool accepting, bool? panelAccepted, string expected)
    {
        var provider = Individual(participations: Participation(accepting: accepting, panelAccepted: panelAccepted));
        var role = Single(_projector.ProjectProvider(provider, Networks(Network()), AsOf), "PractitionerRole");

        var newPatients = Extension(role, PlanNet + "StructureDefinition/newpatients")!;
        var parts = newPatients["extension"]!.AsArray().OfType<JsonObject>().ToDictionary(e => e["url"]!.GetValue<string>());
        parts["acceptingPatients"]["valueCodeableConcept"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be(expected);
        parts["fromNetwork"]["valueReference"]!["reference"]!.GetValue<string>().Should().Be($"Organization/{NetworkId}");
    }

    [Fact]
    public void Roles_outside_their_period_or_network_are_not_published()
    {
        var provider = Individual(participations:
        [
            Participation(termination: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Participation(effective: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Participation(networkId: "net-unpublished"),
        ]);

        _projector.ProjectProvider(provider, Networks(Network()), AsOf)
            .Should().NotContain(r => r["resourceType"]!.GetValue<string>() == "PractitionerRole");
    }

    [Fact]
    public void The_practice_location_is_a_plan_net_location()
    {
        var location = Single(_projector.ProjectProvider(Individual(), Networks(Network()), AsOf), "Location");

        location["id"]!.GetValue<string>().Should().Be("1234567893-loc");
        location["status"]!.GetValue<string>().Should().Be("active");
        location["name"]!.GetValue<string>().Should().Be("John Smith MD");
        location["address"]!["city"]!.GetValue<string>().Should().Be("Hartford");
        Extension(location, PlanNet + "StructureDefinition/accessibility")!["valueCodeableConcept"]!["coding"]![0]!["code"]!
            .GetValue<string>().Should().Be("handiaccess");
        Profile(location).Should().Be(PlanNet + "StructureDefinition/plannet-Location");
    }

    [Fact]
    public void A_provider_without_an_address_has_no_location()
    {
        var provider = Individual();
        provider.Address = provider.City = provider.State = provider.ZipCode = null;

        var resources = _projector.ProjectProvider(provider, Networks(Network()), AsOf);

        resources.Should().NotContain(r => r["resourceType"]!.GetValue<string>() == "Location");
        Single(resources, "PractitionerRole").ContainsKey("location").Should().BeFalse();
    }

    [Fact]
    public void An_inactive_provider_contributes_nothing()
    {
        var provider = Individual();
        provider.Status = ProviderStatus.Terminated;

        _projector.ProjectProvider(provider, Networks(Network()), AsOf).Should().BeEmpty();
    }

    // ── organizations ─────────────────────────────────────────────────────

    [Fact]
    public void An_organization_contributes_organization_location_service_and_affiliation()
    {
        var resources = _projector.ProjectProvider(Hospital(), Networks(Network()), AsOf);

        resources.Select(r => r["resourceType"]!.GetValue<string>()).Should().BeEquivalentTo(
            "Organization", "Location", "HealthcareService", "OrganizationAffiliation");
    }

    [Fact]
    public void A_provider_organization_is_typed_in_plan_net_and_hl7_terms()
    {
        var organization = Single(_projector.ProjectProvider(Hospital(), Networks(Network()), AsOf), "Organization");

        var codings = organization["type"]![0]!["coding"]!.AsArray().Select(c => c!["code"]!.GetValue<string>());
        codings.Should().Equal("fac", "prov");
        Profile(organization).Should().Be(PlanNet + "StructureDefinition/plannet-Organization");
    }

    [Theory]
    [InlineData("282N00000X", "hosp", "hospital", "fac")]
    [InlineData("261QU0200X", "urg", "urgent", "fac")]
    [InlineData("291U00000X", "lab", "laboratory", "fac")]
    [InlineData("333600000X", "pharm", "pharmacy", "fac")]
    [InlineData("193200000X", "group", "group", "prvgrp")]
    [InlineData("341600000X", "trans", "transport", "atyprv")]
    public void An_organization_is_classified_from_its_taxonomy(string taxonomy, string category, string role, string orgType)
    {
        var resources = _projector.ProjectProvider(Hospital(taxonomy), Networks(Network()), AsOf);

        Single(resources, "HealthcareService")["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be(category);
        Single(resources, "OrganizationAffiliation")["code"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be(role);
        Single(resources, "Organization")["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be(orgType);
    }

    [Fact]
    public void The_healthcare_service_is_provided_by_the_organization_at_its_location()
    {
        var service = Single(_projector.ProjectProvider(Hospital(), Networks(Network()), AsOf), "HealthcareService");

        service["id"]!.GetValue<string>().Should().Be("1999999992-svc");
        service["active"]!.GetValue<bool>().Should().BeTrue();
        service["providedBy"]!["reference"]!.GetValue<string>().Should().Be("Organization/1999999992");
        service["location"]![0]!["reference"]!.GetValue<string>().Should().Be("Location/1999999992-loc");
        service["specialty"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("282N00000X");
        Extension(service, PlanNet + "StructureDefinition/newpatients")!.ToJsonString().Should().Contain("nopt");
        Profile(service).Should().Be(PlanNet + "StructureDefinition/plannet-HealthcareService");
    }

    [Fact]
    public void An_affiliation_ties_the_organization_to_the_network()
    {
        var resources = _projector.ProjectProvider(Hospital(), Networks(Network()), AsOf);
        var affiliation = Single(resources, "OrganizationAffiliation");

        affiliation["participatingOrganization"]!["reference"]!.GetValue<string>().Should().Be("Organization/1999999992");
        affiliation["network"]![0]!["reference"]!.GetValue<string>().Should().Be($"Organization/{NetworkId}");
        affiliation["healthcareService"]![0]!["reference"]!.GetValue<string>().Should().Be("HealthcareService/1999999992-svc");
        affiliation["location"]![0]!["reference"]!.GetValue<string>().Should().Be("Location/1999999992-loc");
        affiliation["period"]!["start"]!.GetValue<string>().Should().Be("2024-01-01");
        affiliation["active"]!.GetValue<bool>().Should().BeTrue();
        affiliation.ContainsKey("organization").Should().BeFalse();
        Profile(affiliation).Should().Be(PlanNet + "StructureDefinition/plannet-OrganizationAffiliation");
        Single(resources, "Location")["managingOrganization"]!["reference"]!.GetValue<string>()
            .Should().Be("Organization/1999999992");
    }

    [Fact]
    public void Projection_is_deterministic()
    {
        var first = _projector.ProjectProvider(Hospital(), Networks(Network()), AsOf).Select(r => r.ToJsonString());
        var second = _projector.ProjectProvider(Hospital(), Networks(Network()), AsOf).Select(r => r.ToJsonString());

        first.Should().Equal(second);
    }

    [Theory]
    [InlineData("207R00000X", "ph")]
    [InlineData("363LF0000X", "crnp")]
    [InlineData("363A00000X", "pa")]
    [InlineData("1223G0001X", "de")]
    [InlineData("103T00000X", "py")]
    [InlineData("225100000X", "pt")]
    [InlineData("2255A2300X", "at")]
    [InlineData("163W00000X", "rn")]
    public void Practitioner_roles_follow_the_nucc_grouping(string taxonomy, string expected)
        => PlanNetTaxonomy.PractitionerRole(taxonomy)!.Value.Code.Should().Be(expected);
}
