using System.Text.Json.Nodes;
using FluentAssertions;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Capability 5.8 — FHIR PractitionerRole projection unit tests. Mirrors
/// the fixture style of <see cref="FhirPractitionerProjectorTests"/>:
/// US Core 6.1.0 + Plan-Net IG 1.1.0 structural conformance, edge-case
/// coverage (legacy NetworkId-null returns null, Organization-type
/// providers return null, terminated participation emits period.end and
/// active=false, panel-gating extension only when populated), composite-id
/// round-trip, and a determinism check.
/// </summary>
public class FhirPractitionerRoleProjectorTests
{
    private readonly FhirPractitionerRoleProjector _projector = new();

    private const string DefaultNetworkId = "11111111-1111-1111-1111-111111111111";

    private static Provider BuildProvider(string npi = "1234567890") => new()
    {
        TenantId = "tenant-a",
        Id = "v-1",
        ProviderId = "p-1",
        VersionId = "v-1",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        NPI = npi,
        ProviderType = ProviderType.Individual,
        FirstName = "Jane",
        MiddleName = "Q",
        LastName = "Doe",
        Credentials = "MD",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X",
        SecondarySpecialties = new() { "Cardiology" },
        Phone = "617-555-0100",
        Fax = "617-555-0101",
        Email = "jane.doe@example.com",
        LastUpdatedDate = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
    };

    private static NetworkParticipation BuildParticipation(
        string? networkId = DefaultNetworkId,
        LineOfBusiness lob = LineOfBusiness.Commercial,
        string? networkTier = "Tier1",
        DateTime? effective = null,
        DateTime? termination = null) => new()
        {
            NetworkId = networkId,
            PlanId = "PLAN-1",
            LineOfBusiness = lob,
            NetworkTier = networkTier ?? string.Empty,
            EffectiveDate = effective ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TerminationDate = termination,
            AcceptingNewPatients = true,
        };

    private static Organization BuildOrganization(string? id = null) => new()
    {
        TenantId = "tenant-a",
        Id = id ?? DefaultNetworkId,
        OrganizationId = id ?? DefaultNetworkId,
        Name = "Aetna PPO Florida 2024",
        NetworkType = NetworkType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Status = OrganizationStatus.Active,
        VersionState = OrganizationVersionState.Active,
        VersionNumber = 1,
        VersionId = "vorg-1",
    };

