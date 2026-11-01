namespace EncounterSubmissionService.StateRoutes;

public enum EncounterFormType
{
    Professional,
    Institutional,
    Dental
}

/// <summary>
/// An adjudicated claim, normalized for state encounter reporting. Built from the
/// claim, member and billing provider records; written by <see cref="Encounter837Writer"/>.
/// </summary>
public sealed record EncounterClaim
{
    public required string SubmissionId { get; init; }
    public required string ClaimId { get; init; }

    /// <summary>CLM01. The plan's own claim reference; the state echoes it on every response.</summary>
    public required string PatientControlNumber { get; init; }

    public EncounterFormType Form { get; init; } = EncounterFormType.Professional;

    /// <summary>CLM05-3: 1 original, 7 replacement, 8 void.</summary>
    public string FrequencyCode { get; init; } = "1";

    /// <summary>REF*F8: the state's identifier for the encounter being replaced or voided.</summary>
    public string? OriginalStateReference { get; init; }

    public decimal TotalCharge { get; init; }

    /// <summary>837P: place of service. 837I: two-digit facility type (type of bill, first two digits).</summary>
    public string? PlaceOfService { get; init; }
    public string? FacilityTypeCode { get; init; }

    public DateOnly ServiceFrom { get; init; }
    public DateOnly ServiceTo { get; init; }

    public DateOnly? AdmissionDate { get; init; }
    public string? AdmissionHour { get; init; }
    public string? AdmissionTypeCode { get; init; }
    public string? AdmissionSourceCode { get; init; }
    public string? PatientStatusCode { get; init; }

    public string? PriorAuthorizationNumber { get; init; }

    /// <summary>ICD-10-CM codes; the first is principal (837I) / primary (837P).</summary>
    public IReadOnlyList<EncounterDiagnosis> Diagnoses { get; init; } = Array.Empty<EncounterDiagnosis>();

    public required EncounterMember Member { get; init; }
    public required EncounterProvider BillingProvider { get; init; }
    public string? RenderingNpi { get; init; }
    public string? RenderingLastName { get; init; }
    public string? RenderingFirstName { get; init; }

    // ── Plan adjudication (2320 / 2330B / 2430) ──────────────────────
    public decimal PlanPaidAmount { get; init; }
    public DateOnly PlanAdjudicationDate { get; init; }
    public IReadOnlyList<EncounterAdjustment> ClaimAdjustments { get; init; } = Array.Empty<EncounterAdjustment>();

    public IReadOnlyList<EncounterLine> Lines { get; init; } = Array.Empty<EncounterLine>();
}

public sealed record EncounterDiagnosis(string Code, string? PresentOnAdmission = null);

public sealed record EncounterMember
{
    /// <summary>The member's state Medicaid identifier (Texas Medicaid ID, Medi-Cal CIN) as carried on the 834.</summary>
    public required string MemberId { get; init; }
    public string? GroupNumber { get; init; }
    public required string LastName { get; init; }
    public string? FirstName { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public string Gender { get; init; } = "U";
    public string? AddressLine { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
}

public sealed record EncounterProvider
{
    public required string Npi { get; init; }

    /// <summary>1 person, 2 non-person entity.</summary>
    public string EntityType { get; init; } = "2";
    public required string LastOrOrganizationName { get; init; }
    public string? FirstName { get; init; }
    public string? TaxId { get; init; }
    public string? TaxonomyCode { get; init; }
    public string? AddressLine { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
}

public sealed record EncounterLine
{
    public int LineNumber { get; init; }
    public string ProcedureQualifier { get; init; } = "HC";
    public required string ProcedureCode { get; init; }
    public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();
    public string? RevenueCode { get; init; }
    public decimal Charge { get; init; }
    public decimal Units { get; init; } = 1;

    /// <summary>UN units, MJ minutes, DA days.</summary>
    public string UnitType { get; init; } = "UN";
    public IReadOnlyList<int> DiagnosisPointers { get; init; } = Array.Empty<int>();
    public DateOnly ServiceFrom { get; init; }
    public DateOnly ServiceTo { get; init; }
    public string? PlaceOfService { get; init; }
    public string? NationalDrugCode { get; init; }
    public decimal? DrugQuantity { get; init; }
    public string? DrugUnit { get; init; }

    public decimal PaidAmount { get; init; }
    public IReadOnlyList<EncounterAdjustment> Adjustments { get; init; } = Array.Empty<EncounterAdjustment>();
}

/// <summary>A CAS adjustment: group (CO, PR, OA, PI, CR), reason (CARC) and amount.</summary>
public sealed record EncounterAdjustment(string GroupCode, string ReasonCode, decimal Amount, decimal? Quantity = null);
