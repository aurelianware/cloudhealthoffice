using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.TestSupport;

/// <summary>
/// A complete Da Vinci PAS request Bundle: the Claim plus the Patient,
/// Coverage and requesting Practitioner it references, as the PAS IG
/// requires. Tests that need a gap (no Patient, no NPI, ...) build the full
/// bundle and remove or change the part they are about.
/// </summary>
internal static class PasBundles
{
    public const string MemberId = "MBR-778899";
    public const string PatientId = "pat-001";
    public const string CoverageId = "cov-001";
    public const string PractitionerId = "prac-001";

    /// <summary>A valid example NPI (passes the NPI check digit).</summary>
    public const string RequestingNpi = "1234567893";

    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string CptSystem = "http://www.ama-assn.org/go/cpt";
    public const string X12ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";
    public const string X12ClaimFilingSystem = "https://codesystem.x12.org/005010/1032";
    public const string ProcessPrioritySystem = "http://terminology.hl7.org/CodeSystem/processpriority";

    public static Bundle Complete(
        string procedureCode = "27447",
        string? priority = "normal",
        string servicedStart = "2026-11-02",
        string? servicedEnd = "2026-11-04",
        string coverageTypeCode = "MC",
        string? serviceTypeCode = "2",
        string? serviceState = null)
    {
        var patient = new Patient
        {
            Id = PatientId,
            Identifier =
            [
                new Identifier("http://example.org/payer/member-id", MemberId)
                {
                    Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MB"),
                },
            ],
            Name = [new HumanName { Family = "Rivera", Given = ["Ana"] }],
            BirthDate = "1984-03-07",
            Gender = AdministrativeGender.Female,
        };

        var coverage = new Coverage
        {
            Id = CoverageId,
            Status = FinancialResourceStatusCodes.Active,
            Beneficiary = new ResourceReference($"Patient/{PatientId}"),
            SubscriberId = MemberId,
            Payor = [new ResourceReference("Organization/cho-payer")],
            Type = new CodeableConcept(X12ClaimFilingSystem, coverageTypeCode),
        };

        var practitioner = new Practitioner
        {
            Id = PractitionerId,
            Identifier = [new Identifier(NpiSystem, RequestingNpi)],
            Name = [new HumanName { Family = "Lee", Given = ["Jordan"] }],
        };

        var item = new Claim.ItemComponent
        {
            Sequence = 1,
            ProductOrService = new CodeableConcept(CptSystem, procedureCode),
            Quantity = new Quantity(1, "units"),
            Serviced = servicedEnd is null
                ? new Date(servicedStart)
                : new Period { Start = servicedStart, End = servicedEnd },
        };
        // The service location's state (Claim.item.locationAddress), which the
        // adjudicator's state-specific rules key on; absent unless asked for.
        if (serviceState is not null)
            item.Location = new Address { State = serviceState };
        if (serviceTypeCode is not null)
            item.Category = new CodeableConcept(X12ServiceTypeSystem, serviceTypeCode);

        var claim = new Claim
        {
            Id = "claim-" + Guid.NewGuid().ToString("N")[..8],
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference($"Patient/{PatientId}"),
            Created = "2026-10-11",
            Insurer = new ResourceReference("Organization/cho-payer"),
            Provider = new ResourceReference($"Practitioner/{PractitionerId}"),
            Insurance = [new() { Sequence = 1, Focal = true, Coverage = new ResourceReference($"Coverage/{CoverageId}") }],
            Diagnosis =
            [
                new Claim.DiagnosisComponent
                {
                    Sequence = 1,
                    Diagnosis = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "M17.11"),
                },
            ],
            Item = [item],
        };
        if (priority is not null)
            claim.Priority = new CodeableConcept(ProcessPrioritySystem, priority);

        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                new() { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = claim },
                new() { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = patient },
                new() { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = coverage },
                new() { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = practitioner },
            ],
        };
    }

    public static Claim ClaimOf(Bundle bundle) => bundle.Entry.Select(e => e.Resource).OfType<Claim>().Single();

    public static Bundle Without<T>(this Bundle bundle) where T : Resource
    {
        bundle.Entry.RemoveAll(e => e.Resource is T);
        return bundle;
    }
}
