namespace CloudHealthOffice.PricingApi.Models;

// ─────────────────────────────────────────────────────────────
//  Repricing
// ─────────────────────────────────────────────────────────────

public record RepricingRequest
{
    /// <summary>Fee schedule to price against (e.g., "MEDICARE_RBRVS_2025", "MEDICARE_OPPS_2025", "MEDICARE_DRG_2025").</summary>
    public required string FeeScheduleId { get; init; }

    /// <summary>Medicare locality / MAC region for geographic adjustment (e.g., "05", "01").</summary>
    public string? Locality { get; init; }

    /// <summary>Claim type: Professional, Outpatient, Inpatient.</summary>
    public required ClaimType ClaimType { get; init; }

    /// <summary>Place of service code (relevant for professional claims).</summary>
    public string? PlaceOfService { get; init; }

    /// <summary>Primary diagnosis code (ICD-10-CM). Required for DRG-based pricing.</summary>
    public string? PrimaryDiagnosis { get; init; }

    /// <summary>Additional diagnosis codes.</summary>
    public List<string>? SecondaryDiagnoses { get; init; }

    /// <summary>MS-DRG code (if known; otherwise will be derived from diagnoses + procedures).</summary>
    public string? DrgCode { get; init; }

    /// <summary>Individual service lines to price.</summary>
    public required List<ClaimLineRequest> Lines { get; init; }
}

public record ClaimLineRequest
{
    /// <summary>Service line number (1-based).</summary>
    public int LineNumber { get; init; } = 1;

    /// <summary>CPT/HCPCS procedure code.</summary>
    public required string ProcedureCode { get; init; }

    /// <summary>Modifier codes (up to 4).</summary>
    public List<string>? Modifiers { get; init; }

    /// <summary>Revenue code (required for outpatient/institutional claims).</summary>
    public string? RevenueCode { get; init; }

    /// <summary>Units of service.</summary>
    public decimal Units { get; init; } = 1;

    /// <summary>Billed amount (for reference/comparison).</summary>
    public decimal? BilledAmount { get; init; }

    /// <summary>Date of service.</summary>
    public DateOnly? ServiceDate { get; init; }
}

public record RepricingResponse
{
    public required string RequestId { get; init; }
    public required string FeeScheduleId { get; init; }
    public required string FeeScheduleVersion { get; init; }
    public required ClaimType ClaimType { get; init; }
    public string? DrgCode { get; init; }
    public decimal? DrgWeight { get; init; }
    public decimal TotalAllowed { get; init; }
    public decimal? TotalBilled { get; init; }
    public required List<PricedLine> Lines { get; init; }
    public List<string>? Warnings { get; init; }
    public required DateTimeOffset PricedAt { get; init; }
}

public record PricedLine
{
    public int LineNumber { get; init; }
    public required string ProcedureCode { get; init; }
    public List<string>? Modifiers { get; init; }
    public decimal Units { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal? BilledAmount { get; init; }
    public required PricingBreakdown Breakdown { get; init; }
    public PricingStatus Status { get; init; } = PricingStatus.Priced;
    public string? StatusReason { get; init; }
}

public record PricingBreakdown
{
    /// <summary>Base rate before geographic or modifier adjustments.</summary>
    public decimal BaseRate { get; init; }

    /// <summary>Geographic Practice Cost Index adjustment factor (RBRVS).</summary>
    public decimal? GpciAdjustment { get; init; }

    /// <summary>Facility/non-facility indicator used.</summary>
    public string? FacilityIndicator { get; init; }

    /// <summary>Work RVU component (RBRVS).</summary>
    public decimal? WorkRvu { get; init; }

    /// <summary>Practice Expense RVU component (RBRVS).</summary>
    public decimal? PracticeExpenseRvu { get; init; }

    /// <summary>Malpractice RVU component (RBRVS).</summary>
    public decimal? MalpracticeRvu { get; init; }

    /// <summary>Conversion factor applied (RBRVS).</summary>
    public decimal? ConversionFactor { get; init; }

    /// <summary>Multiple Procedure Reduction percentage applied.</summary>
    public decimal? MultiProcReduction { get; init; }

    /// <summary>Modifier impact description.</summary>
    public string? ModifierAdjustment { get; init; }

    /// <summary>APC code and payment rate (OPPS).</summary>
    public string? ApcCode { get; init; }

    /// <summary>DRG relative weight (Inpatient).</summary>
    public decimal? DrgRelativeWeight { get; init; }

