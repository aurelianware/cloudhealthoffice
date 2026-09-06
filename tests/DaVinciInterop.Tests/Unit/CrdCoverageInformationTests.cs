using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// A CRD server states its determination in the coverage-information extension on
/// a system action, not in cards. Misreading it — or defaulting an absent field —
/// would turn "the payer said nothing" into a confident claim about coverage.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class CrdCoverageInformationTests
{
    private static CdsHooksResponse ResponseWith(string extensionJson) =>
        CdsHooksResponse.Parse(
            """{"cards":[],"systemActions":[{"type":"update","resource":{"resourceType":"DeviceRequest","id":"o1","extension":["""
            + extensionJson
            + """]}}]}""")!;

    private const string PriorAuthExtension = """
    {
      "url": "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information",
      "extension": [
        { "url": "coverage", "valueReference": { "reference": "Coverage/interop-coverage-001" } },
        { "url": "covered", "valueCode": "covered" },
        { "url": "pa-needed", "valueCode": "auth-needed" },
        { "url": "doc-needed", "valueCode": "no-doc" },
        { "url": "questionnaire", "valueCanonical": "http://example.org/fhir/Questionnaire/PriorAuthRequired" },
        { "url": "billingCode", "valueCoding": { "system": "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "code": "L8000" } },
        { "url": "date", "valueDate": "2026-09-06" },
        { "url": "coverage-assertion-id", "valueString": "prior-auth-2026-09-06" }
      ]
    }
    """;

    [Fact]
    public void A_prior_authorization_determination_is_parsed_in_full()
    {
        var info = CrdCoverageInformation.FromSystemActions(ResponseWith(PriorAuthExtension)).Single();

        info.Covered.Should().Be("covered");
        info.PaNeeded.Should().Be("auth-needed");
        info.DocNeeded.Should().Be("no-doc");
        info.CoverageReference.Should().Be("Coverage/interop-coverage-001");
        info.BillingCode.Should().Be("L8000");
        info.BillingCodeSystem.Should().Contain("HCPCS");
        info.QuestionnaireCanonical.Should().EndWith("Questionnaire/PriorAuthRequired");
        info.CoverageAssertionId.Should().Be("prior-auth-2026-09-06");
        info.IsPriorAuthRequired.Should().BeTrue();
        info.IsNotCovered.Should().BeFalse();
    }

    [Fact]
    public void A_not_covered_determination_is_distinguished_from_prior_authorization()
    {
        var info = CrdCoverageInformation.FromSystemActions(ResponseWith("""
        { "url": "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information",
          "extension": [ { "url": "covered", "valueCode": "not-covered" } ] }
        """)).Single();

        info.IsNotCovered.Should().BeTrue();
        info.IsPriorAuthRequired.Should().BeFalse();
    }

    [Fact]
    public void An_absent_pa_needed_is_null_rather_than_assumed_to_mean_no_authorization()
    {
        var info = CrdCoverageInformation.FromSystemActions(ResponseWith("""
        { "url": "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information",
          "extension": [ { "url": "covered", "valueCode": "conditional" } ] }
        """)).Single();

        info.PaNeeded.Should().BeNull(
            "the payer said nothing about prior authorization, which is not the same as saying none is needed");
        info.IsPriorAuthRequired.Should().BeFalse();
    }

    [Fact]
    public void A_determination_carried_as_a_codeable_concept_is_read_too()
    {
        var info = CrdCoverageInformation.FromSystemActions(ResponseWith("""
        { "url": "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information",
          "extension": [ { "url": "covered",
            "valueCodeableConcept": { "coding": [ { "system": "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp", "code": "covered" } ] } } ] }
        """)).Single();

        info.Covered.Should().Be("covered",
            "servers differ on code vs CodeableConcept; both are read rather than one being treated as absent");
    }

    [Fact]
    public void Extensions_that_are_not_coverage_information_are_ignored()
    {
        CrdCoverageInformation.FromSystemActions(ResponseWith("""
        { "url": "http://example.org/some-other-extension", "extension": [ { "url": "covered", "valueCode": "covered" } ] }
        """)).Should().BeEmpty();
    }

    [Fact]
    public void A_response_with_no_system_actions_yields_no_determination()
    {
        CrdCoverageInformation.FromSystemActions(CdsHooksResponse.Parse("""{"cards":[]}""")!)
            .Should().BeEmpty("no determination is not the same as a negative determination");
    }

    [Fact]
    public void Present_fields_are_recorded_so_an_unexpected_one_stays_visible()
    {
        var info = CrdCoverageInformation.FromSystemActions(ResponseWith(PriorAuthExtension)).Single();

        info.PresentFields.Should().Contain(["coverage", "covered", "pa-needed", "billingCode"]);
    }

    [Fact]
    public void The_safe_summary_carries_determinations_and_no_patient_detail()
    {
        var summary = CrdCoverageInformation.FromSystemActions(ResponseWith(PriorAuthExtension)).Single().SafeSummary();

        summary.Should().Contain("covered=covered").And.Contain("pa-needed=auth-needed").And.Contain("L8000");
        summary.Should().NotContain("Patient").And.NotContain("interop-member-001");
    }

    [Fact]
    public void The_extension_canonical_is_the_unversioned_crd_url()
    {
        CrdCoverageInformation.ExtensionUrl
            .Should().Be("http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information");
    }
}
