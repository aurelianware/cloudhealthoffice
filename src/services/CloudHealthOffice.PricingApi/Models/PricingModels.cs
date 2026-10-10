using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

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

    /// <summary>
    /// Claim type: <c>professional</c>, <c>institutional</c>, <c>outpatient</c>, <c>inpatient</c> or
    /// <c>dental</c>. Optional: when absent the claim is <c>institutional</c> if
    /// <see cref="BillType"/> is a valid type of bill and <c>professional</c> otherwise.
    /// Institutional, outpatient and inpatient claims (837I) always take the facility rate,
    /// as in claims adjudication; professional and dental claims take the facility rate only
    /// for a facility <see cref="PlaceOfService"/>. Inpatient claims (and institutional claims
    /// against an MS-DRG schedule) are priced by <see cref="DrgCode"/>.
    /// </summary>
    public ClaimType? ClaimType { get; init; }

    /// <summary>
    /// NUBC type of bill (837I CLM05-1 facility type + CLM05-3 frequency), three digits
    /// ("131") or four with a leading zero ("0131"). Optional; institutional claims only.
    /// A valid type of bill marks the claim institutional (facility rate). A malformed value,
    /// or a type of bill on a professional or dental claim, is rejected with 400.
    /// </summary>
    public string? BillType { get; init; }

    /// <summary>
    /// Place of service code (relevant for professional and dental claims). On an
    /// institutional claim it is not read for the facility decision.
    /// </summary>
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
    /// <summary>The claim type priced: the request's, or the one inferred when it sent none.</summary>
    public required ClaimType ClaimType { get; init; }

    /// <summary>The request's type of bill, normalized to three digits; absent when none was sent.</summary>
    public string? BillType { get; init; }
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

    /// <summary>CMS MPFS multiple procedure indicator (PPRRVU "MULT PROC"); null when not loaded.</summary>
    public int? MultipleProcedureIndicator { get; init; }
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

    /// <summary>
    /// CMS MPFS multiple procedure indicator from the PPRRVU "MULT PROC" column:
    /// 0 = no reduction, 2 = standard multiple surgery (100/50), 3 = endoscopy,
    /// 4 = diagnostic imaging, 5 = therapy, 6 = cardiovascular, 7 = ophthalmology,
    /// 9 = concept does not apply. Null when the source file did not supply it.
    /// </summary>
    public int? MultipleProcedureIndicator { get; init; }
}

// ─────────────────────────────────────────────────────────────
//  API Key / Tenant
// ─────────────────────────────────────────────────────────────

/// <summary>
/// An external customer's API key. The key itself is never stored: only its
/// SHA-256 (<see cref="KeyHash"/>, never serialized to a response) and a short
/// <see cref="KeyPrefix"/> that identifies it to a reader. The key is shown
/// once, in the response that creates it. Admin actions address a key by
/// <see cref="KeyId"/>, never by the key. Keys stored in plaintext before this
/// are hashed at startup (<c>MongoApiKeyRepository.InitializeAsync</c>).
/// </summary>
public record ApiKeyRecord
{
    /// <summary>Storage id (Mongo <c>_id</c>). Not part of any response.</summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [JsonIgnore]
    public string? Id { get; init; }

    /// <summary>Public identifier of the key (what admin actions take).</summary>
    public required string KeyId { get; init; }

    /// <summary>Lower-case hex SHA-256 of the key. Never returned.</summary>
    [JsonIgnore]
    public string KeyHash { get; init; } = string.Empty;

    /// <summary>The first 12 characters of the key ("cho_" and 8 hex), for identification.</summary>
    public required string KeyPrefix { get; init; }

    public required string TenantName { get; init; }
    public string? ContactEmail { get; init; }
    public required PricingTier Tier { get; init; }
    public int MonthlyLimit { get; init; }
    public int CurrentMonthUsage { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>Token subject of the platform admin who issued the key (null for keys issued before CHO tokens).</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Token subject of the platform admin who deactivated the key.</summary>
    public string? DeactivatedBy { get; init; }

    public DateTimeOffset? DeactivatedAt { get; init; }
}

/// <summary>What an admin sees of a key: never the key or its hash.</summary>
public record ApiKeyView
{
    public required string KeyId { get; init; }
    public required string KeyPrefix { get; init; }
    public required string TenantName { get; init; }
    public string? ContactEmail { get; init; }
    public required PricingTier Tier { get; init; }
    public int MonthlyLimit { get; init; }
    public int CurrentMonthUsage { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; init; }
    public string? CreatedBy { get; init; }
    public string? DeactivatedBy { get; init; }
    public DateTimeOffset? DeactivatedAt { get; init; }

    public static ApiKeyView From(ApiKeyRecord r) => new()
    {
        KeyId = r.KeyId,
        KeyPrefix = r.KeyPrefix,
        TenantName = r.TenantName,
        ContactEmail = r.ContactEmail,
        Tier = r.Tier,
        MonthlyLimit = r.MonthlyLimit,
        CurrentMonthUsage = r.CurrentMonthUsage,
        CreatedAt = r.CreatedAt,
        IsActive = r.IsActive,
        CreatedBy = r.CreatedBy,
        DeactivatedBy = r.DeactivatedBy,
        DeactivatedAt = r.DeactivatedAt,
    };
}

/// <summary>The response that creates a key: the only time the key itself is shown.</summary>
public record ApiKeyCreated
{
    /// <summary>The key. Shown once; only its hash is stored.</summary>
    public required string ApiKey { get; init; }
    public required ApiKeyView Key { get; init; }
}

public record UsageRecord
{
    /// <summary>Storage id, generated here (a string id is not generated by the driver).</summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; init; } = ObjectId.GenerateNewId().ToString();

    /// <summary>The <see cref="ApiKeyRecord.KeyId"/> of the key that was used, never the key.</summary>
    public required string KeyId { get; init; }
    public required string Endpoint { get; init; }
    public required int LineCount { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public int ResponseTimeMs { get; init; }
    public bool Success { get; init; }
}

// ─────────────────────────────────────────────────────────────
//  Enums
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Repricing claim type. Outpatient, Inpatient and Institutional are institutional (837I)
/// claims and always take the facility rate; Professional and Dental are priced by place of
/// service. New values are appended so existing numeric values keep their meaning.
/// </summary>
public enum ClaimType
{
    Professional,
    Outpatient,
    Inpatient,

    /// <summary>An institutional (837I) claim whose setting is not stated; see <see cref="RepricingRequest.BillType"/>.</summary>
    Institutional,

    /// <summary>A dental (837D) claim: priced like a professional claim, by place of service.</summary>
    Dental
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
