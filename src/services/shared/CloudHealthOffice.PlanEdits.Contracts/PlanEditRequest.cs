namespace CloudHealthOffice.PlanEdits.Contracts;

/// <summary>
/// What CHO sends a plan's edit service for one claim (contract version 1).
/// It carries the claim facts edits are written against and no more: no
/// member or provider names, addresses, or free-text notes.
/// </summary>
public sealed record PlanEditRequest
{
    /// <summary>Always <see cref="PlanEditContract.Version"/>.</summary>
    public string ContractVersion { get; init; } = PlanEditContract.Version;

    /// <summary>Unique for each request CHO sends (repeated in <see cref="PlanEditContract.RequestIdHeader"/>).</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>The CHO tenant the claim belongs to.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>One of <see cref="PlanEditPurpose"/>.</summary>
    public string Purpose { get; init; } = PlanEditPurpose.Adjudication;

    public PlanEditClaim Claim { get; init; } = new();
}

/// <summary>The claim, as edits see it.</summary>
public sealed record PlanEditClaim
{
    public string ClaimId { get; init; } = string.Empty;
    public string ClaimVersionId { get; init; } = string.Empty;
    public int VersionNumber { get; init; }
    public string ClaimNumber { get; init; } = string.Empty;

    /// <summary>Professional, Institutional, Dental, Pharmacy or Vision.</summary>
    public string ClaimType { get; init; } = string.Empty;

    /// <summary>The 837 claim frequency code: 1 original, 7 replacement, 8 void.</summary>
    public string FrequencyCode { get; init; } = string.Empty;

    /// <summary>Commercial, Medicare, Medicaid, MedicareAdvantage, Exchange, CHIP and so on.</summary>
    public string LineOfBusiness { get; init; } = string.Empty;

    public string? BenefitPlanId { get; init; }
    public string MemberId { get; init; } = string.Empty;
    public string? SubscriberId { get; init; }

    /// <summary>Present when CHO resolved the member; age and sex edits need it.</summary>
    public PlanEditMember? Member { get; init; }

    public string BillingProviderNpi { get; init; } = string.Empty;
    public string? RenderingProviderNpi { get; init; }
    public string? FacilityNpi { get; init; }
    public string? PlaceOfService { get; init; }

    /// <summary>yyyy-MM-dd.</summary>
    public string ServiceDateFrom { get; init; } = string.Empty;

    /// <summary>yyyy-MM-dd.</summary>
    public string ServiceDateTo { get; init; } = string.Empty;

    public decimal TotalCharge { get; init; }
    public string? PriorAuthorizationNumber { get; init; }
    public string? ReferralNumber { get; init; }
    public IReadOnlyList<PlanEditDiagnosis> Diagnoses { get; init; } = [];
    public IReadOnlyList<PlanEditLine> Lines { get; init; } = [];
}

/// <summary>The member facts edits commonly depend on.</summary>
public sealed record PlanEditMember
{
    /// <summary>yyyy-MM-dd, when known.</summary>
    public string? BirthDate { get; init; }

    /// <summary>The gender CHO holds for the member (M, F or U), when known.</summary>
    public string? Gender { get; init; }
}

public sealed record PlanEditDiagnosis
{
    public string Code { get; init; } = string.Empty;

    /// <summary>X12 code-list qualifier: ABK principal, ABF other, and so on.</summary>
    public string Qualifier { get; init; } = string.Empty;

    /// <summary>The number a line's <see cref="PlanEditLine.DiagnosisPointers"/> refer to.</summary>
    public int Pointer { get; init; }
}

public sealed record PlanEditLine
{
    /// <summary>The number an edit names in <see cref="PlanEdit.LineNumbers"/>.</summary>
    public int LineNumber { get; init; }

    public string ProcedureCode { get; init; } = string.Empty;
    public IReadOnlyList<string> Modifiers { get; init; } = [];
    public string? RevenueCode { get; init; }
    public decimal Units { get; init; }
    public string? UnitType { get; init; }
    public decimal Charge { get; init; }

    /// <summary>yyyy-MM-dd.</summary>
    public string ServiceDateFrom { get; init; } = string.Empty;

    /// <summary>yyyy-MM-dd.</summary>
    public string ServiceDateTo { get; init; } = string.Empty;

    public string? PlaceOfService { get; init; }
    public IReadOnlyList<int> DiagnosisPointers { get; init; } = [];
    public string? NationalDrugCode { get; init; }
    public string? PriorAuthorizationNumber { get; init; }
}