    [Fact]
    public void Projects_minimum_required_elements()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization());

        result.Should().NotBeNull();
        result!["resourceType"]!.GetValue<string>().Should().Be("PractitionerRole");
        result["id"]!.GetValue<string>().Should().StartWith("1234567890-1-20240101-");

        var profiles = result["meta"]!["profile"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        profiles.Should().Contain("http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitionerrole");
        profiles.Should().Contain("http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-PractitionerRole");
    }

    [Fact]
    public void Active_when_period_open_and_provider_active()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        result["active"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Active_false_when_terminated_in_past()
    {
        var participation = BuildParticipation(
            effective: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termination: new DateTime(2020, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        var result = _projector.Project(participation, BuildProvider(), BuildOrganization())!;
        result["active"]!.GetValue<bool>().Should().BeFalse();
        result["period"]!["end"]!.GetValue<string>().Should().Be("2020-12-31");
    }

    [Fact]
    public void Period_emits_start_only_when_no_termination()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        var period = result["period"]!.AsObject();
        period["start"]!.GetValue<string>().Should().Be("2024-01-01");
        period.ContainsKey("end").Should().BeFalse();
    }

    [Fact]
    public void Practitioner_reference_uses_NPI()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        var practitionerRef = result["practitioner"]!.AsObject();
        practitionerRef["reference"]!.GetValue<string>().Should().Be("Practitioner/1234567890");
        practitionerRef["display"]!.GetValue<string>().Should().Contain("Jane");
        practitionerRef["display"]!.GetValue<string>().Should().Contain("Doe");
    }

    [Fact]
    public void Organization_reference_uses_NetworkId_with_display_when_resolved()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        var orgRef = result["organization"]!.AsObject();
        orgRef["reference"]!.GetValue<string>().Should().Be($"Organization/{DefaultNetworkId}");
        orgRef["display"]!.GetValue<string>().Should().Be("Aetna PPO Florida 2024");
    }

    [Fact]
    public void Organization_reference_emits_without_display_when_network_unresolved()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), network: null)!;
        var orgRef = result["organization"]!.AsObject();
        orgRef["reference"]!.GetValue<string>().Should().Be($"Organization/{DefaultNetworkId}");
        orgRef.ContainsKey("display").Should().BeFalse();
    }

    [Fact]
    public void Returns_null_when_participation_has_no_NetworkId()
    {
        var participation = BuildParticipation(networkId: null);
        var result = _projector.Project(participation, BuildProvider(), network: null);
        result.Should().BeNull("legacy participations without NetworkId are invisible to FHIR");
    }

    [Fact]
    public void Returns_null_for_organization_type_provider()
    {
        var provider = BuildProvider();
        provider.ProviderType = ProviderType.Organization;
        var result = _projector.Project(BuildParticipation(), provider, BuildOrganization());
        result.Should().BeNull("Organization-type providers project as FHIR Organization, not PractitionerRole");
    }

    [Fact]
    public void Returns_null_for_non_active_provider_version()
    {
        var provider = BuildProvider();
        provider.VersionState = ProviderVersionState.Suspended;
        var result = _projector.Project(BuildParticipation(), provider, BuildOrganization());
        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_non_active_provider_status()
    {
        var provider = BuildProvider();
        provider.Status = ProviderStatus.Terminated;
        var result = _projector.Project(BuildParticipation(), provider, BuildOrganization());
        result.Should().BeNull();
    }

    [Fact]
    public void Code_emits_NetworkTier_text_only()
    {
        var result = _projector.Project(BuildParticipation(networkTier: "Preferred"), BuildProvider(), BuildOrganization())!;
        var codes = result["code"]!.AsArray();
        codes.Count.Should().Be(1);
        codes[0]!["text"]!.GetValue<string>().Should().Be("Preferred");
    }

    [Fact]
    public void Code_omitted_when_NetworkTier_blank()
    {
        var result = _projector.Project(BuildParticipation(networkTier: string.Empty), BuildProvider(), BuildOrganization())!;
        result.ContainsKey("code").Should().BeFalse();
    }

    [Fact]
    public void Specialty_carries_NUCC_coding_for_primary_and_text_for_secondary()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        var specialty = result["specialty"]!.AsArray();
        specialty.Count.Should().Be(2);

        var primary = specialty[0]!.AsObject();
        var primaryCoding = primary["coding"]!.AsArray()[0]!.AsObject();
        primaryCoding["system"]!.GetValue<string>().Should().Be("http://nucc.org/provider-taxonomy");
        primaryCoding["code"]!.GetValue<string>().Should().Be("207R00000X");
        primaryCoding["display"]!.GetValue<string>().Should().Be("Internal Medicine");

        var secondary = specialty[1]!.AsObject();
        secondary.ContainsKey("coding").Should().BeFalse();
        secondary["text"]!.GetValue<string>().Should().Be("Cardiology");
    }

    [Fact]
    public void Telecom_passes_through_provider_fields_in_order()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        var telecom = result["telecom"]!.AsArray();
        telecom.Count.Should().Be(3);
        telecom[0]!["system"]!.GetValue<string>().Should().Be("phone");
        telecom[1]!["system"]!.GetValue<string>().Should().Be("fax");
        telecom[2]!["system"]!.GetValue<string>().Should().Be("email");
    }

    [Fact]
    public void Panel_gating_extension_omitted_when_all_fields_null()
    {
        var result = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        result.ContainsKey("extension").Should().BeFalse();
    }

    [Fact]
    public void Panel_gating_extension_emits_only_populated_subextensions()
    {
        var participation = BuildParticipation();
        participation.PanelLimit = 250;
        participation.PanelAccepted = true;
        participation.AcceptedLobs = new() { LineOfBusiness.Commercial, LineOfBusiness.Medicare };
        // MinAcceptedAgeYears + MaxAcceptedAgeYears intentionally null

        var result = _projector.Project(participation, BuildProvider(), BuildOrganization())!;
        var extension = result["extension"]!.AsArray()[0]!.AsObject();
        extension["url"]!.GetValue<string>().Should().Be(
            "http://fhir.cloudhealthoffice.com/StructureDefinition/practitionerrole-panel-gating");

        var inner = extension["extension"]!.AsArray();
        var urls = inner.Select(n => n!["url"]!.GetValue<string>()).ToList();
        urls.Should().Contain("panel-limit");
        urls.Should().Contain("panel-accepted");
        urls.Count(u => u == "accepted-lobs").Should().Be(2);
        urls.Should().NotContain("min-accepted-age-years");
        urls.Should().NotContain("max-accepted-age-years");
    }

    [Fact]
    public void Panel_gating_extension_emits_age_bounds_when_set()
    {
        var participation = BuildParticipation();
        participation.MinAcceptedAgeYears = 18;
        participation.MaxAcceptedAgeYears = 65;

        var result = _projector.Project(participation, BuildProvider(), BuildOrganization())!;
        var extension = result["extension"]!.AsArray()[0]!.AsObject();
        var inner = extension["extension"]!.AsArray();

        var min = inner.Single(n => n!["url"]!.GetValue<string>() == "min-accepted-age-years")!.AsObject();
        min["valueInteger"]!.GetValue<int>().Should().Be(18);
        var max = inner.Single(n => n!["url"]!.GetValue<string>() == "max-accepted-age-years")!.AsObject();
        max["valueInteger"]!.GetValue<int>().Should().Be(65);
    }

    [Fact]
    public void Encode_id_and_decode_id_round_trip()
    {
        var participation = BuildParticipation(
            lob: LineOfBusiness.Medicare,
            effective: new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        var encoded = _projector.EncodeId(participation, BuildProvider());

        encoded.Should().Be($"1234567890-2-20240615-{DefaultNetworkId}");

        var decoded = _projector.DecodeId(encoded!);
        decoded.Should().NotBeNull();
        decoded!.Npi.Should().Be("1234567890");
        decoded.LineOfBusiness.Should().Be(LineOfBusiness.Medicare);
        decoded.EffectiveDate.Date.Should().Be(new DateTime(2024, 6, 15));
        decoded.NetworkId.Should().Be(DefaultNetworkId);
    }

    [Fact]
    public void Encode_id_returns_null_when_composite_exceeds_64_chars()
    {
        // 64-char NetworkId pushes the composite over the FHIR id grammar
        // limit. Projector returns null so caller can skip the row.
        var longNetworkId = new string('a', 64);
        var participation = BuildParticipation(networkId: longNetworkId);
        var encoded = _projector.EncodeId(participation, BuildProvider());
        encoded.Should().BeNull();

        var role = _projector.Project(participation, BuildProvider(), network: null);
        role.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-real-id")]
    [InlineData("1234567890-x-20240101-foo")]
    [InlineData("1234567890-1-2024XXXX-foo")]
    [InlineData("123-1-20240101-foo")]
    [InlineData("1234567890-99-20240101-foo")]  // LOB enum out of range
    public void Decode_id_rejects_malformed_inputs(string id)
    {
        _projector.DecodeId(id).Should().BeNull();
    }

    [Fact]
    public void Decode_id_preserves_hyphens_in_network_id()
    {
        var decoded = _projector.DecodeId($"1234567890-1-20240101-{DefaultNetworkId}");
        decoded.Should().NotBeNull();
        decoded!.NetworkId.Should().Be(DefaultNetworkId);
    }

    [Fact]
    public void Projection_is_deterministic_across_calls()
    {
        var first = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        var second = _projector.Project(BuildParticipation(), BuildProvider(), BuildOrganization())!;
        first.ToJsonString().Should().Be(second.ToJsonString());
    }
}
