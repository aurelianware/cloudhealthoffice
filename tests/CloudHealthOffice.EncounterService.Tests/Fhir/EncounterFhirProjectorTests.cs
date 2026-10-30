using System.Text.Json.Nodes;
using EncounterService.Fhir;
using EncounterService.Models;

namespace CloudHealthOffice.EncounterService.Tests.Fhir;

/// <summary>
/// The FHIR R4 Encounter projection (ADR 024). The claims worth pinning are
/// the ones a reader could otherwise get wrong: that <c>status</c> describes
/// the real-world contact rather than the EDI submission lifecycle, and that
/// nothing the record does not hold is invented.
/// </summary>
public class EncounterFhirProjectorTests
{
    private static readonly EncounterFhirProjector Projector = new();

    private static Encounter Encounter() => new()
    {
        Id = "enc-1",
        TenantId = "tenant-1",
        MemberId = "MEM-1",
        EncounterControlNumber = "ECN-1",
        ClaimId = "C-1",
        BillingProviderNPI = "1234567893",
        BillingProviderName = "Example Clinic",
        PayerId = "PAYER-1",
        PlaceOfServiceCode = "11",
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
        TotalChargeAmount = 180m,
    };

    private static JsonObject Project(Encounter encounter) => Projector.Project(encounter);

    [Fact]
    public void The_resource_identifies_the_encounter_and_its_member()
    {
        var resource = Project(Encounter());

        resource["resourceType"]!.GetValue<string>().Should().Be("Encounter");
        resource["id"]!.GetValue<string>().Should().Be("enc-1");
        resource["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/MEM-1");
        resource["identifier"]![0]!["system"]!.GetValue<string>().Should().Be(EncounterFhirProjector.ControlNumberSystem);
        resource["identifier"]![0]!["value"]!.GetValue<string>().Should().Be("ECN-1");
        resource["meta"]!["lastUpdated"]!.GetValue<string>().Should().StartWith("2026-09-18");
    }