    /// <summary>Hospital base rate used (Inpatient).</summary>
    public decimal? HospitalBaseRate { get; init; }
}

// ─────────────────────────────────────────────────────────────
//  Code Lookup
// ─────────────────────────────────────────────────────────────

public record CodeLookupRequest
{
    /// <summary>CPT/HCPCS code to look up.</summary>
    public required string ProcedureCode { get; init; }

    /// <summary>Fee schedule to look up against.</summary>
    public required string FeeScheduleId { get; init; }

    /// <summary>Medicare locality for geographic adjustment.</summary>
    public string? Locality { get; init; }

    /// <summary>Facility or non-facility rate.</summary>
    public bool Facility { get; init; } = false;
}

public record CodeLookupResponse
{
    public required string ProcedureCode { get; init; }
    public string? Description { get; init; }
    public required string FeeScheduleId { get; init; }
    public string? Locality { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal? WorkRvu { get; init; }
    public decimal? PracticeExpenseRvu { get; init; }
    public decimal? MalpracticeRvu { get; init; }
    public decimal? TotalRvu { get; init; }
    public decimal? ConversionFactor { get; init; }
    public string? StatusIndicator { get; init; }
    public string? ApcCode { get; init; }
    public bool Facility { get; init; }
}

// ─────────────────────────────────────────────────────────────
//  Fee Schedule Metadata
// ─────────────────────────────────────────────────────────────

public record FeeScheduleInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required FeeScheduleType Type { get; init; }
    public required string Version { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public DateOnly? TermDate { get; init; }
    public required int CodeCount { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset LastUpdated { get; init; }
}

// ─────────────────────────────────────────────────────────────
//  Fee Schedule Data (internal storage)
// ─────────────────────────────────────────────────────────────

public record FeeScheduleEntry
{
    public string Id { get; init; } = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
    public required string FeeScheduleId { get; init; }
    public required string ProcedureCode { get; init; }
    public string? Description { get; init; }
    public string? Locality { get; init; }
    public decimal? WorkRvu { get; init; }
    public decimal? PracticeExpenseRvu { get; init; }
    public decimal? PracticeExpenseRvuFacility { get; init; }
    public decimal? MalpracticeRvu { get; init; }
    public decimal? TotalRvuNonFacility { get; init; }
    public decimal? TotalRvuFacility { get; init; }
    public decimal? ConversionFactor { get; init; }
    public decimal? NonFacilityRate { get; init; }
    public decimal? FacilityRate { get; init; }
    public string? StatusIndicator { get; init; }
    public string? ApcCode { get; init; }
    public decimal? ApcPaymentRate { get; init; }
    public decimal? DrgWeight { get; init; }
    public decimal? DrgBaseRate { get; init; }
    public int? MultiProcRank { get; init; }
}

// ─────────────────────────────────────────────────────────────
//  API Key / Tenant
// ─────────────────────────────────────────────────────────────

public record ApiKeyRecord
{
    public string? Id { get; init; }
    public required string ApiKey { get; init; }
    public required string TenantName { get; init; }
    public string? ContactEmail { get; init; }
    public required PricingTier Tier { get; init; }
    public int MonthlyLimit { get; init; }
    public int CurrentMonthUsage { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; init; } = true;
}

public record UsageRecord
{
    public string? Id { get; init; }
    public required string ApiKey { get; init; }
    public required string Endpoint { get; init; }
    public required int LineCount { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public int ResponseTimeMs { get; init; }
    public bool Success { get; init; }
}

// ─────────────────────────────────────────────────────────────
//  Enums
// ─────────────────────────────────────────────────────────────

public enum ClaimType
{
    Professional,
    Outpatient,
    Inpatient
}

public enum FeeScheduleType
{
    MedicareRbrvs,
    MedicareOpps,
    MedicareDrg,
    Medicaid,
    Commercial
}

public enum PricingTier
{
    Free,        // 1,000 claims/month
    Starter,     // 10,000 claims/month
    Professional, // 100,000 claims/month
    Enterprise   // Unlimited
}

public enum PricingStatus
{
    Priced,
    NotFound,
    BundledWithPrimary,
    ByReport,
    NonCovered,
    StatutoryExclusion
}

// ─────────────────────────────────────────────────────────────
//  Standard API Envelope
// ─────────────────────────────────────────────────────────────

public record ApiResponse<T>
{
    public bool Success { get; init; } = true;
    public T? Data { get; init; }
    public ApiError? Error { get; init; }
}

public record ApiError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, string[]>? Details { get; init; }
}
