using System.Globalization;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Services.Dtr;

// The portal has its own Coverage view model one namespace up.
using Coverage = Hl7.Fhir.Model.Coverage;

/// <summary>
/// What the provider enters on the questionnaire page to request the prior
/// authorization the form documents.
/// </summary>
public sealed class PriorAuthEntry
{
    /// <summary>The requesting provider's NPI (10 digits).</summary>
    public string RequestingNpi { get; set; } = string.Empty;

    public string? RequestingName { get; set; }

    /// <summary>True for a practice or facility (Organization), false for an individual clinician.</summary>
    public bool RequestingIsOrganization { get; set; }

    public string ServiceSystem { get; set; } = PasRequestBuilder.CptSystem;

    public string ServiceCode { get; set; } = string.Empty;

    public string? ServiceDisplay { get; set; }

    public decimal Quantity { get; set; } = 1;

    public DateTime? ServiceDateFrom { get; set; }

    public DateTime? ServiceDateTo { get; set; }

    /// <summary>ICD-10-CM codes, principal first.</summary>
    public List<string> DiagnosisCodes { get; set; } = [];

    /// <summary>An expedited (urgent) request: 72-hour decision clock.</summary>
    public bool Urgent { get; set; }
}

/// <summary>
/// Builds the Da Vinci PAS request Bundle for <c>Claim/$submit</c> from the
/// member's Patient and Coverage (as fhir-service holds them), what the
/// provider entered, and the completed DTR QuestionnaireResponses, which ride
/// in <c>Claim.supportingInfo</c> (PAS AdditionalInformation) and as Bundle
/// entries, so fhir-service records them on the authorization.
/// </summary>
public static class PasRequestBuilder
{
    public const string CptSystem = "http://www.ama-assn.org/go/cpt";
    public const string HcpcsSystem = "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    public const string CdtSystem = "http://www.ada.org/cdt";
    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string PasTempCodes = "http://hl7.org/fhir/us/davinci-pas/CodeSystem/PASTempCodes";
    public const string IdentifierTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";
    public const string ProcessPrioritySystem = "http://terminology.hl7.org/CodeSystem/processpriority";
    public const string ClaimTypeSystem = "http://terminology.hl7.org/CodeSystem/claim-type";

    /// <summary>The checks the page runs before building: what the provider still has to fix.</summary>
    public static IReadOnlyList<string> Validate(PriorAuthEntry entry)
    {
        var problems = new List<string>();
        var npi = entry.RequestingNpi?.Trim() ?? string.Empty;
        if (npi.Length != 10 || !npi.All(char.IsAsciiDigit))
            problems.Add("Requesting provider NPI must be 10 digits.");
        if (string.IsNullOrWhiteSpace(entry.ServiceCode))
            problems.Add("Enter the service (procedure) code.");
        if (entry.Quantity <= 0)
            problems.Add("Quantity must be positive.");
        if (entry.ServiceDateFrom is null)
            problems.Add("Enter the planned service date.");
        else if (entry.ServiceDateTo < entry.ServiceDateFrom)
            problems.Add("The service end date is before the start date.");
        foreach (var code in entry.DiagnosisCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (code.Trim().Length > 10)
                problems.Add($"Diagnosis code '{code.Trim()}' is too long for ICD-10-CM.");
        }
        return problems;
    }

