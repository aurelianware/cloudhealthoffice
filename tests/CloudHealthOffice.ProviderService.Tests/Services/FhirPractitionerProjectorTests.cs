using System.Text.Json.Nodes;
using FluentAssertions;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Capability 5.7 — FHIR Practitioner projection unit tests. Mirrors
/// member-service's <c>FhirPatientProjectorTests</c>: comprehensive
/// fixture, structural conformance assertions for US Core 6.1.0 + Plan-Net
/// IG 1.1.0 required elements, edge coverage (Organization → null,
/// missing optionals, credential parsing, integrity extension presence /
/// absence), and a determinism check.
/// </summary>
public class FhirPractitionerProjectorTests
{
    private readonly FhirPractitionerProjector _projector = new();

    private static Provider BuildIndividualProvider() => new()
    {
        TenantId = "tenant-a",
        Id = "v-1",
        ProviderId = "p-1",
        VersionId = "v-1",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        NPI = "1234567890",
        ProviderType = ProviderType.Individual,
        FirstName = "Jane",
        MiddleName = "Q",
        LastName = "Doe",
        Credentials = "MD, FACP",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X",
        SecondarySpecialties = new() { "Cardiology", "Hypertension" },
        Address = "100 Main St",
        City = "Boston",
        State = "MA",
        ZipCode = "02101",
        Phone = "617-555-0100",
        Fax = "617-555-0101",
        Email = "jane.doe@example.com",
        LanguagesSpoken = new() { "en", "es" },
        BoardCertifications = new()
        {
            new BoardCertification
            {
                Specialty = "Internal Medicine",
                Board = "American Board of Internal Medicine",
                CertificationDate = new DateTime(2018, 6, 1),
                ExpirationDate = new DateTime(2028, 6, 1),
            }
        },
        IntegrityScore = 87,
        IntegrityRating = "Clear",
        LastVerifiedAt = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero),
        LastUpdatedDate = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Projects_minimum_required_elements()
    {
        var result = _projector.Project(BuildIndividualProvider());

        result.Should().NotBeNull();
        result!["resourceType"]!.GetValue<string>().Should().Be("Practitioner");
        result["id"]!.GetValue<string>().Should().Be("1234567890");
        result["active"]!.GetValue<bool>().Should().BeTrue();

        var profiles = result["meta"]!["profile"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        profiles.Should().Contain("http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitioner");
        profiles.Should().Contain("http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Practitioner");
    }

    [Fact]
    public void Identifier_emits_NPI_with_us_npi_system()
    {
        var result = _projector.Project(BuildIndividualProvider())!;
        var identifier = result["identifier"]!.AsArray().Single()!.AsObject();
        identifier["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/sid/us-npi");
        identifier["value"]!.GetValue<string>().Should().Be("1234567890");
        identifier["use"]!.GetValue<string>().Should().Be("official");
    }

    [Fact]
    public void Name_parses_credentials_into_suffix_array()
    {
        var result = _projector.Project(BuildIndividualProvider())!;
        var name = result["name"]!.AsArray().Single()!.AsObject();
        name["family"]!.GetValue<string>().Should().Be("Doe");

        var given = name["given"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        given.Should().BeEquivalentTo(new[] { "Jane", "Q" }, opt => opt.WithStrictOrdering());

        var suffix = name["suffix"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        suffix.Should().BeEquivalentTo(new[] { "MD", "FACP" }, opt => opt.WithStrictOrdering());
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("MD", new[] { "MD" })]
    [InlineData("MD,FACP", new[] { "MD", "FACP" })]
    [InlineData("MD , FACP , FAAFP", new[] { "MD", "FACP", "FAAFP" })]
    [InlineData(",MD,,", new[] { "MD" })]
    public void Credentials_parsing_strips_whitespace_and_drops_empty(string? credentials, string[] expected)
    {
        var provider = BuildIndividualProvider();
        provider.Credentials = credentials;
        var result = _projector.Project(provider)!;
        var name = result["name"]!.AsArray().Single()!.AsObject();
        if (expected.Length == 0)
        {
            name.ContainsKey("suffix").Should().BeFalse();
        }
        else
        {
            var suffix = name["suffix"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            suffix.Should().BeEquivalentTo(expected, opt => opt.WithStrictOrdering());
        }
    }

    [Fact]
    public void Name_omits_given_when_first_and_middle_are_empty()
    {
        var provider = BuildIndividualProvider();
        provider.FirstName = null;
        provider.MiddleName = null;
        var result = _projector.Project(provider)!;
        var name = result["name"]!.AsArray().Single()!.AsObject();
        name.ContainsKey("given").Should().BeFalse();
        name["family"]!.GetValue<string>().Should().Be("Doe");
    }

    [Fact]
    public void Gender_is_never_emitted()
    {
        // Capability 5.7 Premise A: Provider has no Gender field today;
        // US Core 6.1.0 Practitioner.gender 0..1 — omission is conformant.
        // 5.17 adds the field; until then the projector MUST NOT emit it
        // (consumers should treat absence as "unknown", not "unrepresented").
        var result = _projector.Project(BuildIndividualProvider())!;
        result.ContainsKey("gender").Should().BeFalse();
    }

    [Fact]
    public void Telecom_includes_phone_email_fax_in_canonical_order()
    {
        var result = _projector.Project(BuildIndividualProvider())!;
        var telecom = result["telecom"]!.AsArray()
            .Select(n => n!.AsObject())
            .Select(o => (System: o["system"]!.GetValue<string>(), Value: o["value"]!.GetValue<string>()))
            .ToList();
        telecom.Should().HaveCount(3);
        telecom[0].System.Should().Be("phone");
        telecom[1].System.Should().Be("fax");
        telecom[2].System.Should().Be("email");
    }

    [Fact]
    public void Telecom_omits_missing_entries()
    {
        var provider = BuildIndividualProvider();
        provider.Phone = null;
        provider.Fax = null;
        provider.Email = null;
        var result = _projector.Project(provider)!;
        result.ContainsKey("telecom").Should().BeFalse();
    }

    [Fact]
    public void Address_emits_when_any_component_present()
    {
        var result = _projector.Project(BuildIndividualProvider())!;
        var address = result["address"]!.AsArray().Single()!.AsObject();
        address["line"]!.AsArray().Single()!.GetValue<string>().Should().Be("100 Main St");
        address["city"]!.GetValue<string>().Should().Be("Boston");
        address["state"]!.GetValue<string>().Should().Be("MA");
        address["postalCode"]!.GetValue<string>().Should().Be("02101");
    }

    [Fact]
    public void Address_omitted_when_all_components_null()
    {
        var provider = BuildIndividualProvider();
        provider.Address = null; provider.City = null; provider.State = null; provider.ZipCode = null;
        var result = _projector.Project(provider)!;
        result.ContainsKey("address").Should().BeFalse();
    }

    [Fact]
    public void Qualification_primary_specialty_carries_full_NUCC_coding()
    {
        var result = _projector.Project(BuildIndividualProvider())!;
        var qualifications = result["qualification"]!.AsArray();
        var primary = qualifications[0]!.AsObject()["code"]!.AsObject();

        var coding = primary["coding"]!.AsArray().Single()!.AsObject();
        coding["system"]!.GetValue<string>().Should().Be("http://nucc.org/provider-taxonomy");
        coding["code"]!.GetValue<string>().Should().Be("207R00000X");
        coding["display"]!.GetValue<string>().Should().Be("Internal Medicine");
        primary["text"]!.GetValue<string>().Should().Be("Internal Medicine");
    }

    [Fact]
    public void Qualification_secondary_specialties_emit_text_only()
    {
        // Capability 5.7 Premise B: Provider has no parallel taxonomy-code
        // list for SecondarySpecialties. Text-only CodeableConcept is the
        // ratified shape. A 'coding' array on these entries would be wrong.
        var result = _projector.Project(BuildIndividualProvider())!;
        var qualifications = result["qualification"]!.AsArray();

        var cardiology = qualifications[1]!.AsObject()["code"]!.AsObject();
        cardiology.ContainsKey("coding").Should().BeFalse();
        cardiology["text"]!.GetValue<string>().Should().Be("Cardiology");

        var hypertension = qualifications[2]!.AsObject()["code"]!.AsObject();
        hypertension.ContainsKey("coding").Should().BeFalse();
        hypertension["text"]!.GetValue<string>().Should().Be("Hypertension");
    }

    [Fact]
    public void Qualification_includes_board_certification_with_period()
    {
        var result = _projector.Project(BuildIndividualProvider())!;
        var qualifications = result["qualification"]!.AsArray();
        var cert = qualifications.Last()!.AsObject();

        var coding = cert["code"]!["coding"]!.AsArray().Single()!.AsObject();
        coding["system"]!.GetValue<string>().Should().Be("http://terminology.hl7.org/CodeSystem/v2-0360");
        coding["code"]!.GetValue<string>().Should().Be("BC");
        coding["display"]!.GetValue<string>().Should().Be("Board Certified");
        cert["code"]!["text"]!.GetValue<string>()
            .Should().Be("Internal Medicine (American Board of Internal Medicine)");
        cert["period"]!["start"]!.GetValue<string>().Should().Be("2018-06-01");
        cert["period"]!["end"]!.GetValue<string>().Should().Be("2028-06-01");
        cert["issuer"]!["display"]!.GetValue<string>().Should().Be("American Board of Internal Medicine");
    }

    [Fact]
    public void Communication_maps_languages_to_BCP47_with_display_names()
    {
        var result = _projector.Project(BuildIndividualProvider())!;
        var communication = result["communication"]!.AsArray()
            .Select(n => n!.AsObject())
            .Select(o => (
                Code: o["coding"]!.AsArray().Single()!["code"]!.GetValue<string>(),
                Display: o["coding"]!.AsArray().Single()!["display"]!.GetValue<string>(),
                Text: o["text"]!.GetValue<string>()))
            .ToList();
        communication.Should().Contain(("en", "English", "English"));
        communication.Should().Contain(("es", "Spanish", "Spanish"));
    }

    [Fact]
    public void Communication_omitted_when_no_languages()
    {
        var provider = BuildIndividualProvider();
        provider.LanguagesSpoken = new();
        var result = _projector.Project(provider)!;
        result.ContainsKey("communication").Should().BeFalse();
    }

    [Fact]
    public void IntegrityScore_extension_emitted_when_score_present()
    {
        var integrity = ProviderIntegrityProjection.FromProvider(BuildIndividualProvider());
        var result = _projector.Project(BuildIndividualProvider(), integrity)!;
        var ext = result["extension"]!.AsArray().Single()!.AsObject();
        ext["url"]!.GetValue<string>()
            .Should().Be("http://fhir.cloudhealthoffice.com/StructureDefinition/provider-integrity-score");

        var inner = ext["extension"]!.AsArray()
            .Select(n => n!.AsObject())
            .ToList();
        inner.Single(e => e["url"]!.GetValue<string>() == "score")
             ["valueInteger"]!.GetValue<int>().Should().Be(87);
        inner.Single(e => e["url"]!.GetValue<string>() == "rating")
             ["valueString"]!.GetValue<string>().Should().Be("Clear");
        inner.Single(e => e["url"]!.GetValue<string>() == "lastVerifiedAt")
             ["valueDateTime"]!.GetValue<string>().Should().StartWith("2026-04-22");
    }

    [Fact]
    public void IntegrityScore_extension_omitted_when_score_null()
    {
        var provider = BuildIndividualProvider();
        provider.IntegrityScore = null;
        provider.IntegrityRating = null;
        provider.LastVerifiedAt = null;

        var integrity = ProviderIntegrityProjection.FromProvider(provider);
        integrity.Should().BeNull();

        var result = _projector.Project(provider, integrity)!;
        result.ContainsKey("extension").Should().BeFalse();
    }

    [Fact]
    public void Returns_null_for_organization_provider()
    {
        var provider = BuildIndividualProvider();
        provider.ProviderType = ProviderType.Organization;
        provider.OrganizationName = "Acme Hospital";
        _projector.Project(provider).Should().BeNull();
    }

    [Theory]
    [InlineData(ProviderVersionState.Active, ProviderStatus.Active, true)]
    [InlineData(ProviderVersionState.Suspended, ProviderStatus.Inactive, false)]
    [InlineData(ProviderVersionState.Terminated, ProviderStatus.Terminated, false)]
    [InlineData(ProviderVersionState.Superseded, ProviderStatus.Inactive, false)]
    [InlineData(ProviderVersionState.Draft, ProviderStatus.Pending, false)]
    public void Active_flag_reflects_VersionState_and_Status(
        ProviderVersionState versionState, ProviderStatus status, bool expectedActive)
    {
        var provider = BuildIndividualProvider();
        provider.VersionState = versionState;
        provider.Status = status;
        var result = _projector.Project(provider);
        result!["active"]!.GetValue<bool>().Should().Be(expectedActive);
    }

    [Fact]
    public void Projection_is_deterministic_byte_for_byte()
    {
        var provider = BuildIndividualProvider();
        var integrity = ProviderIntegrityProjection.FromProvider(provider);
        var first = _projector.Project(provider, integrity)!.ToJsonString();
        var second = _projector.Project(BuildIndividualProvider(), ProviderIntegrityProjection.FromProvider(BuildIndividualProvider()))!.ToJsonString();
        second.Should().Be(first);
    }
}
