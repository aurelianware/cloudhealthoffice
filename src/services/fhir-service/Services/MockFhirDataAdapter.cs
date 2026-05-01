using Hl7.Fhir.Model;
using FhirService.Models;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services;

/// <summary>
/// In-memory test data for Sprint 2.  Sprint 3 replaces this with adapters that
/// call member-service, coverage-service, and claims-service via typed HttpClients.
/// </summary>
public class MockFhirDataAdapter : IFhirDataAdapter
{
    // ── Seed data ─────────────────────────────────────────────────────────────

    private static readonly List<Patient> Patients =
    [
        new()
        {
            Id = "pat-001",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), VersionId = "1" },
            Identifier =
            [
                new() { System = "http://hl7.org/fhir/sid/us-medicare", Value = "1EG4-TE5-MK72" }
            ],
            Name =
            [
                new() { Family = "Smith", Given = ["John", "A"], Use = HumanName.NameUse.Official }
            ],
            Gender = AdministrativeGender.Male,
            BirthDate = "1955-07-14",
            Address =
            [
                new() { Line = ["123 Main St"], City = "Nashville", State = "TN", PostalCode = "37201", Country = "US" }
            ],
            Telecom =
            [
                new() { System = ContactPoint.ContactPointSystem.Phone, Value = "615-555-0101", Use = ContactPoint.ContactPointUse.Home }
            ]
        },
        new()
        {
            Id = "pat-002",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 8, 15, 0, 0, 0, TimeSpan.Zero), VersionId = "1" },
            Identifier =
            [
                new() { System = "http://hl7.org/fhir/sid/us-medicare", Value = "2HF5-UF6-NL83" }
            ],
            Name =
            [
                new() { Family = "Jones", Given = ["Mary", "E"], Use = HumanName.NameUse.Official }
            ],
            Gender = AdministrativeGender.Female,
            BirthDate = "1962-03-22",
            Address =
            [
                new() { Line = ["456 Oak Ave"], City = "Memphis", State = "TN", PostalCode = "38101", Country = "US" }
            ]
        },
        new()
        {
            Id = "pat-003",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 11, 1, 0, 0, 0, TimeSpan.Zero), VersionId = "1" },
            Identifier =
            [
                new() { System = "http://hl7.org/fhir/sid/us-medicare", Value = "3JG6-VG7-OM94" }
            ],
            Name =
            [
                new() { Family = "Williams", Given = ["Robert"], Use = HumanName.NameUse.Official }
            ],
            Gender = AdministrativeGender.Male,
            BirthDate = "1948-11-30",
            Address =
            [
                new() { Line = ["789 Pine Rd"], City = "Knoxville", State = "TN", PostalCode = "37901", Country = "US" }
            ]
        }
    ];

    private static readonly List<Coverage> Coverages =
    [
        new()
        {
            Id = "cov-001",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), VersionId = "1" },
            Status = FinancialResourceStatusCodes.Active,
            Subscriber = new ResourceReference("Patient/pat-001"),
            Beneficiary = new ResourceReference("Patient/pat-001"),
            Payor = [new ResourceReference("Organization/cho-payer-001")],
            Period = new Period { Start = "2025-01-01" },
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "HMO", "Health Maintenance Organization")
        },
        new()
        {
            Id = "cov-002",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), VersionId = "1" },
            Status = FinancialResourceStatusCodes.Active,
            Subscriber = new ResourceReference("Patient/pat-002"),
            Beneficiary = new ResourceReference("Patient/pat-002"),
            Payor = [new ResourceReference("Organization/cho-payer-001")],
            Period = new Period { Start = "2025-01-01" },
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "PPO", "Preferred Provider Organization")
        },
        new()
        {
            Id = "cov-003",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), VersionId = "1" },
            Status = FinancialResourceStatusCodes.Active,
            Subscriber = new ResourceReference("Patient/pat-003"),
            Beneficiary = new ResourceReference("Patient/pat-003"),
            Payor = [new ResourceReference("Organization/cho-payer-001")],
            Period = new Period { Start = "2025-03-01", End = "2025-12-31" },
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "HMO", "Health Maintenance Organization")
        }
    ];

    private static readonly List<Encounter> Encounters =
    [
        new()
        {
            Id = "enc-001",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 2, 5, 0, 0, 0, TimeSpan.Zero) },
            Status = Encounter.EncounterStatus.Finished,
            Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB", "ambulatory"),
            Type = [new CodeableConcept("http://snomed.info/sct", "11429006", "Consultation")],
            Subject = new ResourceReference("Patient/pat-001"),
            Period = new Period { Start = "2025-02-05T09:00:00Z", End = "2025-02-05T09:30:00Z" }
        },
        new()
        {
            Id = "enc-002",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 5, 15, 0, 0, 0, TimeSpan.Zero) },
            Status = Encounter.EncounterStatus.Finished,
            Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB", "ambulatory"),
            Type = [new CodeableConcept("http://snomed.info/sct", "410620009", "Well child visit")],
            Subject = new ResourceReference("Patient/pat-001"),
            Period = new Period { Start = "2025-05-15T14:00:00Z", End = "2025-05-15T14:45:00Z" }
        },
        new()
        {
            Id = "enc-003",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero) },
            Status = Encounter.EncounterStatus.Finished,
            Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB", "ambulatory"),
            Type = [new CodeableConcept("http://snomed.info/sct", "11429006", "Consultation")],
            Subject = new ResourceReference("Patient/pat-002"),
            Period = new Period { Start = "2025-07-01T10:00:00Z", End = "2025-07-01T10:30:00Z" }
        },
        new()
        {
            Id = "enc-004",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 10, 20, 0, 0, 0, TimeSpan.Zero) },
            Status = Encounter.EncounterStatus.InProgress,
            Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IMP", "inpatient encounter"),
            Type = [new CodeableConcept("http://snomed.info/sct", "32485007", "Hospital admission")],
            Subject = new ResourceReference("Patient/pat-003"),
            Period = new Period { Start = "2025-10-20T08:00:00Z" }
        }
    ];

    private static readonly List<Claim> Claims =
    [
        new()
        {
            Id = "clm-001",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 2, 6, 0, 0, 0, TimeSpan.Zero) },
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Claim,
            Patient = new ResourceReference("Patient/pat-001"),
            Created = "2025-02-06",
            Insurer = new ResourceReference("Organization/cho-payer-001"),
            Provider = new ResourceReference("Practitioner/prov-001"),
            Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
            Insurance = [new Claim.InsuranceComponent { Sequence = 1, Focal = true, Coverage = new ResourceReference("Coverage/cov-001") }],
            Item =
            [
                new()
                {
                    Sequence = 1,
                    ProductOrService = new CodeableConcept("http://www.ama-assn.org/go/cpt", "99213"),
                    Serviced = new Date("2025-02-05"),
                    Quantity = new Quantity(1, "1"),
                    UnitPrice = new Money { Value = 150.00m, Currency = Money.Currencies.USD }
                }
            ]
        },
        new()
        {
            Id = "clm-002",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 5, 16, 0, 0, 0, TimeSpan.Zero) },
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Claim,
            Patient = new ResourceReference("Patient/pat-001"),
            Created = "2025-05-16",
            Insurer = new ResourceReference("Organization/cho-payer-001"),
            Provider = new ResourceReference("Practitioner/prov-002"),
            Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
            Insurance = [new Claim.InsuranceComponent { Sequence = 1, Focal = true, Coverage = new ResourceReference("Coverage/cov-001") }],
            Item =
            [
                new()
                {
                    Sequence = 1,
                    ProductOrService = new CodeableConcept("http://www.ama-assn.org/go/cpt", "93000"),
                    Serviced = new Date("2025-05-15"),
                    Quantity = new Quantity(1, "1"),
                    UnitPrice = new Money { Value = 210.00m, Currency = Money.Currencies.USD }
                }
            ]
        },
        new()
        {
            Id = "clm-003",
            Meta = new Meta { LastUpdated = new DateTimeOffset(2025, 7, 2, 0, 0, 0, TimeSpan.Zero) },
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Claim,
            Patient = new ResourceReference("Patient/pat-002"),
            Created = "2025-07-02",
            Insurer = new ResourceReference("Organization/cho-payer-001"),
            Provider = new ResourceReference("Practitioner/prov-001"),
            Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
            Insurance = [new Claim.InsuranceComponent { Sequence = 1, Focal = true, Coverage = new ResourceReference("Coverage/cov-002") }],
            Item =
            [
                new()
                {
                    Sequence = 1,
                    ProductOrService = new CodeableConcept("http://www.ama-assn.org/go/cpt", "99214"),
                    Serviced = new Date("2025-07-01"),
                    Quantity = new Quantity(1, "1"),
                    UnitPrice = new Money { Value = 195.00m, Currency = Money.Currencies.USD }
                }
            ]
        }
    ];

    // ── Patient ───────────────────────────────────────────────────────────────

    public Task<Patient?> GetPatientAsync(string id, string tenantId, CancellationToken ct = default)
        => Task.FromResult(Patients.FirstOrDefault(p => p.Id == id));

    public Task<(IReadOnlyList<Patient> Items, int Total)> SearchPatientsAsync(
        PatientSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = Patients.AsEnumerable();

        if (!string.IsNullOrEmpty(search.Id))
            query = query.Where(p => p.Id == search.Id);

        if (!string.IsNullOrEmpty(search.Name))
        {
            var lower = search.Name.ToLowerInvariant();
            query = query.Where(p =>
                p.Name.Any(n =>
                    (n.Family?.Contains(lower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    n.Given.Any(g => g.Contains(lower, StringComparison.OrdinalIgnoreCase))));
        }

        if (!string.IsNullOrEmpty(search.Family))
            query = query.Where(p => p.Name.Any(n =>
                n.Family?.Contains(search.Family, StringComparison.OrdinalIgnoreCase) ?? false));

        if (!string.IsNullOrEmpty(search.BirthDate))
            query = query.Where(p => p.BirthDate == search.BirthDate);

        if (!string.IsNullOrEmpty(search.Identifier))
            query = query.Where(p => p.Identifier.Any(i => i.Value == search.Identifier));

        if (!string.IsNullOrEmpty(search.Gender) &&
            Enum.TryParse<AdministrativeGender>(search.Gender, true, out var gender))
            query = query.Where(p => p.Gender == gender);

        var all = query.ToList();
        var page = all
            .Skip((search.Page - 1) * search.Count)
            .Take(search.Count)
            .ToList();

        return Task.FromResult<(IReadOnlyList<Patient>, int)>((page, all.Count));
    }

    // ── Coverage ──────────────────────────────────────────────────────────────

    public Task<Coverage?> GetCoverageAsync(string id, string tenantId, CancellationToken ct = default)
        => Task.FromResult(Coverages.FirstOrDefault(c => c.Id == id));

    public Task<(IReadOnlyList<Coverage> Items, int Total)> SearchCoverageAsync(
        CoverageSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = Coverages.AsEnumerable();

        if (!string.IsNullOrEmpty(search.Id))
            query = query.Where(c => c.Id == search.Id);

        if (!string.IsNullOrEmpty(search.Patient))
        {
            var ref_ = NormalizeRef("Patient", search.Patient);
            query = query.Where(c =>
                c.Beneficiary?.Reference == ref_ || c.Subscriber?.Reference == ref_);
        }

        if (!string.IsNullOrEmpty(search.Status) &&
            Enum.TryParse<FinancialResourceStatusCodes>(search.Status, true, out var status))
            query = query.Where(c => c.Status == status);

        var all = query.ToList();
        var page = all.Skip((search.Page - 1) * search.Count).Take(search.Count).ToList();

        return Task.FromResult<(IReadOnlyList<Coverage>, int)>((page, all.Count));
    }

    // ── Encounter ─────────────────────────────────────────────────────────────

    public Task<Encounter?> GetEncounterAsync(string id, string tenantId, CancellationToken ct = default)
        => Task.FromResult(Encounters.FirstOrDefault(e => e.Id == id));

    public Task<(IReadOnlyList<Encounter> Items, int Total)> SearchEncountersAsync(
        EncounterSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = Encounters.AsEnumerable();

        if (!string.IsNullOrEmpty(search.Id))
            query = query.Where(e => e.Id == search.Id);

        if (!string.IsNullOrEmpty(search.Patient))
        {
            var ref_ = NormalizeRef("Patient", search.Patient);
            query = query.Where(e => e.Subject?.Reference == ref_);
        }

        if (!string.IsNullOrEmpty(search.Status) &&
            Enum.TryParse<Encounter.EncounterStatus>(search.Status.Replace("-", ""), true, out var status))
            query = query.Where(e => e.Status == status);

        var all = query.ToList();
        var page = all.Skip((search.Page - 1) * search.Count).Take(search.Count).ToList();

        return Task.FromResult<(IReadOnlyList<Encounter>, int)>((page, all.Count));
    }

    // ── Claim ─────────────────────────────────────────────────────────────────

    public Task<Claim?> GetClaimAsync(string id, string tenantId, CancellationToken ct = default)
        => Task.FromResult(Claims.FirstOrDefault(c => c.Id == id));

    public Task<(IReadOnlyList<Claim> Items, int Total)> SearchClaimsAsync(
        ClaimSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = Claims.AsEnumerable();

        if (!string.IsNullOrEmpty(search.Id))
            query = query.Where(c => c.Id == search.Id);

        if (!string.IsNullOrEmpty(search.Patient))
        {
            var ref_ = NormalizeRef("Patient", search.Patient);
            query = query.Where(c => c.Patient?.Reference == ref_);
        }

        if (!string.IsNullOrEmpty(search.Status) &&
            Enum.TryParse<FinancialResourceStatusCodes>(search.Status, true, out var status))
            query = query.Where(c => c.Status == status);

        var all = query.ToList();
        var page = all.Skip((search.Page - 1) * search.Count).Take(search.Count).ToList();

        return Task.FromResult<(IReadOnlyList<Claim>, int)>((page, all.Count));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts either a bare ID ("pat-001") or a typed reference ("Patient/pat-001")
    /// and normalises to the typed form used in resource references.
    /// </summary>
    private static string NormalizeRef(string resourceType, string value)
        => value.StartsWith(resourceType + "/", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{resourceType}/{value}";
}