    public static Bundle Build(
        string memberId,
        Patient patient,
        Coverage coverage,
        PriorAuthEntry entry,
        IReadOnlyList<QuestionnaireResponse> documentation,
        DateTimeOffset now)
    {
        var patientCopy = (Patient)patient.DeepCopy();
        patientCopy.Id = memberId;
        // fhir-service files the request under Patient.identifier typed MB:
        // the member the provider looked up, whatever else the Patient carries.
        if (!(patientCopy.Identifier ?? []).Any(IsMemberIdentifier))
        {
            patientCopy.Identifier ??= [];
            patientCopy.Identifier.Add(new Identifier { Value = memberId, Type = new CodeableConcept(IdentifierTypeSystem, "MB") });
        }

        var coverageCopy = (Coverage)coverage.DeepCopy();
        coverageCopy.Beneficiary = new ResourceReference($"Patient/{memberId}");

        var npi = entry.RequestingNpi.Trim();
        Resource requester = entry.RequestingIsOrganization
            ? new Organization
            {
                Id = "requester",
                Identifier = [new Identifier(NpiSystem, npi)],
                Name = NullIfBlank(entry.RequestingName),
            }
            : new Practitioner
            {
                Id = "requester",
                Identifier = [new Identifier(NpiSystem, npi)],
                Name = NullIfBlank(entry.RequestingName) is { } name ? [new HumanName { Text = name, Family = LastWord(name) }] : [],
            };

        var from = entry.ServiceDateFrom!.Value.Date;
        var to = (entry.ServiceDateTo ?? entry.ServiceDateFrom).Value.Date;

        var claim = new Claim
        {
            Id = "pa-request",
            Identifier = [new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{Guid.NewGuid()}")],
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept(ClaimTypeSystem, "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference($"Patient/{memberId}"),
            Created = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Provider = new ResourceReference($"{requester.TypeName}/requester"),
            Priority = new CodeableConcept(ProcessPrioritySystem, entry.Urgent ? "stat" : "normal"),
            Insurance =
            [
                new Claim.InsuranceComponent
                {
                    Sequence = 1,
                    Focal = true,
                    Coverage = new ResourceReference($"Coverage/{coverageCopy.Id}"),
                },
            ],
            Item =
            [
                new Claim.ItemComponent
                {
                    Sequence = 1,
                    ProductOrService = new CodeableConcept(entry.ServiceSystem, entry.ServiceCode.Trim(), NullIfBlank(entry.ServiceDisplay)),
                    Quantity = new Quantity { Value = entry.Quantity },
                    Serviced = new Period(new FhirDateTime(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new FhirDateTime(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
                },
            ],
        };

        var sequence = 1;
        foreach (var code in entry.DiagnosisCodes.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct())
        {
            claim.Diagnosis.Add(new Claim.DiagnosisComponent
            {
                Sequence = sequence++,
                Diagnosis = new CodeableConcept(Icd10CmSystem, code),
            });
        }

        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Timestamp = now,
            Identifier = new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{Guid.NewGuid()}"),
        };
        Add(bundle, claim);
        Add(bundle, patientCopy);
        Add(bundle, coverageCopy);
        Add(bundle, requester);

        var info = 1;
        foreach (var response in documentation)
        {
            if (string.IsNullOrEmpty(response.Id))
                throw new ArgumentException("Save each QuestionnaireResponse before attaching it.", nameof(documentation));
            claim.SupportingInfo.Add(new Claim.SupportingInformationComponent
            {
                Sequence = info++,
                Category = new CodeableConcept(PasTempCodes, "additionalInformation"),
                Value = new ResourceReference($"QuestionnaireResponse/{response.Id}"),
            });
            Add(bundle, response);
        }

        return bundle;
    }

    /// <summary>The service code system for a code typed by hand: CDT for D-codes, HCPCS for other letters, else CPT.</summary>
    public static string GuessSystem(string code)
    {
        var c = code.Trim().ToUpperInvariant();
        if (c.Length == 5 && c[0] == 'D' && c[1..].All(char.IsAsciiDigit)) return CdtSystem;
        if (c.Length == 5 && char.IsAsciiLetter(c[0]) && c[1..].All(char.IsAsciiDigit)) return HcpcsSystem;
        return CptSystem;
    }

    private static bool IsMemberIdentifier(Identifier identifier)
        => identifier.Type?.Coding?.Any(c => c.System == IdentifierTypeSystem && c.Code == "MB") == true
           && !string.IsNullOrWhiteSpace(identifier.Value);

    private static void Add(Bundle bundle, Resource resource)
        => bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = $"urn:uuid:{Guid.NewGuid()}", Resource = resource });

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LastWord(string name) => name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
}