    [Theory]
    // The submission lifecycle does not change the fact that the visit happened.
    [InlineData(EncounterStatus.Pending, "finished")]
    [InlineData(EncounterStatus.Queued, "finished")]
    [InlineData(EncounterStatus.Submitted, "finished")]
    [InlineData(EncounterStatus.Rejected, "finished")]
    [InlineData(EncounterStatus.Accepted, "finished")]
    // A void withdraws the assertion that it did.
    [InlineData(EncounterStatus.Voided, "entered-in-error")]
    public void Status_is_the_state_of_the_contact_not_of_the_edi_submission(EncounterStatus stored, string expected)
    {
        var encounter = Encounter();
        encounter.Status = stored;

        Project(encounter)["status"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void A_void_recorded_as_a_submission_type_or_a_frequency_code_is_entered_in_error_too()
    {
        var bySubmissionType = Encounter();
        bySubmissionType.SubmissionType = SubmissionType.Void;

        var byFrequencyCode = Encounter();
        byFrequencyCode.ClaimFrequencyCode = "8";

        Project(bySubmissionType)["status"]!.GetValue<string>().Should().Be("entered-in-error");
        Project(byFrequencyCode)["status"]!.GetValue<string>().Should().Be("entered-in-error");
    }

    [Theory]
    [InlineData("11", "AMB")]
    [InlineData("22", "AMB")]
    [InlineData("21", "IMP")]
    [InlineData("31", "IMP")]
    [InlineData("23", "EMER")]
    [InlineData("12", "HH")]
    // A code with no unambiguous ActCode counterpart is ambulatory, which is
    // what an outpatient professional encounter is.
    [InlineData("99", "AMB")]
    public void Class_comes_from_the_place_of_service(string placeOfService, string expected)
    {
        var encounter = Encounter();
        encounter.PlaceOfServiceCode = placeOfService;

        var resource = Project(encounter);

        resource["class"]!["system"]!.GetValue<string>().Should().Be(EncounterFhirProjector.ActCodeSystem);
        resource["class"]!["code"]!.GetValue<string>().Should().Be(expected);
        // The code it was narrowed from is kept, since class cannot carry it.
        resource["extension"]![0]!["url"]!.GetValue<string>().Should().Be(EncounterFhirProjector.PlaceOfServiceExtension);
        resource["extension"]![0]!["valueCoding"]!["code"]!.GetValue<string>().Should().Be(placeOfService);
    }

    [Fact]
    public void Diagnoses_travel_as_reason_codes_in_pointer_order()
    {
        var encounter = Encounter();
        encounter.DiagnosisCodes =
        [
            new EncounterDiagnosis { Code = "I10", PointerNumber = 2, Description = "Hypertension" },
            new EncounterDiagnosis { Code = "E11.9", PointerNumber = 1 },
        ];

        var reasons = Project(encounter)["reasonCode"]!.AsArray();

        reasons.Should().HaveCount(2);
        reasons[0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("E11.9");
        reasons[0]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be(EncounterFhirProjector.Icd10CmSystem);
        reasons[1]!["coding"]![0]!["display"]!.GetValue<string>().Should().Be("Hypertension");
    }

    [Fact]
    public void A_diagnosis_stored_the_way_the_837_sends_it_is_dotted_for_fhir()
    {
        // Inferno run 40979709678 rejected J189 as an unknown ICD-10-CM code.
        var encounter = Encounter();
        encounter.DiagnosisCodes = [new EncounterDiagnosis { Code = "J189", PointerNumber = 1 }];

        Project(encounter)["reasonCode"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("J18.9");
    }

    [Fact]
    public void The_rendering_provider_is_an_npi_identifier_not_a_reference_that_would_not_resolve()
    {
        var encounter = Encounter();
        encounter.RenderingProviderNPI = "1245319599";

        var individual = Project(encounter)["participant"]![0]!["individual"]!;

        individual["identifier"]!["system"]!.GetValue<string>().Should().Be(EncounterFhirProjector.NpiSystem);
        individual["identifier"]!["value"]!.GetValue<string>().Should().Be("1245319599");
        individual["reference"].Should().BeNull("CHO has no FHIR Practitioner id for a provider on this path");
    }

    [Fact]
    public void Nothing_absent_from_the_record_is_invented()
    {
        // No rendering provider, no diagnoses: the elements that would carry
        // them are absent rather than present and empty.
        var resource = Project(Encounter());

        resource["participant"].Should().BeNull();
        resource["reasonCode"].Should().BeNull();
        resource["location"].Should().BeNull("CHO projects no Location resources");
        resource["diagnosis"].Should().BeNull("that would assert Condition resources CHO does not project");
        resource["meta"]!["profile"].Should().BeNull("no profile is claimed (G-14)");
    }

    [Fact]
    public void The_service_provider_carries_the_billing_npi_and_name()
    {
        var serviceProvider = Project(Encounter())["serviceProvider"]!;

        serviceProvider["identifier"]!["value"]!.GetValue<string>().Should().Be("1234567893");
        serviceProvider["display"]!.GetValue<string>().Should().Be("Example Clinic");
    }

    [Theory]
    [InlineData(EncounterType.Professional, "professional")]
    [InlineData(EncounterType.Institutional, "institutional")]
    [InlineData(EncounterType.Dental, "dental")]
    public void Type_states_the_transaction_variant_under_a_cho_private_system(EncounterType stored, string expected)
    {
        var encounter = Encounter();
        encounter.EncounterType = stored;

        var coding = Project(encounter)["type"]![0]!["coding"]![0]!;

        coding["system"]!.GetValue<string>().Should().Be(EncounterFhirProjector.EncounterTypeSystem);
        coding["code"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void A_local_or_unspecified_service_date_is_projected_as_utc()
    {
        var encounter = Encounter();
        encounter.ServiceDateFrom = new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Unspecified);
        encounter.ServiceDateTo = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Unspecified);

        var period = Project(encounter)["period"]!;

        period["start"]!.GetValue<string>().Should().Be("2026-09-16T08:00:00.0000000Z");
        period["end"]!.GetValue<string>().Should().Be("2026-09-16T09:00:00.0000000Z");
    }
}
